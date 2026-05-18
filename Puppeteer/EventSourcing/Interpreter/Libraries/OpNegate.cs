using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class OpNegate : UnaryAstExpression
	{
		internal OpNegate(AstExpression e) : base(e)
		{
		}

		internal override Type ComputeType()
		{
			Type type = e.ComputeType();
			if (type == typeof(double) || type == typeof(int) || type == typeof(decimal))
			{
				return type;
			}
			return null;
		}

		internal override void ValidateStatically()
		{
			var type = ComputeType();
			if (type == null)
			{
				type = e.ComputeType();
				throw new LanguageException($"Cannot negate a value of type '{type.Name}' (only int, double and decimal are supported).");
			}
			ForcedType = type;
		}

		internal override object Execute()
		{
			object objeto1 = e.Execute();
			if (objeto1.GetType() == typeof(int))
			{
				return -(int)objeto1;
			}
			else if (objeto1.GetType() == typeof(double))
			{
				return -(double)objeto1;
			}
			else if (objeto1.GetType() == typeof(decimal))
			{
				return -(decimal)objeto1;
			}
			throw new LanguageException($"The unary minus operator cannot operate on a value of type '{objeto1.GetType().Name}'.");
		}


		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			var value = this.e.ExecuteExpression(parametersParam);

			if (value is ConstantExpression)
			{
				var result = Execute();
				return Expression.Constant(result, value.Type);
			}

			Expression resultado = null;
			if (value.Type == typeof(int) || value.Type == typeof(double) || value.Type == typeof(decimal))
			{
				resultado = Expression.Negate(value);
			}
			else
			{
				throw new LanguageException($"The unary minus operator cannot operate on an expression of type '{value.Type.Name}'.");
			}
			return resultado;
		}

		internal override void write(StringBuilder resultado, DatabaseType databaseType)
		{
			resultado.Append('-');
			e.write(resultado, databaseType);
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			e.Visit(v);
		}

	}
}
