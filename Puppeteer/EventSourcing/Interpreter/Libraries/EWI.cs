using Puppeteer.EventSourcing.Follower;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	abstract class EWI : AST
	{
		private readonly AstExpression reason;

		internal EWI(AstExpression reason)
		{
			this.reason = reason;
		}

		internal void ValidateStatically()
		{
			if (!reason.ComputeType().Equals(typeof(string)))
			{
				throw new LanguageException($"The expression '{reason.GetType().Name}' on the right-hand side of the EWI must return a string value.");
			}
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			reason.PreparePatternMatching(patternAst, ref position);
		}

		internal void Execute(Output output)
		{
			if (!output.IsWriting)
			{
				return;
			}
			string resultado = (string)reason.Execute();
			if (resultado == null)
			{
				throw new LanguageException("The output value of an EWI cannot be null.");
			}

			string type = "";
			switch (this)
			{
				case Error:
					type = "Error";
					break;
				case Information:
					type = "Message";
					break;
				case Warning:
					type = "Warning";
					break;
				default:
					throw new LanguageException("Unhandled EWI type.");
			}
			;

			output.AddEWI(type, resultado);
		}

		internal Expression ExecuteExpression(ParameterExpression parametersParam, ParameterExpression outputParam)
		{
			// Access internal property 'IsWriting' via reflection
			var estaEscribiendoProp = typeof(Output).GetProperty(nameof(Output.IsWriting), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
			var estaEscribiendoExpr = Expression.Property(outputParam, estaEscribiendoProp);

			// if (!outputParam.IsWriting) return Expression.Empty();
			var earlyReturn = Expression.IfThen(
				Expression.IsFalse(estaEscribiendoExpr),
				Expression.Return(Expression.Label(), Expression.Empty())
			);

			var reasonExpr = reason.ExecuteExpression(parametersParam);
			var resultVar = Expression.Variable(typeof(string), "resultado");
			var assignResult = Expression.Assign(resultVar, Expression.Convert(reasonExpr, typeof(string)));

			var nullCheck = Expression.IfThen(
				Expression.Equal(resultVar, Expression.Constant(null, typeof(string))),
				Expression.Throw(Expression.New(typeof(LanguageException).GetConstructor(new[] { typeof(string) }),
					Expression.Constant("The output value of an EWI cannot be null.")))
			);

			Expression tipoExpr;
			switch (this)
			{
				case Error:
					tipoExpr = Expression.Constant("Error");
					break;
				case Information:
					tipoExpr = Expression.Constant("Message");
					break;
				case Warning:
					tipoExpr = Expression.Constant("Warning");
					break;
				default:
					throw new LanguageException("Unhandled EWI type.");
			}

			var addEwiMethod = typeof(Output).GetMethod(
				nameof(Output.AddEWI),
				System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
				null,
				new[] { typeof(string), typeof(string) },
				null
			);

			var addEwiCall = Expression.Call(
				outputParam,
				addEwiMethod,
				tipoExpr,
				resultVar
			);

			return Expression.Block(
				new[] { resultVar },
				assignResult,
				nullCheck,
				addEwiCall
			);
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			reason.Visit(v);
		}

		internal void Write(StringBuilder resultado, DatabaseType databaseType)
		{
			switch (this)
			{
				case Error e:
					resultado.Append(" Error ");
					break;
				case Information m:
					resultado.Append(" Message ");
					break;
				case Warning w:
					resultado.Append(" Warning ");
					break;
				default:
					throw new LanguageException("Unhandled EWI type.");
			}
			;

			reason.write(resultado, databaseType);
		}

		protected AstExpression Reason
		{
			get
			{
				return reason;
			}
		}
	}
}
