using System;

namespace Puppeteer.EventSourcing.Playbill
{
	// Typed schema of a Playbill. Internal — the dev does not construct PlaybillSchema
	// directly; they declare it via Performance.Playbill(name, s => s.Required<T>(...))
	// and the builder produces the canonical declarations text (same format that V2
	// actions use in their define statements).
	//
	// Wraps a Parameters instance parsed from the declarations text. Serves
	// as a template for validation when setting values via WithPlaybill — verifies
	// that each field set exists in the schema and that the type matches.
	internal sealed class PlaybillSchema
	{
		internal string Name { get; }
		internal string Declarations { get; }
		internal Parameters Template { get; }

		internal PlaybillSchema(string name, string declarations)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(name);
			ArgumentNullException.ThrowIfNull(declarations);

			this.Name = name;
			this.Declarations = declarations;
			this.Template = new Parameters(declarations);
		}
	}
}
