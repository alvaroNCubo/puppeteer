using Puppeteer.EventSourcing.Follower;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	// Range literal {start..end}: an inclusive, ascending integer sequence. It
	// materializes to an IntRangeList (a List<int> that remembers its bounds) so it
	// slots into every place a collection is expected — foreach and collection
	// parameters — while letting the journal serialize it by comprehension. The
	// sequence is empty when end < start. See docs/rfc/foreach-range-literal.md.
	//
	// PromotionCandidateHash keeps the AST default (value-blind), so {1..5} and
	// {1..9} hash alike and are recognized as promotable to {@a..@b}.
	sealed class LiteralRange : AstExpression
	{
		private readonly AstExpression start;
		private readonly AstExpression end;

		internal LiteralRange(AstExpression start, AstExpression end)
		{
			this.start = start ?? throw new ArgumentNullException(nameof(start));
			this.end = end ?? throw new ArgumentNullException(nameof(end));
		}

		private static void RequireIntEndpoint(Type endpointType, string which)
		{
			// A null type is a late-bound endpoint (e.g. an @parameter whose type is
			// resolved later); leave it to runtime. Only a known non-int type is wrong.
			if (endpointType != null && endpointType != typeof(int))
			{
				throw new LanguageException($"A range literal {{start..end}} requires int endpoints, but the {which} endpoint is of type {endpointType.Name}.");
			}
		}

		internal override Type ComputeType()
		{
			RequireIntEndpoint(start.ComputeType(), "start");
			RequireIntEndpoint(end.ComputeType(), "end");
			return typeof(List<int>);
		}

		internal override void ValidateStatically()
		{
			start.ValidateStatically();
			end.ValidateStatically();
			base.ValidateStatically();
		}

		internal override object Execute()
		{
			int startValue = Convert.ToInt32(start.Execute());
			int endValue = Convert.ToInt32(end.Execute());
			return new IntRangeList(startValue, endValue);
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			Expression startExp = ToInt(start.ExecuteExpression(parametersParam));
			Expression endExp = ToInt(end.ExecuteExpression(parametersParam));
			ConstructorInfo ctor = typeof(IntRangeList).GetConstructor(new[] { typeof(int), typeof(int) });
			return Expression.New(ctor, startExp, endExp);
		}

		private static Expression ToInt(Expression endpoint)
		{
			return endpoint.Type == typeof(int) ? endpoint : Expression.Convert(endpoint, typeof(int));
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
			start.Visit(v);
			end.Visit(v);
		}

		internal override void write(StringBuilder result, DatabaseType databaseType)
		{
			result.Append('{');
			start.write(result, databaseType);
			result.Append("..");
			end.write(result, databaseType);
			result.Append('}');
		}
	}
}
