using Puppeteer.EventSourcing.Follower;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{

	internal abstract class AST
	{
		protected internal string GenerateTabs(int count)
		{
			switch (count)
			{
				case 1:
					return "\t";
				case 2:
					return "\t\t";
				case 3:
					return "\t\t\t";
				case 4:
					return "\t\t\t\t";
				case 5:
					return "\t\t\t\t\t";
				case 6:
					return "\t\t\t\t\t\t";
				case 7:
					return "\t\t\t\t\t\t\t";
				default:
					string generatedTabs = (new string(new char[count])).Replace('\0', '\t');
					return generatedTabs;
			}
		}

		internal abstract void Visit(ASTVisitor v);

		internal abstract void PreparePatternMatching(PatternListNode patternAst, ref int position);

		internal IEnumerable<T> Collect<T>()
		{
			Collector collector = new Collector(this, typeof(T));
			collector.Visit();
			return collector.GetAll().Cast<T>();
		}


		private class Collector : ASTVisitor
		{
			private readonly List<AST> list = new List<AST>();
			// The dedup is real for the literal singletons (LiteralBoolean.LiteralTrue/False,
			// LiteralString.EMPTY) that appear at more than one point of the tree. The List.Contains
			// that used to be here made OnVisit O(n) and therefore each Collect<T>() O(n^2) in the
			// number of matched nodes (Collect<Id>() over scripts with many identifiers was quadratic).
			// HashSet brings the dedup down to O(1) and the parallel list preserves the visit order
			// that ReferencesSolver depends on (OrderBy(Level) and the LValue/RValue loops).
			private readonly HashSet<AST> seen = new HashSet<AST>();

			internal Collector(AST root, Type type) : base(root, type)
			{
			}

			internal override void OnVisit(AST node)
			{
				if (seen.Add(node)) list.Add(node);
			}

			internal List<AST> GetAll()
			{
				return list;
			}
		}

	}

}
