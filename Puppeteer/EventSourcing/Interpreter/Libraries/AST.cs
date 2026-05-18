using Puppeteer.EventSourcing.Follower;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{

	internal abstract class AST
	{
		protected internal string GenerarTabs(int cantidad)
		{
			switch (cantidad)
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
					string tabsGenerados = (new string(new char[cantidad])).Replace('\0', '\t');
					return tabsGenerados;
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

			internal Collector(AST root, Type type) : base(root, type)
			{
			}

			internal override void OnVisit(AST nodo)
			{
				if (!list.Contains(nodo)) list.Add(nodo);
			}

			internal List<AST> GetAll()
			{
				return list;
			}
		}

	}

}
