using Puppeteer.EventSourcing.Follower;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class ForStatement : Statement
	{
		private readonly Id variable;
		private readonly Id variableIndice;
		private readonly bool soloIndice;
		private readonly SymbolTable symbolTable;
		private AstExpression expression;
		private readonly Statement body;

		internal ForStatement(SymbolTable symbolTable, Id variable, AstExpression expression, Statement body)
		{
			this.symbolTable = symbolTable;
			this.variable = variable;
			this.expression = expression;
			this.body = body;
			this.variableIndice = null;
			this.soloIndice = false;
		}

		internal ForStatement(SymbolTable symbolTable, Id variableIndice, Id variableElemento, bool soloIndice, AstExpression expression, Statement body)
		{
			this.symbolTable = symbolTable;
			this.variableIndice = variableIndice;
			this.variable = variableElemento;
			this.soloIndice = soloIndice;
			this.expression = expression;
			this.body = body;
		}

		internal Id Variable
		{
			get
			{
				return variable;
			}
		}

		internal Id VariableIndice
		{
			get
			{
				return variableIndice;
			}
		}

		internal AstExpression AstExpression
		{
			get
			{
				return expression;
			}
		}

		internal override void Execute(ExecutionOutput output)
		{
			bool bodyEsUnBloque = this.body is BlockStatement;
			if (bodyEsUnBloque && ((BlockStatement)this.body).IsEmpty)
			{
				return;
			}

			Type elementType;
			IEnumerator iterador;
			var expresionEvaluada = expression.Execute();
			if (expresionEvaluada is IEnumerable)
			{
				var valoresDeLaExpresion = (expresionEvaluada as IEnumerable).GetEnumerator();
				var expressionType = expresionEvaluada.GetType();

				int[] unArreglo = Array.Empty<int>();

				if (valoresDeLaExpresion.GetType() == unArreglo.GetEnumerator().GetType())
				{

					elementType = expressionType.GetElementType();
				}
				else
				{
					elementType = valoresDeLaExpresion.GetType().GenericTypeArguments[0];
				}
				if (typeof(object).IsAssignableFrom(elementType))
				{
					iterador = valoresDeLaExpresion;
				}
				else
				{
					List<object> listaTemp = new List<object>();
					foreach (var elemento in (expresionEvaluada as IEnumerable))
					{
						listaTemp.Add(elemento);
					}
					iterador = listaTemp.GetEnumerator();
				}
			}
			else
			{
				throw new LanguageException("The value of the 'for' expression is neither a List nor an IEnumerable.");
			}

			output.OpenFor();

			if (!bodyEsUnBloque)
			{
				if (Program != null) Program.lastExecutedStatement = body;
			}

			int indiceCurrent = 0;
			while (iterador.MoveNext())
			{
				object element = iterador.Current;
				output.BeginForMoveNext();
				if (variableIndice != null)
				{
					variableIndice.Store(indiceCurrent, typeof(int));
				}
				if (!soloIndice)
				{
					variable.Store(element, elementType);
				}
				body.Execute(output);
				output.EndForMoveNext();
				indiceCurrent++;
			}

			output.CloseFor(soloIndice ? "_" : variable.Name);
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam, ParameterExpression outputParam)
		{
			Expression expresionExp = this.expression.ExecuteExpression(parametersParam);

			Type collectionType = expresionExp.Type;

			Type elementType;

			if (collectionType.IsArray)
			{
				//int[] arr = new int[10];
				//IEnumerator<int> e = arr.GetEnumerator(); << Genera Unable to cast object of type 'SZArrayEnumerator'
				//IEnumerator<int> e = ((IEnumerable<int>)arr).GetEnumerator(); << Solucion
				elementType = collectionType.GetElementType();
				var CastArregloType = typeof(IEnumerable<>).MakeGenericType(new[] { elementType });
				expresionExp = Expression.Convert(expresionExp, CastArregloType);
				collectionType = CastArregloType;
			}
			else if (collectionType.IsGenericType)
			{
				elementType = collectionType.GetGenericArguments()[0];
			}
			else if (typeof(IEnumerable).IsAssignableFrom(collectionType))
			{
				elementType = null;
				foreach (var bt in collectionType.GetInterfaces())
					if (bt.IsGenericType && bt.GetGenericTypeDefinition() == typeof(IEnumerable<>))
						elementType = bt.GetGenericArguments()[0];
				if (elementType == null)
					elementType = typeof(object);
			}
			else
			{
				elementType = null;
			}

			if (elementType == null && !soloIndice)
			{
				this.variable.ForcedType = typeof(object);
			}

			string nuevaVariable = soloIndice ? "_for_iter_" : this.variable.Name;

			ParameterExpression varIterador;
			Type iEnumeratorType;
			Expression iterador;

			Expression variableCreation;
			ParameterExpression iteratorVarDeclaration;

			if (!soloIndice)
			{
				if (this.variable.IsOriginalLValueDeclaration)
				{
					variableCreation = this.variable.AllocateStorageExpression(parametersParam, useLValueReference: this.variable.IsLValue);
				}
				else
				{
					if (this.variable.ForcedType != elementType) throw new LanguageException($"Variable {this.variable.Name} was declared as {this.variable.ForcedType} but for collection is {elementType}");
					variableCreation = Expression.Empty();
				}
				iteratorVarDeclaration = (ParameterExpression)this.variable.LValueStorageExpression;
			}
			else
			{
				variableCreation = Expression.Empty();
				iteratorVarDeclaration = Expression.Variable(typeof(object), "_for_iter_discard_");
			}

			if (elementType != null && collectionType.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
			{
				// Use generic path only if the type implements IEnumerable<T>
				iEnumeratorType = typeof(IEnumerator<>).MakeGenericType(new[] { elementType });
				varIterador = Expression.Variable(iEnumeratorType, nuevaVariable);

				var genericEnumerableType = typeof(IEnumerable<>).MakeGenericType(elementType);
				MethodInfo getEnumeratorMethod = genericEnumerableType.GetMethod(nameof(IEnumerable.GetEnumerator), Array.Empty<Type>());

				iterador = Expression.Call(
					Expression.Convert(expresionExp, genericEnumerableType),
					getEnumeratorMethod
				);
				iterador = Expression.Assign(varIterador, Expression.Convert(iterador, iEnumeratorType));
			}
			else
			{
				// Fallback to non-generic
				iEnumeratorType = typeof(IEnumerator);
				varIterador = Expression.Variable(iEnumeratorType, nuevaVariable);

				iterador = Expression.Call(
					Expression.Convert(expresionExp, typeof(IEnumerable)),
					typeof(IEnumerable).GetMethod(nameof(IEnumerable.GetEnumerator), Array.Empty<Type>())
				);
				iterador = Expression.Assign(varIterador, iterador);
			}

			Expression currentExp = Expression.Property(
					varIterador,
					iEnumeratorType.GetProperty(nameof(IEnumerator.Current), Array.Empty<Type>())
			);

			Expression moveNext = Expression.Call(
				varIterador,
				typeof(IEnumerator).GetMethod(nameof(IEnumerator.MoveNext), Array.Empty<Type>())
			);

			Expression salidaExp = outputParam;

			Expression salidaAbrirFor = Expression.Call(
				salidaExp,
				typeof(Output).GetMethod(nameof(Output.OpenFor), BindingFlags.Instance | BindingFlags.NonPublic)
			);

			Expression inicioMoveNextDelFor = Expression.Call(
				salidaExp,
				typeof(Output).GetMethod(nameof(Output.BeginForMoveNext), BindingFlags.Instance | BindingFlags.NonPublic)
			);

			var objetoField = typeof(VariableSymbol).GetField(
				nameof(VariableSymbol.value),
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly
			);

			Expression variableStore;
			if (!soloIndice)
			{
				Expression varExp = this.variable.ExecuteExpression(parametersParam);
				variableStore = Expression.Assign(Expression.Field(varExp, objetoField), Expression.Convert(currentExp, typeof(object)));
			}
			else
			{
				variableStore = Expression.Empty();
			}

			Expression finMoveNextDelFor = Expression.Call(
				outputParam,
				typeof(Output).GetMethod(nameof(Output.EndForMoveNext), BindingFlags.Instance | BindingFlags.NonPublic)
			);

			Expression indiceStore = Expression.Empty();
			Expression indiceIncrement = Expression.Empty();
			ParameterExpression indiceIteratorDeclaration = null;

			if (variableIndice != null)
			{
				Expression indiceCreation;
				if (variableIndice.LValueStorageExpression != null)
				{
					indiceCreation = Expression.Empty();
				}
				else if (variableIndice.IsOriginalLValueDeclaration)
				{
					indiceCreation = variableIndice.AllocateStorageExpression(parametersParam, useLValueReference: variableIndice.IsLValue);
				}
				else
				{
					indiceCreation = Expression.Empty();
				}

				indiceIteratorDeclaration = (ParameterExpression)variableIndice.LValueStorageExpression;

				Expression indiceVarExp = variableIndice.ExecuteExpression(parametersParam);
				indiceStore = Expression.Block(
					indiceCreation,
					Expression.Assign(Expression.Field(indiceVarExp, objetoField), Expression.Convert(Expression.Constant(0), typeof(object)))
				);

				indiceIncrement = Expression.Assign(
					Expression.Field(indiceVarExp, objetoField),
					Expression.Convert(
						Expression.Add(
							Expression.Convert(Expression.Field(indiceVarExp, objetoField), typeof(int)),
							Expression.Constant(1)
						),
						typeof(object)
					)
				);
			}

			Expression cuerpoExp = this.body.ExecuteExpression(parametersParam, outputParam);

			Expression bloqueCiclo = Expression.Block(
				inicioMoveNextDelFor,
				variableStore,
				cuerpoExp,
				indiceIncrement,
				finMoveNextDelFor
			);

			string cerrarForName = soloIndice ? "_" : this.variable.Name;
			Expression salidaCerrarFor = Expression.Call(
				salidaExp,
				typeof(Output).GetMethod(nameof(Output.CloseFor), BindingFlags.Instance | BindingFlags.NonPublic),
				Expression.Constant(cerrarForName)
			);

			LabelTarget finCiclo = Expression.Label();

			var blockVariables = new List<ParameterExpression> { varIterador, iteratorVarDeclaration };
			if (indiceIteratorDeclaration != null)
				blockVariables.Add(indiceIteratorDeclaration);

			var blockExpressions = new List<Expression> { variableCreation, indiceStore, iterador, salidaAbrirFor };
			blockExpressions.Add(
				Expression.Loop(
					Expression.IfThenElse(
						moveNext,
						bloqueCiclo,
						Expression.Break(finCiclo)
					),
					finCiclo
				)
			);
			blockExpressions.Add(salidaCerrarFor);

			Expression blockExpr = Expression.Block(
				blockVariables,
				blockExpressions
			);
			return blockExpr;
		}

		internal override void ValidateStatically()
		{
			expression.ValidateStatically();

			Type collectionType = expression.ComputeType();

			Type elementType;
			if (collectionType.IsArray)
			{
				elementType = collectionType.GetElementType();
			}
			else if (collectionType.IsGenericType)
			{
				elementType = collectionType.GetGenericArguments()[0];
			}
			else if (typeof(IEnumerable).IsAssignableFrom(collectionType))
			{
				elementType = null;
				foreach (var bt in collectionType.GetInterfaces())
					if (bt.IsGenericType && bt.GetGenericTypeDefinition() == typeof(IEnumerable<>))
						elementType = bt.GetGenericArguments()[0];
				if (elementType == null)
					elementType = typeof(object);
			}
			else if (collectionType == typeof(object)) //late binding
			{
				elementType = typeof(object);
			}
			else
			{
				throw new LanguageException($"A 'for' statement can only be executed when its expression is of type List, but found type '{collectionType.Name}'.");
			}

			if (variableIndice != null)
			{
				variableIndice.ForcedType = typeof(int);
			}

			if (!soloIndice && elementType != null)
			{
				this.variable.ForcedType = elementType;
			}

			body.ValidateStatically();
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			expression.PreparePatternMatching(patternAst, ref position);
			body.PreparePatternMatching(patternAst, ref position);
		}

		// B.3.1: include both loop variables (when present), iteration source,
		// and body so two for-loops with different shapes hash distinctly.
		internal override void AccumulatePromotionCandidateHash(ref HashCode hc)
		{
			hc.Add(nameof(ForStatement));
			hc.Add(soloIndice ? 1 : 0);
			if (variableIndice != null) { hc.Add(1); variableIndice.AccumulatePromotionCandidateHash(ref hc); } else { hc.Add(0); }
			if (variable != null) { hc.Add(1); variable.AccumulatePromotionCandidateHash(ref hc); } else { hc.Add(0); }
			expression.AccumulatePromotionCandidateHash(ref hc);
			body.AccumulatePromotionCandidateHash(ref hc);
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			if (variableIndice != null) variableIndice.Visit(v);
			if (!soloIndice) variable.Visit(v);
			expression.Visit(v);
			body.Visit(v);
		}

		internal override void Write(StringBuilder resultado, int tabs, DatabaseType databaseType)
		{
			if (FueFiltrado) return;
			if (tabs > 0) resultado.Append(GenerarTabs(tabs));
			resultado.Append("For ( ");
			if (variableIndice != null)
			{
				resultado.Append(variableIndice.Name);
				resultado.Append(", ");
				resultado.Append(soloIndice ? "_" : variable.Name);
			}
			else
			{
				resultado.Append(variable.Name);
			}
			resultado.Append(" : ");
			expression.write(resultado, databaseType);
			resultado.Append(" )\r");
			if (!(body is BlockStatement))
			{
				tabs++;
			}
			body.Write(resultado, tabs, databaseType);
			if (!(body is BlockStatement))
			{
				tabs--;
			}
		}
	}
}
