using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class OpNotEqual : BinaryAstExpression
	{
		private readonly OpEqual opIgualQue;

		internal OpNotEqual(AstExpression e1, AstExpression e2) : base(e1, e2)
		{
			this.opIgualQue = new OpEqual(e1, e2);
		}

		internal override Type ComputeType()
		{
			return typeof(bool);
		}

		internal override void ValidateStatically()
		{
			try
			{
				this.opIgualQue.ValidateStatically();
			}
			catch
			{
				var tipo1 = e1.ComputeType();
				var tipo2 = e2.ComputeType();
				throw new LanguageException($"Cannot compare types '{tipo1}' and '{tipo2}' with '!='.");
			}
		}

		internal override object Execute()
		{
			return !(bool)opIgualQue.Execute();
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			var innerExpression = opIgualQue.ExecuteExpression(parametersParam);
			if (innerExpression.Type != typeof(bool))
			{
				throw new LanguageException($"The '!=' operator cannot operate on an expression of type '{innerExpression.Type.Name}'.");
			}

			if (innerExpression is ConstantExpression ce)
			{
				bool value = (bool)ce.Value;
				return Expression.Constant(!value, typeof(bool));
			}

			return Expression.Not(innerExpression);
		}

		internal override void write(StringBuilder resultado, DatabaseType databaseType)
		{
			e1.write(resultado, databaseType);
			resultado.Append(" != ");
			e2.write(resultado, databaseType);
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			e1.Visit(v);
			e2.Visit(v);
		}

	}
}
