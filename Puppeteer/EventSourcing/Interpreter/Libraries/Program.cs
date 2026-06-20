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
		internal bool StatementsReleased => statementsReleased;

        private readonly bool elProgramaEsUnEval;
        internal Statement lastExecutedStatement;

        private List<Id> idAllReferences;
        private List<Id> idParameters;
		private List<Id> idDeclarations;
		private List<Id> idDeclaracionesExternas;

		private readonly int[] level;
        private readonly bool isQuery;
        private Parameters parameters;
		private ParameterSignature parameterSignature;
		private readonly bool isCheck;

		internal DateTime Now { get; set; }
		internal long EntryId { get; set; }
        internal string Script { get;}
		internal bool IsCompiledMode { get; private set; } = false;

		// Lo setea el Parser al construir el Program: true sii el parse creo algun
		// EvalStatement. ValidateStatically lo lee en vez de hacer
		// Collect<EvalStatement>() (un recorrido completo del
		// AST por cada entry). En el journal de rehidratacion no hay un solo Eval
		// (se calcularon y sustituyeron por texto antes de persistir), asi que el
		// flag queda false y ValidateStatically toma el camino de validacion completa
		// sin pagar los dos traversals.
		internal bool HasEval { get; set; } = false;

		// Lever 1 de la optimizacion de Now: true sii el script referencia el parametro de
		// SISTEMA Now (como Id 'Now'/'@Now'), o conservadoramente si HasEval (un Eval puede
		// sintetizar la referencia en tiempo de ejecucion y no es visible al Collect<Id>
		// estatico). Lo computa el Parser tras el parse (con los statements presentes) y
		// viaja cacheado con el Program en el cache de operaciones. El framework solo inyecta
		// Now en cada Perform en vivo cuando este flag es true: las operaciones que no usan el
		// reloj no pagan el box ni el set de Now. OccurredAt del journal sale del 'now' local
		// del Perform, no del parametro, asi que omitir la inyeccion no lo afecta. Calcular
		// por NOMBRE no puede sub-inyectar: 'Now' es nombre reservado (no declarable), de modo
		// que la unica forma de referenciarlo es escribir Now/@Now, que Collect<Id> si ve.
		internal bool ReferencesNow { get; set; }
		internal string LastExposeData { get; private set; }

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
            this.elProgramaEsUnEval = symbolTable.InEvalMode;//Revisar que el source Eval no se puede usar con el perfomqry
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

		internal List<Id> DeclaracionesExternas
		{
			get 
			{
				if (idDeclaracionesExternas == null)
					return new List<Id>();
				return idDeclaracionesExternas;
			}
			set 
			{
				if (value == null) throw new LanguageException("External declarations can not be null");

				idDeclaracionesExternas = value;
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

					IsCompiledMode = !useInterpretedMode;
					break;
				case CompilationModePolicy.AlwaysCompiled:
					IsCompiledMode = true;
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
			// Preserve the cheap shape query that must outlive the AST.
			cachedHasSingleTellStatement = statements.Count == 1 && statements[0] is TellStatement;

			// Drop the Statement tree and the all-references list. idDeclarations
			// is kept (small, and Declaraciones may be queried) — the bulk freed
			// is the Statement nodes plus the Ids reachable only via idAllReferences.
			statements = null;
			idAllReferences = null;
			statementsReleased = true;
		}

		internal Expression<Func<Parameters, Output, string>> ProgramExpression()
		{
			var parametersParam = Expression.Parameter(typeof(Parameters), "_$_context_parametros");
			var outputParam = Expression.Parameter(typeof(Output), "_$_context_salida");

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

		private string Execute(bool limpiarParametros)
        {
			ExecutionOutput output = (symbolTable.RecoveringState) ? ExecutionOutput.RentWithoutOutput() : ExecutionOutput.RentWithOutput();
			output.Clear();
            foreach (Statement source in statements)
            {
                this.lastExecutedStatement = source;
                source.Execute(output);
            }
            output.Finish();
            string resultado = output.ToString();
			string exposeJson = output.GetExposeJson();
			LastExposeData = string.IsNullOrEmpty(exposeJson) ? null : exposeJson;

			ExecutionOutput.Return(output);
			if (limpiarParametros || output.HasEWIS())
			{
				parameters.Clear();
            }

            return resultado;
        }

		internal string Execute()
        {
            return Execute(limpiarParametros: true);
        }

        private string EjecutarEval()
        {
            return Execute(false);
        }

        internal string ExecuteCheck()
        {
            return Execute(false);
        }

        internal void LoadArguments(Parameters arguments)
        {
			if (!this.IsCompiledMode || _executable == null)
			{
				this.parameters = arguments;
			}

			foreach (var argument in arguments)
            {
                if (argument.ParameterModifier == Parameter.Eval)
                {
                    var resultEval = EvaluateEvalParameters(argument);
                    argument.Value = resultEval;
                }
            }
        }


		private Dictionary<string, (string EvalScript, Func<Parameters, Output, string> Ejecutable)> _ejecutableEvalParameter = new Dictionary<string, (string, Func<Parameters, Output, string>)>();
		private Program CrearProgramaEval(Parameter parametro)
		{
			Parser parser = new Parser(this.libraries, this.symbolTable);
			parser.SetSource(parametro.EvalScript);
			var programaEval = parser.Parse(isQuery: false, isCheck: false);
			programaEval.SetContextInfo();
			programaEval.AdjustCompilationMode(useInterpretedMode: false, CompilationModePolicy.Automatic);
			programaEval.Parameters = this.parameters;
			programaEval.SolveReferences(this.parameters, withStaticValidation: true);
			parametro.Program = programaEval;
			parametro.Program.Parameters = this.parameters;
			return programaEval;
		}

		private object EvaluateEvalParameters(Parameter parametro)
		{
			var evalScript = parametro.EvalScript;
			if (!_ejecutableEvalParameter.TryGetValue(parametro.Name, out var evalParameterCacheEntry) || evalParameterCacheEntry.EvalScript != evalScript)
			{
				Program programaEval = CrearProgramaEval(parametro);
				var programExpression = parametro.Program.ProgramExpression();
				var swEval = LabInstrumentation.OnEvalCompileElapsedTicks != null ? System.Diagnostics.Stopwatch.StartNew() : null;
				var executable = programExpression.Compile();
				if (swEval != null) { swEval.Stop(); LabInstrumentation.OnEvalCompileElapsedTicks(swEval.ElapsedTicks); }
				evalParameterCacheEntry = (evalScript, executable);
				_ejecutableEvalParameter[parametro.Name] = evalParameterCacheEntry;
				parametro.Program.idParameters = null;
			}
			var output = Output.RentWithoutOutput();
			evalParameterCacheEntry.Ejecutable(this.parameters, output);
			Output.Return(output);

			return parametro.GetValue();
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

        // ConvertToString cachea builderStr en la primera llamada. ActorHandler
        // invoca ese primer render en PrepareCommand, ANTES de Perform — para un
        // programa con Eval ese render es la forma LITERAL `Eval(<expr>);` porque
        // EvalStatement.forDairy aun es null. Tras ejecutar (cuando cada Eval
        // ejecutado ya poblo su forDairy con la asignacion evaluada), ActorHandler
        // invalida este cache para re-renderizar la forma EVALUADA y journalizarla
        // (determinismo en replay). Solo se usa en el path Eval (HasEval).
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
			if (!withStaticValidation && this.parameterSignature != null) throw new LanguageException("Ya se han resuelto las referencias del program, no se pueden resolver nuevamente.");

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
				// Best-effort: cuando hay Eval omitimos la validacion estatica completa
				// (los identifiers sintetizados por Eval no se conocen en tiempo de resolve), pero
				// propagamos el tipo declarado de cada global asignada via NewInstanceStatement
				// cuyo rValue.ComputeType() resuelve sin tocar el path de Eval. Sin esto, el
				// setter Id.ForcedType nunca corre para `g = Guardian(company);` y
				// SymbolTable.Entry("g").type queda en null al terminar el resolverTask de esta
				// entry. Si una entry posterior del journal referencia ese global como RValue,
				// el resolver no le puede asignar ForcedType (Program.cs:667-670 requiere
				// symbol.type != null) y la static validation de la entry posterior cae en
				// DotAccess.ComputeCallExpressionType con instanceClass==null. El symptom
				// produccion es resolverTask logueando NRE/LanguageException por cada entry
				// dependiente del global. Documentado en
				// BUG_RehydrationStaticValidation_AccountsCreate_ExchangeAPI §4.1, §4.2, §7.2.
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

		// Lever 1 de la optimizacion de Now: escanea los Id del programa una sola vez (en
		// parse-time, statements presentes) buscando una referencia al parametro de SISTEMA
		// Now. Reusa el mismo Collect<Id> que ReferencesSolver usa como vista canonica de
		// todos los ids, asi que es tan completo como la resolucion de referencias. Normaliza
		// el alias '@' (CLAUDE.md: '@Now' es alias de 'Now') por span sin asignar. El llamador
		// (Parser) combina con HasEval para el caso conservador.
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

        internal List<Id> Declaraciones
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
            private readonly List<Id> todasLasDeclaraciones;
            private readonly List<Id> localDeclarations;
            private readonly List<Id> todosLosIds;
            private readonly HashSet<Id> parametersIds;
			private readonly SymbolTable symbolTable;

			internal ReferencesSolver(Program program, Parameters initialParameterSet)
            {
				this.symbolTable = program.symbolTable;

				// Colectar LValues de asignaciones, excluyendo aquellos que ya existen como variables globales
				var lValuesFromAssignments = program.Collect<NewInstanceStatement>()
					.Where(x => x.LValue is Id)
					.Select(x => (Id)x.LValue)
					.Where(id => !symbolTable.HasVariable(id.Name))  // Filter out existing global variables
					.ToList();

                var forVariables = program.Collect<ForStatement>().Where(x => x.Variable != null).Select(x => (Id)x.Variable);
                var forIndexVariables = program.Collect<ForStatement>()
                    .Where(x => x.VariableIndice != null)
                    .Select(x => (Id)x.VariableIndice);
                localDeclarations = lValuesFromAssignments
                    .Union(forVariables)
                    .Union(forIndexVariables)
                    .OrderBy(x => x.Level).ToList();
                todasLasDeclaraciones = program.DeclaracionesExternas.Count > 0 ? program.DeclaracionesExternas.Union(localDeclarations).ToList() : localDeclarations;
                todosLosIds = program.Collect<Id>().ToList();
				parametersIds = new HashSet<Id>();

				// First, process LValues that are parameters AND exist in the symbol table
				// (those that don't exist in the table will be processed in the todasLasDeclaraciones loop)
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

				// Segundo, procesar LValues que son referencias a variables globales existentes
				var globalLValues = program.Collect<NewInstanceStatement>()
					.Where(x => x.LValue is Id)
					.Select(x => (Id)x.LValue)
					.Where(id => symbolTable.HasVariable(id.Name) && !initialParameterSet.ContainsParameter(id.Name))
					.ToList();

				foreach (var globalLValue in globalLValues)
				{
					globalLValue.Program = program;
					globalLValue.DeclareAsGlobalVariable();
					globalLValue.MarkAsLValue();
					var symbol = symbolTable.Entry(globalLValue.Name);
					if (symbol != null && symbol.type != null)
					{
						globalLValue.ForcedType = symbol.type;
					}
				}

				var programaLevel = program.Level;
                foreach (var id in todasLasDeclaraciones)
                {
					id.Program = program;
					if (initialParameterSet.ContainsParameter(id.Name))
                    {
						parametersIds.Add(id);
						id.DeclareAsLocalParameter(initialParameterSet[id.Name]);
                    }

					if (id.Level == 0 && !id.IsParameter)
                    {
                        id.DeclareAsGlobalVariable();
                    }
                    else if (id.Level >= programaLevel && !id.IsGlobalVariable && !id.IsParameter)
                    {
                        id.DeclareAsLocalVariable();
                    }

					id.MarkAsLValue();
                }

				// Eval re-declaration unification:
				// EvalStatement.Execute re-entra a SolveReferences pasando las
				// declaraciones del programa interno como DeclaracionesExternas del
				// padre (y viceversa al parsear el siguiente Eval). En el TOP-LEVEL
				// el filtro HasVariable evita el problema porque el x del eval queda
				// Global. Dentro de un bloque el x es Local (IsolatedStorage), no
				// entra al SymbolTable y cada Eval('x = ...;') sucesivo produce un
				// x_evalN nuevo en localDeclarations. Sin unificar, parent termina con
				// dos OriginalLValueDeclaration distintas para el mismo nombre y, al
				// re-resolver tras el segundo Eval, ReferencesTo intenta rebindear el
				// x RValue ya bindeado a x_eval1 hacia x_eval2 y lanza "ambigous
				// declaration". Aqui detectamos ese caso y unificamos el local con
				// el external: el assignment del Eval interno termina escribiendo al
				// mismo symbol que ya ven los reads del bloque externo.
				if (program.DeclaracionesExternas.Count > 0)
				{
					var externalsByName = program.DeclaracionesExternas
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
						todasLasDeclaraciones.Remove(localLValue);
					}
				}

                foreach (var id in todosLosIds)
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

				// program.HasEval evita el Collect<EvalStatement>() (un recorrido completo del
				// AST) cuando el Program no tiene evals — el caso de todos los scripts del
				// journal en rehidratacion. Si no hay evals el foreach no haria nada de todos
				// modos, pero el Collect igual caminaba el arbol entero.
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
						Id declaracion = localDeclarations[declIdx];

						if (declaracion.IsOriginalLValueDeclaration)
						{
							for (int refIdx = declIdx + 1; refIdx < localDeclarations.Count; refIdx++)
							{
								Id referencia = localDeclarations[refIdx];
								if (declaracion.IsReferencedBy(referencia))
								{
									referencia.ReferencesTo(declaracion);
								}
							}

							if (!declaracion.IsParameter)
							{
								var referencias = todosLosIds.Where(
									referencia =>
										referencia != declaracion &&
										!referencia.IsLValue && 
										!referencia.IsParameter &&
										string.Equals(referencia.Name, declaracion.Name, StringComparison.OrdinalIgnoreCase) &&
										declaracion.IsReferencedBy(referencia)
								);
								foreach (Id referencia in referencias)
								{
									referencia.ReferencesTo(declaracion);
								}
							}
						}
					}
            }

            internal void SolveReferencesToRValues()
            {
                var ids = todosLosIds;
                foreach (Id declaracion in todasLasDeclaraciones)
                {
					if (declaracion.IsOriginalLValueDeclaration && !declaracion.IsParameter)
					{
						var referencias = ids.Where(
							referencia =>
								string.Equals(referencia.Name, declaracion.Name, StringComparison.OrdinalIgnoreCase) &&
								! referencia.IsLValue &&
								declaracion.IsReferencedBy(referencia)
						);
						foreach (Id referencia in referencias)
						{
							referencia.ReferencesTo(declaracion);
						}
					}
                }
				foreach (Id reference in todosLosIds)
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

			internal IEnumerable<Id> IdDeclarations()
			{
				return todasLasDeclaraciones;
			}

			internal IEnumerable<Id> IdAllReferences()
			{
				return todosLosIds;
			}

			internal IEnumerable<Id> IdsParameter()
			{
				return parametersIds;
			}

			internal ParameterSignature ParameterSignature()
			{
				var referencedParams = this.todosLosIds
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

            internal override void OnVisit(AST nodo)
            {
                Id id = (Id)nodo;
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
