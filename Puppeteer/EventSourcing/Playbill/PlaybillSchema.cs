using System;

namespace Puppeteer.EventSourcing.Playbill
{
	// Schema tipado de un Playbill. Internal — el dev no construye PlaybillSchema
	// directamente; lo declara via Performance.Playbill(name, s => s.Required<T>(...))
	// y el builder produce el declarations text canonico (mismo formato que V2
	// actions usan en sus define statements).
	//
	// Wraps una instancia de Parameters parseada desde el declarations text. Sirve
	// como template para validacion al setear values via WithPlaybill — verifica
	// que cada field setteado existe en el schema y que el tipo coincide.
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
