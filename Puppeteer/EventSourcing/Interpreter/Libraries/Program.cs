using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Puppeteer.EventSourcing.Follower;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{

    internal class Program : AST
    {
        private readonly DomainLibraries libraries;
        private readonly SymbolTable symbolTable;
		// B.1c: no longer readonly — ReleaseStatements() nulls it once the
		// compiled lambda (_executable) is built, to free the resolved AST of
		// cached compiled Actions. See ReleaseStatements for the invariant.
		private List<Statement> statements;
		private bool statementsReleased;
		private bool cachedHasSingleTellStatement;
		private bool cachedContainsTell;
		internal bool StatementsReleased => statementsReleased;

        private readonly bool programIsEval;
        internal Statement lastExecutedStatement;

        private List<Id> idAllReferences;
        private List<Id> idParameters;
		private List<Id> idDeclarations;
		private List<Id> externalDeclarationIds;

		private readonly int[] level;
        private readonly bool isQuery;
        private Parameters parameters;
		private ParameterSignature parameterSignature;
		private readonly bool isCheck;

		internal DateTime Now { get; set; }
		internal long EntryId { get; set; }
        internal string Script { get;}
		internal bool IsCompiledMode { get; private set; } = false;

		// Set by the Parser while building the Program: true iff the parse created some
		// EvalStatement. ValidateStatically reads it instead of doing
		// Collect<EvalStatement>() (a full traversal of the
		// AST per entry). In the rehydration journal there is not a single Eval
		// (they were computed and replaced by text before persisting), so the
		// flag stays false and ValidateStatically takes the full validation path
		// without paying the two traversals.
		internal bool HasEval { get; set; } = false;

		// Lever 1 of the Now optimization: true iff the script references the SYSTEM
		// parameter Now (as Id 'Now'/'@Now'), or conservatively if HasEval (an Eval can
		// synthesize the reference at execution time and it is not visible to the static
		// Collect<Id>). The Parser computes it after the parse (with the statements present) and
		// it travels cached with the Program in the operations cache. The framework only injects
		// Now on each live Perform when this flag is true: operations that do not use the
		// clock pay neither the box nor the set of Now. The journal's OccurredAt comes from the
		// Perform's local 'now', not from the parameter, so omitting the injection does not affect it. Computing
		// by NAME cannot under-inject: 'Now' is a reserved name (not declarable), so
		// the only way to reference it is to write Now/@Now, which Collect<Id> does see.
		internal bool ReferencesNow { get; set; }
		internal string LastExposeData { get; private set; }

		// Set by the interpreted Execute() after each run: true iff the execution
		// emitted an EWI of a blocking severity (Error/Warning). The check-then-command
		// flow reads it right after ExecuteCheck to decide whether the command is
		// conditioned, instead of the old "any output ⇒ block" rule (which wrongly
		// treated an advisory Information/Message as a failure).
		internal bool LastCheckBlocked { get; private set; }

		// B.1: AST property + Expression<Func<AST>> compiled delegate. The
		// Program IS the AST root (Program : AST). The AstFactory delegate is
		// built from an Expression tree that captures this parsed instance and
		// is compiled JIT on first access — replays and Reactions read the
		// AST via the delegate, never re-parsing the script text. Singleton
		// is viable because the AST is treated as immutable post-parse:
		// pattern matches live in caches on the Reaction, not on the AST.
		// Journal storage keeps the raw script text for human legibility; the
		// AST is the canonical machine-readable form.
		internal AST AST => this;

		private Func<AST> astFactory;
		private System.Linq.Expressions.Expression<Func<AST>> astFactoryExpression;
		internal System.Linq.Expressions.Expression<Func<AST>> AstFactoryExpression
		{
			get
			{
				if (astFactoryExpression == null)
				{
					astFactoryExpression = System.Linq.Expressions.Expression.Lambda<Func<AST>>(
						System.Linq.Expressions.Expression.Constant(this, typeof(AST)));
				}
				return astFactoryExpression;
			}
		}
		internal Func<AST> AstFactory
		{
			get
			{
				if (astFactory == null)
				{
					astFactory = AstFactoryExpression.Compile();
				}
				return astFactory;
			}
		}

		// Shared ParameterExpressions for globals referenced from this script's
		// compiled lambda. AllocateGlobalStorageExpression caches one per name
		// here so that every Id occurrence of the same global variable points
		// at the *same* ParameterExpression instance — the LambdaCompiler
		// matches variables by identity, so distinct ParameterExpressions with
		// the same Name would still fail "referenced from scope, not defined".
		private readonly Dictionary<string, ParameterExpression> globalStorageByName =
			new Dictionary<string, ParameterExpression>(StringComparer.OrdinalIgnoreCase);
		internal Dictionary<string, ParameterExpression> GlobalStorageByName => globalStorageByName;

		internal Program(DomainLibraries libraries, string script, SymbolTable symbolTable, List<Statement> statements, int [] level, bool isQuery, bool isCheck)
        {
            this.libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
            this.Script = script;
			this.statements = statements;
            this.symbolTable = symbolTable;
            this.programIsEval = symbolTable.InEvalMode;//Check that the Eval source cannot be used with the perform query
            this.level = level;
            this.isQuery = isQuery;
            this.parameters = Parameters.EMPTY;
            this.isCheck = isCheck;
        }

        internal int Level
        {
            get
            {
                return level.Length;
            }
        }

        // True iff the program is *exactly* one statement and that statement is
        // a TellStatement subclass. Plan 6 of the Tell primitive roadmap uses
        // this to gate ack-side pair elision: only single-tell entries are
        // safe to MarkAsSkip when the matching ack arrives, because the
        // elision API is entry-coarse and eliding a multi-statement entry
        // would discard non-tell siblings as collateral damage.
        internal bool HasSingleTellStatement
        {
            get
            {
                // B.1c: after ReleaseStatements the AST is gone; the bool was
                // snapshotted at release time (tell-elision runs on every
                // execution, including post-release compiled re-invocations).
                if (statementsReleased) return cachedHasSingleTellStatement;
                return statements.Count == 1 && statements[0] is TellStatement;
            }
        }

        // True iff the program contains any TellStatement (at any position).
        // A `tell` has no compiled-mode lowering, so AdjustCompilationMode uses
        // this to force the WHOLE program to interpreted execution. A tell-bearing
        // program is therefore never compiled and never released, so the live
        // scan below always has statements to read; the cached value only ever
        // backs released (compiled, tell-free) programs, where it is false.
        internal bool ContainsTell
        {
            get
            {
                if (statementsReleased) return cachedContainsTell;
                return ComputeContainsTell();
            }
        }

        private bool ComputeContainsTell()
        {
            if (statements == null) return false;
            for (int i = 0; i < statements.Count; i++)
            {
                if (statements[i] is TellStatement) return true;
            }
            return false;
        }

        internal bool IsQuery
        {
            get
            {
                return isQuery;
            }
        }

        internal bool IsCheck
        {
            get
            {
                return isCheck;
            }
        }

        internal Parameters Parameters
        {
            get
            {
                return parameters;
            }
            set
            {
                this.parameters = value;
            }
        }

		internal List<Id> ExternalDeclarations
		{
			get 
			{
				if (externalDeclarationIds == null)
					return new List<Id>();
				return externalDeclarationIds;
			}
			set 
			{
				if (value == null) throw new LanguageException("External declarations can not be null");

				externalDeclarationIds = value;
			}
		}

		internal string GetCommandErrorLine ()
        {
            return lastExecutedStatement == null ? "" : lastExecutedStatement.ToString();
        }

		internal void AdjustCompilationMode(bool useInterpretedMode, CompilationModePolicy compilationMode)
		{
			switch (compilationMode)
			{
				case CompilationModePolicy.Automatic:
					if (IsCompiledMode) throw new LanguageException("The Program is already in compiled execution mode.");

					// A `tell` has no compiled-mode lowering, so a tell-bearing
					// program runs interpreted. This is per-program: the tell lives in
					// a Reaction's Causation body (post-commit, not the actor's hot
					// command path), so the actor's other programs keep compiling —
					// no global interpreted regression.
					IsCompiledMode = !useInterpretedMode && !ContainsTell;
					break;
				case CompilationModePolicy.AlwaysCompiled:
					// Even under an explicit AlwaysCompiled policy a tell cannot be
					// compiled; fall back to interpreted for the tell-bearing program
					// rather than failing at write-time. AlwaysCompiled exists only to
					// pin unit-test execution mode.
					IsCompiledMode = !ContainsTell;
					break;
				case CompilationModePolicy.AlwaysInterpreted:
					IsCompiledMode = false;
					break;
				default:
					throw new LanguageException($"Compilation mode policy '{compilationMode}' is not recognized.");
			}
		}

		private Func<Parameters, Output, string> _executable;
		internal string ExecuteExpression(Parameters parameters)
		{
			if (_executable == null)
			{
				this.SolveReferences(parameters, withStaticValidation: true);

				var programExpression = this.ProgramExpression();
				var sw = LabInstrumentation.OnCompileElapsedTicks != null ? System.Diagnostics.Stopwatch.StartNew() : null;
				_executable = programExpression.Compile();
				if (sw != null) { sw.Stop(); LabInstrumentation.OnCompileElapsedTicks(sw.ElapsedTicks); }

				this.idParameters = null;
			}
			else
			{
				this.SolveParameters(parameters);
			}

			var output = (symbolTable.RecoveringState) ? Output.RentWithoutOutput() : Output.RentWithOutput();
			var result = _executable(parameters, output);

			Output.Return(output);
			parameters.Clear();	

			return result;
		}

		// B.1c: drop the resolved AST of a compiled Program once its lambda is
		// built. Memory win for the unbounded actionCommands cache: each cached
		// compiled Action retains a full resolved AST (fat Id nodes with
		// ForcedType / parameter / symbol / storage-expression refs) that is
		// dead weight after compilation — execution runs through _executable,
		// the journal needs only Script + Parameters (both retained), and the
		// canonical text is preserved in builderStr.
		//
		// Invariant: release ONLY when IsCompiledMode && _executable != null.
		// Interpreted Programs (V1 scripts) run Execute() which walks statements
		// on EVERY invocation, so they must keep it — but interpreted Programs
		// are never cached in actionCommands (they are ephemeral per-event), so
		// this gate effectively targets V2 Actions and promoted Actions.
		//
		// Pattern matching is unaffected: Reactions re-parse entry.Script into
		// their own per-Reaction Program copy (Reaction.SolveActionReferences),
		// never touching this instance's statements.
		internal void ReleaseStatements(DatabaseType databaseType)
		{
			if (statementsReleased) return;
			if (!IsCompiledMode || _executable == null) return;
			if (statements == null) return;

			// Warm the canonical-text cache before dropping the AST it renders from.
			_ = ConvertToString(databaseType);
			// Preserve the cheap shape queries that must outlive the AST. (A
			// tell-bearing program is forced interpreted and never reaches here, so
			// cachedContainsTell is only ever snapshotted false — but keep it honest.)
			cachedHasSingleTellStatement = statements.Count == 1 && statements[0] is TellStatement;
			cachedContainsTell = ComputeContainsTell();

			// Drop the Statement tree and the all-references list. idDeclarations
			// is kept (small, and Declarations may be queried) — the bulk freed
			// is the Statement nodes plus the Ids reachable only via idAllReferences.
			statements = null;
			idAllReferences = null;
			statementsReleased = true;
		}

		internal Expression<Func<Parameters, Output, string>> ProgramExpression()
		{
			var parametersParam = Expression.Parameter(typeof(Parameters), "_$_context_parameters");
			var outputParam = Expression.Parameter(typeof(Output), "_$_context_output");

			List<Expression> cmds = new List<Expression>();

			Expression inicializar = Expression.Call(
				outputParam,
				typeof(Output).GetMethod(nameof(Output.Clear), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, Array.Empty<Type>())
			);

			cmds.Add(inicializar);

			var referencedParams = this.idParameters
				.Select(id => id.Parameter)
				.Distinct();

			var referencedParamsAndGlobalVarDeclationsExp = new List<ParameterExpression>();
			foreach (Parameter referencedParameter in referencedParams)
			{
				referencedParamsAndGlobalVarDeclationsExp.Add(referencedParameter.ParameterDeclarationExpression());
				cmds.Add(referencedParameter.ParameterInitializationExpression());
			}

			foreach (Statement source in statements)
			{
				cmds.Add(source.ExecuteExpression(parametersParam, outputParam));
			}

			foreach (var id in this.idParameters)
			{
				id.ReleaseLocalParameter();
			}

			var referencedGlobalVars = this.idAllReferences
				.Where(id => id.IsGlobalVariable && id.LValueStorageExpression != null)
				.GroupBy(id => id.Name, StringComparer.OrdinalIgnoreCase)
				.Select(g => g.First());

			foreach (Id referencedGlobal in referencedGlobalVars)
			{
				referencedParamsAndGlobalVarDeclationsExp.Add((ParameterExpression) referencedGlobal.LValueStorageExpression);
			}

			Expression finalizar = Expression.Call(
				outputParam,
				typeof(Output).GetMethod(nameof(Output.Finish), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, Array.Empty<Type>())
			);
			cmds.Add(finalizar);

			Expression toStr = Expression.Call(
				outputParam,
				typeof(Output).GetMethod(nameof(Output.ToString), Array.Empty<Type>())
			);
			cmds.Add(toStr);

			BlockExpression blockExpr = Expression.Block(
				referencedParamsAndGlobalVarDeclationsExp,
				cmds
			);

			var lambda = Expression.Lambda<Func<Parameters, Output, string>>(blockExpr, parametersParam, outputParam);

			return lambda;
		}

		private string Execute(bool clearParameters)
        {
			ExecutionOutput output = (symbolTable.RecoveringState) ? ExecutionOutput.RentWithoutOutput() : ExecutionOutput.RentWithOutput();
			output.Clear();
            foreach (Statement source in statements)
            {
                this.lastExecutedStatement = source;
                source.Execute(output);
            }
            output.Finish();
            string result = output.ToString();
			string exposeJson = output.GetExposeJson();
			LastExposeData = string.IsNullOrEmpty(exposeJson) ? null : exposeJson;
			// Capture BEFORE Return (which clears the buffers): only Error/Warning
			// EWIs condition the command; an Information/Message does not.
			LastCheckBlocked = output.HasBlockingEWI();

			ExecutionOutput.Return(output);
			if (clearParameters || output.HasEWIS())
			{
				parameters.Clear();
            }

            return result;
        }

		internal string Execute()
        {
            return Execute(clearParameters: true);
        }

        private string ExecuteEval()
        {
            return Execute(false);
        }

        internal string ExecuteCheck()
        {
            return Execute(false);
        }

        internal void LoadArguments(Parameters arguments, bool recomputeEvalParameters = true)
        {
			if (!this.IsCompiledMode || _executable == null)
			{
				this.parameters = arguments;
			}

			// Eval parameters are computed transactionally at COMMAND time and their result
			// is journaled with the invocation. Recomputing them means EXECUTING the eval
			// expression (e.g. `company.Sales.DomainFrom(host)`) — which is correct at command
			// time but WRONG on a replay/observer that only carries the arguments blob and a
			// type-seeded (value-less) symbol table: the expression dereferences a null global
			// and throws. When recomputeEvalParameters is false (the Reaction matcher path),
			// keep the journaled value already loaded into `arguments` instead of re-executing.
			if (!recomputeEvalParameters) return;

			foreach (var argument in arguments)
            {
                if (argument.ParameterModifier == Parameter.Eval)
                {
                    var resultEval = EvaluateEvalParameters(argument);
                    argument.Value = resultEval;
                }
            }
        }


		private Dictionary<string, (string EvalScript, Func<Parameters, Output, string> Executable)> _executableEvalParameter = new Dictionary<string, (string, Func<Parameters, Output, string>)>();
		private Program CreateEvalProgram(Parameter parameter)
		{
			Parser parser = new Parser(this.libraries, this.symbolTable);
			parser.SetSource(parameter.EvalScript);
			var evalProgram = parser.Parse(isQuery: false, isCheck: false);
			evalProgram.SetContextInfo();
			evalProgram.AdjustCompilationMode(useInterpretedMode: false, CompilationModePolicy.Automatic);
			evalProgram.Parameters = this.parameters;
			evalProgram.SolveReferences(this.parameters, withStaticValidation: true);
			parameter.Program = evalProgram;
			parameter.Program.Parameters = this.parameters;
			return evalProgram;
		}

		private object EvaluateEvalParameters(Parameter parameter)
		{
			var evalScript = parameter.EvalScript;
			if (!_executableEvalParameter.TryGetValue(parameter.Name, out var evalParameterCacheEntry) || evalParameterCacheEntry.EvalScript != evalScript)
			{
				Program evalProgram = CreateEvalProgram(parameter);
				var programExpression = parameter.Program.ProgramExpression();
				var swEval = LabInstrumentation.OnEvalCompileElapsedTicks != null ? System.Diagnostics.Stopwatch.StartNew() : null;
				var executable = programExpression.Compile();
				if (swEval != null) { swEval.Stop(); LabInstrumentation.OnEvalCompileElapsedTicks(swEval.ElapsedTicks); }
				evalParameterCacheEntry = (evalScript, executable);
				_executableEvalParameter[parameter.Name] = evalParameterCacheEntry;
				parameter.Program.idParameters = null;
			}
			var output = Output.RentWithoutOutput();
			evalParameterCacheEntry.Executable(this.parameters, output);
			Output.Return(output);

			return parameter.GetValue();
		}

        private string builderStr = null;
        internal string ConvertToString(DatabaseType databaseType)
        {
            if (builderStr != null) return builderStr;

            StringBuilder builder = new StringBuilder();
            foreach (Statement source in statements)
            {
                source.Write(builder, 0, databaseType);
            }
            builderStr = builder.ToString();
            return builderStr;
        }

        // Authored render of the program body: identical to ConvertToString
        // except that filtered print statements are kept (see AuthoredRenderScope
        // and OutputStatementIndividual.Write). Used only to compose the once-
        // written Action (Define) body text, so the developer's prints survive in
        // the journal. Deliberately NOT cached and NOT sharing builderStr: the
        // canonical render remains the cache key and the payload for repeated
        // Script rows. The render is synchronous, so the ambient scope enters and
        // exits within this call.
        internal string ConvertToAuthoredString(DatabaseType databaseType)
        {
            StringBuilder builder = new StringBuilder();
            using (AuthoredRenderScope.Enter())
            {
                foreach (Statement source in statements)
                {
                    source.Write(builder, 0, databaseType);
                }
            }
            return builder.ToString();
        }

        // ConvertToString caches builderStr on the first call. ActorHandler
        // invokes that first render in PrepareCommand, BEFORE Perform — for a
        // program with Eval that render is the LITERAL form `Eval(<expr>);` because
        // EvalStatement.forDairy is still null. After executing (when each executed
        // Eval has populated its forDairy with the evaluated assignment), ActorHandler
        // invalidates this cache to re-render the EVALUATED form and journal it
        // (determinism on replay). Only used in the Eval path (HasEval).
        internal void InvalidateDairyRenderCache()
        {
            builderStr = null;
        }

        internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
        {
            foreach (Statement source in statements)
            {
                source.PreparePatternMatching(patternAst, ref position);
            }
        }

		// B.3.1: promotion-candidate structural hash override. Walks the
		// top-level statements to mix their contributions; descendant
		// Statement/Expression subclasses override AccumulatePromotionCandidateHash
		// to propagate structure while holding literal values blind. Cached
		// on first read since the AST is treated as immutable post-parse.
		internal override void AccumulatePromotionCandidateHash(ref HashCode hc)
		{
			hc.Add(nameof(Program));
			hc.Add(statements.Count);
			foreach (Statement source in statements)
			{
				source.AccumulatePromotionCandidateHash(ref hc);
			}
		}

		private int promotionCandidateHash;
		private bool promotionCandidateHashComputed;
		internal int PromotionCandidateHash
		{
			get
			{
				if (!promotionCandidateHashComputed)
				{
					HashCode hc = new HashCode();
					AccumulatePromotionCandidateHash(ref hc);
					promotionCandidateHash = hc.ToHashCode();
					promotionCandidateHashComputed = true;
				}
				return promotionCandidateHash;
			}
		}

        internal PatternMatcher CreatePatternMatcher(ActorHandler.ConcurrentParametersPool parametersPool)
        {
            ArgumentNullException.ThrowIfNull(parametersPool);

            return new PatternMatcher(this, parametersPool);
        }

        internal override void Visit(ASTVisitor v)
        {
            if (this.GetType() == v.Target)
            {
                v.OnVisit(this);
            }
            foreach (Statement source in statements)
            {
                source.Visit(v);
            }
        }

		internal void SolveReferences(Parameters initialParameterSet, bool withStaticValidation)
		{
			if (initialParameterSet == null) throw new ArgumentNullException(nameof(initialParameterSet));
			if (!withStaticValidation && this.parameterSignature != null) throw new LanguageException("The program references have already been resolved; they cannot be resolved again.");

			var solver = new ReferencesSolver(this, initialParameterSet);
			solver.SolveIdReferences();
			if (withStaticValidation) ValidateStatically();
			this.parameterSignature = solver.ParameterSignature();
			this.idAllReferences = solver.IdAllReferences().ToList();
			this.idParameters = solver.IdsParameter().ToList();
			this.idDeclarations = solver.IdDeclarations().ToList();
		}

        internal void SolveParameters(Parameters parameters)
        {
			if (this.parameterSignature != null && ! this.parameterSignature.IsCompatible(parameters))
			{
				throw new LanguageException("The provided parameters are not compatible with the Program's parameter signature.");
			}
			if (!IsCompiledMode && this.idParameters != null)
			{
				foreach (var id in this.idParameters)
				{
					if (parameters.ContainsParameter(id.Name) && id.Parameter != null)
					{
						id.DeclareAsLocalParameter(parameters[id.Name]);
					}
					else
					{
						throw new LanguageException($"Parameter '{id.Name}' was not provided.");
					}
				}
			}
		}

		internal void ValidateStatically()
		{
			bool hasEvals = this.HasEval;
			if (! hasEvals)
			{
				foreach (var source in this.statements)
				{
					source.ValidateStatically();
				}
			}
			else
			{
				// Best-effort: when there is Eval we omit the full static validation
				// (the identifiers synthesized by Eval are not known at resolve time), but
				// we propagate the declared type of each global assigned via NewInstanceStatement
				// whose rValue.ComputeType() resolves without touching the Eval path. Without this, the
				// Id.ForcedType setter never runs for an assignment like `g = Base(obj);` and
				// SymbolTable.Entry("g").type stays null when this entry's resolver task finishes.
				// If a later journal entry references that global as an RValue,
				// the resolver cannot assign it ForcedType (the resolution requires
				// symbol.type != null) and the static validation of the later entry falls into
				// DotAccess.ComputeCallExpressionType with instanceClass==null. The production
				// symptom is the resolver task logging NRE/LanguageException for each entry
				// dependent on the global.
				foreach (var statement in this.statements.OfType<NewInstanceStatement>())
				{
					if (statement.LValue is Id id && id.IsOriginalLValueDeclaration && id.ForcedType == null)
					{
						Type t;
						try { t = statement.RValue.ComputeType(); }
						catch { t = null; }
						if (t != null) id.ForcedType = t;
					}
				}
			}
		}


        internal void SetContextInfo()
        {
			foreach (var source in this.statements)
			{
				source.Program = this;
			}
		}

		// Lever 1 of the Now optimization: scans the program's Ids only once (at
		// parse-time, statements present) looking for a reference to the SYSTEM parameter
		// Now. Reuses the same Collect<Id> that ReferencesSolver uses as the canonical view of
		// all ids, so it is as complete as the reference resolution. Normalizes
		// the '@' alias ('@Now' is an alias of 'Now') by span without allocating. The caller
		// (Parser) combines it with HasEval for the conservative case.
		internal bool ScriptReferencesSystemNow()
		{
			foreach (Id id in this.Collect<Id>())
			{
				ReadOnlySpan<char> name = id.Name.AsSpan();
				if (name.Length > 0 && name[0] == '@') name = name.Slice(1);
				if (name.Equals(Parameters.SystemNowName.AsSpan(), StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}

        internal List<Id> Declarations
        {
            get
            {
				if (idDeclarations == null) 
					return new List<Id>();
                return idDeclarations;
            }
        }

        class ReferencesSolver
        {
            private readonly List<Id> allDeclarations;
            private readonly List<Id> localDeclarations;
            private readonly List<Id> allIds;
            private readonly HashSet<Id> parametersIds;
			private readonly SymbolTable symbolTable;
			private readonly bool isQuery;

			internal ReferencesSolver(Program program, Parameters initialParameterSet)
            {
				this.symbolTable = program.symbolTable;
				this.isQuery = program.IsQuery;

				// Collect LValues from assignments, excluding those that already exist as global variables
				var lValuesFromAssignments = program.Collect<NewInstanceStatement>()
					.Where(x => x.LValue is Id)
					.Select(x => (Id)x.LValue)
					.Where(id => !symbolTable.HasVariable(id.Name))  // Filter out existing global variables
					.ToList();

                var forVariables = program.Collect<ForEachStatement>().Where(x => x.Variable != null).Select(x => (Id)x.Variable);
                var forIndexVariables = program.Collect<ForEachStatement>()
                    .Where(x => x.IndexVariable != null)
                    .Select(x => (Id)x.IndexVariable);
                localDeclarations = lValuesFromAssignments
                    .Union(forVariables)
                    .Union(forIndexVariables)
                    .OrderBy(x => x.Level).ToList();
                allDeclarations = program.ExternalDeclarations.Count > 0 ? program.ExternalDeclarations.Union(localDeclarations).ToList() : localDeclarations;
                allIds = program.Collect<Id>().ToList();
				parametersIds = new HashSet<Id>();

				// First, process LValues that are parameters AND exist in the symbol table
				// (those that don't exist in the table will be processed in the allDeclarations loop)
				var parameterLValues = program.Collect<NewInstanceStatement>()
					.Where(x => x.LValue is Id)
					.Select(x => (Id)x.LValue)
					.Where(id => initialParameterSet.ContainsParameter(id.Name) && symbolTable.HasVariable(id.Name))
					.ToList();

				foreach (var paramLValue in parameterLValues)
				{
					paramLValue.Program = program;
					paramLValue.DeclareAsLocalParameter(initialParameterSet[paramLValue.Name]);
					paramLValue.MarkAsLValue();
					parametersIds.Add(paramLValue);
				}

				// Second, process LValues that are references to existing global variables
				var globalLValues = program.Collect<NewInstanceStatement>()
					.Where(x => x.LValue is Id)
					.Select(x => (Id)x.LValue)
					.Where(id => symbolTable.HasVariable(id.Name) && !initialParameterSet.ContainsParameter(id.Name))
					.ToList();

				foreach (var globalLValue in globalLValues)
				{
					globalLValue.Program = program;
					RejectGlobalDeclarationInQuery(globalLValue);
					globalLValue.DeclareAsGlobalVariable();
					globalLValue.MarkAsLValue();
					var symbol = symbolTable.Entry(globalLValue.Name);
					if (symbol != null && symbol.type != null)
					{
						globalLValue.ForcedType = symbol.type;
					}
				}

				var programLevel = program.Level;
                foreach (var id in allDeclarations)
                {
					id.Program = program;
					if (initialParameterSet.ContainsParameter(id.Name))
                    {
						parametersIds.Add(id);
						id.DeclareAsLocalParameter(initialParameterSet[id.Name]);
                    }

					if (id.Level == 0 && !id.IsParameter)
                    {
                        RejectGlobalDeclarationInQuery(id);
                        id.DeclareAsGlobalVariable();
                    }
                    else if (id.Level >= programLevel && !id.IsGlobalVariable && !id.IsParameter)
                    {
                        id.DeclareAsLocalVariable();
                    }

					id.MarkAsLValue();
                }

				// Eval re-declaration unification:
				// EvalStatement.Execute re-enters SolveReferences passing the
				// inner program's declarations as the parent's ExternalDeclarations
				// (and vice versa when parsing the next Eval). At the TOP-LEVEL
				// the HasVariable filter avoids the problem because the eval's x stays
				// Global. Inside a block the x is Local (IsolatedStorage), does not
				// enter the SymbolTable and each successive Eval('x = ...;') produces a
				// new x_evalN in localDeclarations. Without unifying, parent ends up with
				// two distinct OriginalLValueDeclaration for the same name and, when
				// re-resolving after the second Eval, ReferencesTo tries to rebind the
				// x RValue already bound to x_eval1 toward x_eval2 and throws "ambigous
				// declaration". Here we detect that case and unify the local with
				// the external: the inner Eval's assignment ends up writing to the
				// same symbol that the outer block's reads already see.
				if (program.ExternalDeclarations.Count > 0)
				{
					var externalsByName = program.ExternalDeclarations
						.Where(ext => ext.IsLValue && ext.IsOriginalLValueDeclaration)
						.ToLookup(ext => ext.Name, StringComparer.OrdinalIgnoreCase);
					var unified = new List<Id>();
					foreach (var localLValue in localDeclarations)
					{
						if (!localLValue.IsLValue) continue;
						if (!localLValue.IsOriginalLValueDeclaration) continue;
						var matchingExternal = externalsByName[localLValue.Name]
							.FirstOrDefault(ext => ext != localLValue && ext.IsReferencedBy(localLValue));
						if (matchingExternal != null)
						{
							localLValue.ReferencesTo(matchingExternal);
							unified.Add(localLValue);
						}
					}
					foreach (var localLValue in unified)
					{
						allDeclarations.Remove(localLValue);
					}
				}

                foreach (var id in allIds)
                {
					id.Program = program;
					if (initialParameterSet.ContainsParameter(id.Name))
                    {
                        id.DeclareAsLocalParameter(initialParameterSet[id.Name]);
						parametersIds.Add(id);
					}

					if (! id.IsLValue)
					{
						id.MarkAsRValue();
					}
                }

				// program.HasEval avoids the Collect<EvalStatement>() (a full traversal of the
				// AST) when the Program has no evals — the case of all the journal's scripts
				// during rehydration. If there are no evals the foreach would do nothing
				// anyway, but the Collect still walked the whole tree.
				if (! program.IsCompiledMode && program.HasEval)
				{
					foreach(var eval in program.Collect<EvalStatement>())
					{
						eval.Program = program;
					}
				}
            }

            internal void SolveIdReferences()
            {
                SolveReferencesToLValues();
                SolveReferencesToRValues();
            }

            internal void SolveReferencesToLValues()
            {
					for(int declIdx = 0; declIdx < localDeclarations.Count; declIdx++)
					{
						Id declaration = localDeclarations[declIdx];

						if (declaration.IsOriginalLValueDeclaration)
						{
							for (int refIdx = declIdx + 1; refIdx < localDeclarations.Count; refIdx++)
							{
								Id reference = localDeclarations[refIdx];
								if (declaration.IsReferencedBy(reference))
								{
									reference.ReferencesTo(declaration);
								}
							}

							if (!declaration.IsParameter)
							{
								var references = allIds.Where(
									reference =>
										reference != declaration &&
										!reference.IsLValue && 
										!reference.IsParameter &&
										string.Equals(reference.Name, declaration.Name, StringComparison.OrdinalIgnoreCase) &&
										declaration.IsReferencedBy(reference)
								);
								foreach (Id reference in references)
								{
									reference.ReferencesTo(declaration);
								}
							}
						}
					}
            }

            internal void SolveReferencesToRValues()
            {
                var ids = allIds;
                foreach (Id declaration in allDeclarations)
                {
					if (declaration.IsOriginalLValueDeclaration && !declaration.IsParameter)
					{
						var references = ids.Where(
							reference =>
								string.Equals(reference.Name, declaration.Name, StringComparison.OrdinalIgnoreCase) &&
								! reference.IsLValue &&
								declaration.IsReferencedBy(reference)
						);
						foreach (Id reference in references)
						{
							reference.ReferencesTo(declaration);
						}
					}
                }
				foreach (Id reference in allIds)
				{
					if (! reference.IsLValue && ! reference.IsParameter && ! reference.IsLocalVariable)
					{
						if (this.symbolTable.HasVariable(reference.Name))
						{
							reference.DeclareAsGlobalVariable();
							reference.MarkAsRValue();
							var symbol = symbolTable.Entry(reference.Name);
							if (symbol != null && symbol.type != null)
							{
								reference.ForcedType = symbol.type;
							}
						}
					}
				}
            }

			// A query is read-only and never journaled, so it must not declare or write a
			// top-level global variable — that would mutate actor state. This is the rule
			// the parser used to enforce eagerly with "Global variable declarations are not
			// allowed in queries", but the parser cannot tell a new-global declaration from
			// an assignment to a pre-declared @Out parameter: the Lexer drops the '@' alias
			// and the parameter set is unknown until reference resolution. By the time this
			// runs each LValue's scope is resolved, so an Out/InOut parameter LValue is
			// Scope.Parameter (declared earlier in the ctor) and never reaches the two
			// DeclareAsGlobalVariable sites that call this — only genuine globals do.
			// Restricted to Level 0 to match the original guard, which only fired at the
			// top level (currLevel.Length == 0); assignments nested in a block were never
			// rejected here.
			private void RejectGlobalDeclarationInQuery(Id lValue)
			{
				if (isQuery && lValue.Level == 0)
				{
					throw new LanguageException($"Global variable declarations are not allowed in queries. Cannot assign to the global variable '{lValue.Name}' in a query because queries are read-only. If '{lValue.Name}' is meant to receive a computed result, declare it as an output parameter (Parameter.Out) and reference it as '@{lValue.Name}'.");
				}
			}

			internal IEnumerable<Id> IdDeclarations()
			{
				return allDeclarations;
			}

			internal IEnumerable<Id> IdAllReferences()
			{
				return allIds;
			}

			internal IEnumerable<Id> IdsParameter()
			{
				return parametersIds;
			}

			internal ParameterSignature ParameterSignature()
			{
				var referencedParams = this.allIds
					.Where(id => id.IsParameter)
					.Select(id => id.Parameter)
					.Distinct();

				var result = new ParameterSignature(referencedParams);
				return result;
			}
        }

        class VariableNamesCollector : ASTVisitor
        {
            private readonly List<string> ids = new List<string>();


            internal VariableNamesCollector(CallStatement call) : base(call, typeof(Id))
            {
            }

            internal VariableNamesCollector(AstExpression exp) : base(exp, typeof(Id))
            {
            }

            internal override void OnVisit(AST node)
            {
                Id id = (Id)node;
                var name = id.Name.ToLower();
                if (!ids.Contains(name)) ids.Add(name);
            }

            internal IEnumerable<string> GetAll()
            {
                return ids;
            }
        }
    }
}
