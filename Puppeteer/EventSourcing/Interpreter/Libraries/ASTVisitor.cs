using System;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	internal abstract class ASTVisitor
	{
		private readonly AST root;
		private readonly Type target;

		protected ASTVisitor(AST root, Type target)
		{
			this.root = root;
			this.target = target;
		}

		internal void Visit()
		{
			root.Visit(this);
		}

		internal abstract void OnVisit(AST nodo);

		internal Type Target
		{
			get
			{
				return target;
			}
		}
	}
}
