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

		// B.3.1: structural hash that ignores literal *values* but preserves
		// structure and literal *types*. Two scripts that differ only in their
		// literal arguments (e.g. `cia.GetOrden(123)` vs `cia.GetOrden(456)`)
		// produce the same PromotionCandidateHash; they are equivalent except
		// for their parameters and thus are candidates for automatic promotion
		// from V1 Script to V2 Action. Used as the counter key for detecting
		// recurrent endpoints.
		//
		// Default contribution is just the concrete type name; subclasses
		// override to walk their structural children. Literal subclasses
		// (LiteralNumber, LiteralString, LiteralBoolean, ...) keep the default,
		// which means they contribute their *type* but not their *value* —
		// exactly the value-blindness required. Collisions on the int hash
		// are possible but bounded; B.3.4 will use a PromotionCandidate→
		// ActionId index that reverifies structural equivalence at
		// promotion time.
		internal virtual void AccumulatePromotionCandidateHash(ref HashCode hc)
		{
			hc.Add(this.GetType().Name);
		}

		private class Collector : ASTVisitor
		{
			private readonly List<AST> list = new List<AST>();
			// El dedup es real para los singletons de literales (LiteralBoolean.LiteralTrue/False,
			// LiteralString.EMPTY) que aparecen en mas de un punto del arbol. El List.Contains que
			// habia aqui hacia el OnVisit O(n) y por ende cada Collect<T>() O(n^2) en la cantidad de
			// nodos matcheados (Collect<Id>() sobre scripts con muchos identificadores era cuadratico).
			// HashSet baja el dedup a O(1) y la lista paralela preserva el orden de visita del que
			// dependen ReferencesSolver (OrderBy(Level) y los loops LValue/RValue).
			private readonly HashSet<AST> seen = new HashSet<AST>();

			internal Collector(AST root, Type type) : base(root, type)
			{
			}

			internal override void OnVisit(AST nodo)
			{
				if (seen.Add(nodo)) list.Add(nodo);
			}

			internal List<AST> GetAll()
			{
				return list;
			}
		}

	}

}
