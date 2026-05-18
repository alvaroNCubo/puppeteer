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
		private readonly List<Statement> statements;

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
        internal IpAddress Ip { get; set; }
		internal UserInLog User { get; set; }
		internal long EntryId { get; set; }
        internal string Script { get;}
		internal bool IsCompiledMode { get; private set; } = false;
		internal string LastExposeData { get; private set; }

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

        internal string EjecutarCheck()
        {
            return Execute(false);
        }

        internal void CargarArgumentos(Parameters arguments)
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

        internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
        {
            foreach (Statement source in statements)
            {
                source.PreparePatternMatching(patternAst, ref position);
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
			bool hayEvals = this.Collect<EvalStatement>().Any() || this.Collect<OpEval>().Any();
			if (! hayEvals)
			{
				foreach (var source in this.statements)
				{
					source.ValidateStatically();
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

				if (! program.IsCompiledMode)
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
