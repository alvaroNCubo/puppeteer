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
	class ForEachStatement : Statement
	{
		private readonly Id variable;
		private readonly Id indexVariable;
		private readonly bool indexOnly;
		private readonly SymbolTable symbolTable;
		private AstExpression expression;
		private readonly Statement body;

		internal ForEachStatement(SymbolTable symbolTable, Id variable, AstExpression expression, Statement body)
		{
			this.symbolTable = symbolTable;
			this.variable = variable;
			this.expression = expression;
			this.body = body;
			this.indexVariable = null;
			this.indexOnly = false;
		}

		internal ForEachStatement(SymbolTable symbolTable, Id indexVariable, Id variableElemento, bool indexOnly, AstExpression expression, Statement body)
		{
			this.symbolTable = symbolTable;
			this.indexVariable = indexVariable;
			this.variable = variableElemento;
			this.indexOnly = indexOnly;
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

		internal Id IndexVariable
		{
			get
			{
				return indexVariable;
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
			bool bodyIsBlock = this.body is BlockStatement;
			if (bodyIsBlock && ((BlockStatement)this.body).IsEmpty)
			{
				return;
			}

			Type elementType;
			IEnumerator iterador;
			var evaluatedExpression = expression.Execute();
			if (evaluatedExpression is IEnumerable)
			{
				var expressionValues = (evaluatedExpression as IEnumerable).GetEnumerator();
				var expressionType = evaluatedExpression.GetType();

				int[] anArray = Array.Empty<int>();

				if (expressionValues.GetType() == anArray.GetEnumerator().GetType())
				{

					elementType = expressionType.GetElementType();
				}
				else
				{
					elementType = expressionValues.GetType().GenericTypeArguments[0];
				}
				if (typeof(object).IsAssignableFrom(elementType))
				{
					iterador = expressionValues;
				}
				else
				{
					List<object> listaTemp = new List<object>();
					foreach (var elemento in (evaluatedExpression as IEnumerable))
					{
						listaTemp.Add(elemento);
					}
					iterador = listaTemp.GetEnumerator();
				}
			}
			else
			{
				throw new LanguageException("The value of the 'foreach' expression is neither a List nor an IEnumerable.");
			}

			output.OpenForEach();

			if (!bodyIsBlock)
			{
				if (Program != null) Program.lastExecutedStatement = body;
			}

			int currentIndex = 0;
			while (iterador.MoveNext())
			{
				object element = iterador.Current;
				output.BeginForEachMoveNext();
				if (indexVariable != null)
				{
					indexVariable.Store(currentIndex, typeof(int));
				}
				if (!indexOnly)
				{
					variable.Store(element, elementType);
				}
				body.Execute(output);
				output.EndForEachMoveNext();
				currentIndex++;
			}

			output.CloseForEach(indexOnly ? "_" : variable.Name);
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam, ParameterExpression outputParam)
		{
			Expression expressionExp = this.expression.ExecuteExpression(parametersParam);

			Type collectionType = expressionExp.Type;

			Type elementType;

			if (collectionType.IsArray)
			{
				//int[] arr = new int[10];
				//IEnumerator<int> e = arr.GetEnumerator(); << Genera Unable to cast object of type 'SZArrayEnumerator'
				//IEnumerator<int> e = ((IEnumerable<int>)arr).GetEnumerator(); << Solucion
				elementType = collectionType.GetElementType();
				var CastArrayType = typeof(IEnumerable<>).MakeGenericType(new[] { elementType });
				expressionExp = Expression.Convert(expressionExp, CastArrayType);
				collectionType = CastArrayType;
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

			if (elementType == null && !indexOnly)
			{
				this.variable.ForcedType = typeof(object);
			}

			string newVariable = indexOnly ? "_foreach_iter_" : this.variable.Name;

			ParameterExpression varIterador;
			Type iEnumeratorType;
			Expression iterador;

			Expression variableCreation;
			ParameterExpression iteratorVarDeclaration;

			if (!indexOnly)
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
				iteratorVarDeclaration = Expression.Variable(typeof(object), "_foreach_iter_discard_");
			}

			if (elementType != null && collectionType.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
			{
				// Use generic path only if the type implements IEnumerable<T>
				iEnumeratorType = typeof(IEnumerator<>).MakeGenericType(new[] { elementType });
				varIterador = Expression.Variable(iEnumeratorType, newVariable);

				var genericEnumerableType = typeof(IEnumerable<>).MakeGenericType(elementType);
				MethodInfo getEnumeratorMethod = genericEnumerableType.GetMethod(nameof(IEnumerable.GetEnumerator), Array.Empty<Type>());

				iterador = Expression.Call(
					Expression.Convert(expressionExp, genericEnumerableType),
					getEnumeratorMethod
				);
				iterador = Expression.Assign(varIterador, Expression.Convert(iterador, iEnumeratorType));
			}
			else
			{
				// Fallback to non-generic
				iEnumeratorType = typeof(IEnumerator);
				varIterador = Expression.Variable(iEnumeratorType, newVariable);

				iterador = Expression.Call(
					Expression.Convert(expressionExp, typeof(IEnumerable)),
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

			Expression outputExp = outputParam;

			Expression outputOpenFor = Expression.Call(
				outputExp,
				typeof(Output).GetMethod(nameof(Output.OpenForEach), BindingFlags.Instance | BindingFlags.NonPublic)
			);

			Expression forMoveNextStart = Expression.Call(
				outputExp,
				typeof(Output).GetMethod(nameof(Output.BeginForEachMoveNext), BindingFlags.Instance | BindingFlags.NonPublic)
			);

			var objectField = typeof(VariableSymbol).GetField(
				nameof(VariableSymbol.value),
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly
			);

			Expression variableStore;
			if (!indexOnly)
			{
				Expression varExp = this.variable.ExecuteExpression(parametersParam);
				variableStore = Expression.Assign(Expression.Field(varExp, objectField), Expression.Convert(currentExp, typeof(object)));
			}
			else
			{
				variableStore = Expression.Empty();
			}

			Expression forMoveNextEnd = Expression.Call(
				outputParam,
				typeof(Output).GetMethod(nameof(Output.EndForEachMoveNext), BindingFlags.Instance | BindingFlags.NonPublic)
			);

			Expression indexStore = Expression.Empty();
			Expression indexIncrement = Expression.Empty();
			ParameterExpression indexIteratorDeclaration = null;

			if (indexVariable != null)
			{
				Expression indexCreation;
				if (indexVariable.LValueStorageExpression != null)
				{
					indexCreation = Expression.Empty();
				}
				else if (indexVariable.IsOriginalLValueDeclaration)
				{
					indexCreation = indexVariable.AllocateStorageExpression(parametersParam, useLValueReference: indexVariable.IsLValue);
				}
				else
				{
					indexCreation = Expression.Empty();
				}

				indexIteratorDeclaration = (ParameterExpression)indexVariable.LValueStorageExpression;

				Expression indexVarExp = indexVariable.ExecuteExpression(parametersParam);
				indexStore = Expression.Block(
					indexCreation,
					Expression.Assign(Expression.Field(indexVarExp, objectField), Expression.Convert(Expression.Constant(0), typeof(object)))
				);

				indexIncrement = Expression.Assign(
					Expression.Field(indexVarExp, objectField),
					Expression.Convert(
						Expression.Add(
							Expression.Convert(Expression.Field(indexVarExp, objectField), typeof(int)),
							Expression.Constant(1)
						),
						typeof(object)
					)
				);
			}

			Expression cuerpoExp = this.body.ExecuteExpression(parametersParam, outputParam);

			Expression loopBlock = Expression.Block(
				forMoveNextStart,
				variableStore,
				cuerpoExp,
				indexIncrement,
				forMoveNextEnd
			);

			string cerrarForName = indexOnly ? "_" : this.variable.Name;
			Expression outputCloseFor = Expression.Call(
				outputExp,
				typeof(Output).GetMethod(nameof(Output.CloseForEach), BindingFlags.Instance | BindingFlags.NonPublic),
				Expression.Constant(cerrarForName)
			);

			LabelTarget finCiclo = Expression.Label();

			var blockVariables = new List<ParameterExpression> { varIterador, iteratorVarDeclaration };
			if (indexIteratorDeclaration != null)
				blockVariables.Add(indexIteratorDeclaration);

			var blockExpressions = new List<Expression> { variableCreation, indexStore, iterador, outputOpenFor };
			blockExpressions.Add(
				Expression.Loop(
					Expression.IfThenElse(
						moveNext,
						loopBlock,
						Expression.Break(finCiclo)
					),
					finCiclo
				)
			);
			blockExpressions.Add(outputCloseFor);

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
				throw new LanguageException($"A 'foreach' statement can only be executed when its expression is of type List, but found type '{collectionType.Name}'.");
			}

			if (indexVariable != null)
			{
				indexVariable.ForcedType = typeof(int);
			}

			if (!indexOnly && elementType != null)
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
		// and body so two foreach-loops with different shapes hash distinctly.
		internal override void AccumulatePromotionCandidateHash(ref HashCode hc)
		{
			hc.Add(nameof(ForEachStatement));
			hc.Add(indexOnly ? 1 : 0);
			if (indexVariable != null) { hc.Add(1); indexVariable.AccumulatePromotionCandidateHash(ref hc); } else { hc.Add(0); }
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
			if (indexVariable != null) indexVariable.Visit(v);
			if (!indexOnly) variable.Visit(v);
			expression.Visit(v);
			body.Visit(v);
		}

		internal override void PropagateProgram(Program program)
		{
			base.PropagateProgram(program);
			body.PropagateProgram(program);
		}

		internal override void Write(StringBuilder result, int tabs, DatabaseType databaseType)
		{
			if (WasFiltered) return;
			if (tabs > 0) result.Append(GenerateTabs(tabs));
			result.Append("foreach ( ");
			if (indexVariable != null)
			{
				result.Append(indexVariable.Name);
				result.Append(", ");
				result.Append(indexOnly ? "_" : variable.Name);
			}
			else
			{
				result.Append(variable.Name);
			}
			result.Append(" in ");
			expression.write(result, databaseType);
			result.Append(" )\r");
			if (!(body is BlockStatement))
			{
				tabs++;
			}
			body.Write(result, tabs, databaseType);
			if (!(body is BlockStatement))
			{
				tabs--;
			}
		}
	}
}
