using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class OpNot : UnaryAstExpression
	{
		internal OpNot(AstExpression e) : base(e)
		{
		}

		internal override Type ComputeType()
		{
			return typeof(bool);
		}

		internal override void ValidateStatically()
		{
			Type type = e.ComputeType();
			if (type != typeof(bool))
			{
				throw new LanguageException($"The right-hand expression of NOT must return a boolean value, but got type '{type.Name}'.");
			}
			ForcedType = typeof(bool);
		}

		internal override object Execute()
		{
			object object1 = e.Execute();
			Type type1 = object1.GetType();
			if (type1 != typeof(bool))
			{
				throw new LanguageException($"The NOT operator cannot operate on a value of type '{type1.Name}'.");
			}
			return !(bool)object1;
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			var innerExpression = e.ExecuteExpression(parametersParam);

			if (innerExpression.Type != typeof(bool))
			{
				throw new LanguageException($"The NOT operator cannot operate on an expression of type '{innerExpression.Type.Name}'.");
			}

			if (innerExpression is ConstantExpression ce)
			{
				bool value = (bool)ce.Value;
				return Expression.Constant(!value, typeof(bool));
			}

			return Expression.Not(innerExpression);
		}

		internal override void write(StringBuilder result, DatabaseType databaseType)
		{
			result.Append(" ! ");
			e.write(result, databaseType);
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
