using Puppeteer.EventSourcing.Follower;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class LiteralList : AstExpression
	{
		private readonly AstExpression[] elements;

		internal LiteralList(AstExpression[] elements)
		{
			this.elements = elements;
		}

		internal override object Execute()
		{
			var tipoDeLaLista = ComputeType();
			if (tipoDeLaLista == typeof(List<int>))
			{
				List<int> res = new List<int>(elements.Length);
				foreach (var e in elements) res.Add((int)e.Execute());
				return res;
			}
			else if (tipoDeLaLista == typeof(List<string>))
			{
				List<string> res = new List<string>(elements.Length);
				foreach (var e in elements) res.Add((string)e.Execute());
				return res;
			}
			else if (tipoDeLaLista == typeof(List<DateTime>))
			{
				List<DateTime> res = new List<DateTime>(elements.Length);
				foreach (var e in elements) res.Add((DateTime)e.Execute());
				return res;
			}
			else if (tipoDeLaLista == typeof(List<double>))
			{
				List<double> res = new List<double>(elements.Length);
				foreach (var e in elements) res.Add((double)e.Execute());
				return res;
			}
			else if (tipoDeLaLista == typeof(List<decimal>))
			{
				List<decimal> res = new List<decimal>(elements.Length);
				foreach (var e in elements) res.Add((decimal)e.Execute());
				return res;
			}
			else if (tipoDeLaLista == typeof(List<bool>))
			{
				List<bool> res = new List<bool>(elements.Length);
				foreach (var e in elements) res.Add((bool)e.Execute());
				return res;
			}
			else if (tipoDeLaLista == typeof(List<object>))
			{
				List<object> res = new List<object>(elements.Length);
				foreach (var e in elements) res.Add((object)e.Execute());
				return res;
			}
			else if (tipoDeLaLista == null)
			{
				List<object> res = new List<object>(elements.Length);
				return res;
			}
			return null;
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			// 1. Build the list of element expressions and their types
			var expressionList = new List<Expression>();
			var typeList = new List<Type>();
			foreach (var exp in this.elements)
			{
				var expr = exp.ExecuteExpression(parametersParam);
				expressionList.Add(expr);
				typeList.Add(expr.Type);
			}

			// 2. Determine the common type for the list (strict, as in Execute)
			Type elementType = null;
			if (typeList.Count == 0)
			{
				elementType = typeof(object);
			}
			else
			{
				elementType = typeList[0];
				for (int i = 1; i < typeList.Count; i++)
				{
					if (elementType != typeList[i])
					{
						// Allow int/double promotion
						if ((elementType == typeof(int) && typeList[i] == typeof(double)) ||
						(elementType == typeof(double) && typeList[i] == typeof(int)))
						{
							elementType = typeof(double);
						}
						// Allow int/decimal promotion
						else if ((elementType == typeof(int) && typeList[i] == typeof(decimal)) || (elementType == typeof(decimal) && typeList[i] == typeof(int)))
						{
							elementType = typeof(decimal);
						}
						// Allow double/decimal promotion
						else if ((elementType == typeof(double) && typeList[i] == typeof(decimal)) || (elementType == typeof(decimal) && typeList[i] == typeof(double)))
						{
							elementType = typeof(decimal);
						}
						// Allow int->object, string->object, etc.
						else
						{
							elementType = typeof(object);
							break;
						}
					}
				}
			}
			// 3. Convert elements as needed
			var convertedExpressions = new Expression[expressionList.Count];
			for (int i = 0; i < expressionList.Count; i++)
			{
				var exp = expressionList[i];
				if (exp.Type != elementType && elementType != typeof(object))
				{
					convertedExpressions[i] = Expression.Convert(exp, elementType);
				}
				else
				{
					convertedExpressions[i] = exp;
				}
			}

			// 4. Build the List<T> expression
			Type listType = typeof(List<>).MakeGenericType(elementType);
			Type iEnumerableType = typeof(IEnumerable<>).MakeGenericType(elementType);
			var ctor = listType.GetConstructor(new[] { iEnumerableType });
			var arrayInit = Expression.NewArrayInit(elementType, convertedExpressions);
			var newList = Expression.New(ctor, arrayInit);
			return newList;
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			patternAst.RegisterLiteral(Execute(), ComputeType(), position);
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			foreach (AstExpression e in elements)
			{
				e.Visit(v);
			}
		}

		internal override Type ComputeType()
		{
			Type tipoDeLaLista = null;
			foreach (AstExpression e in elements)
			{
				Type tipoDelElemento = e.ComputeType();
				if (tipoDeLaLista != null && tipoDelElemento != null && tipoDeLaLista != tipoDelElemento)
					throw new LanguageException($"Invalid element type. Element is a {tipoDelElemento} while List of {tipoDeLaLista}");
				if (tipoDeLaLista == null) tipoDeLaLista = tipoDelElemento;
			}
			if (tipoDeLaLista == typeof(int)) return typeof(List<int>);
			if (tipoDeLaLista == typeof(string)) return typeof(List<string>);
			if (tipoDeLaLista == typeof(DateTime)) return typeof(List<DateTime>);
			if (tipoDeLaLista == typeof(double)) return typeof(List<double>);
			if (tipoDeLaLista == typeof(decimal)) return typeof(List<decimal>);
			if (tipoDeLaLista == typeof(bool)) return typeof(List<bool>);

			return typeof(List<object>);
		}

		internal override void write(StringBuilder resultado, DatabaseType databaseType)
		{
			resultado.Append('{');
			for (int i = 0; i < elements.Length; i++)
			{
				if (i > 0)
				{
					resultado.Append(',');
					resultado.Append(' ');
				}
				elements[i].write(resultado, databaseType);
			}
			resultado.Append('}');
		}
	}
}
