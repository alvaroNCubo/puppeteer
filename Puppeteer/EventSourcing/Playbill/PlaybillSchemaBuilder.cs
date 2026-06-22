using System;
using System.Collections.Generic;
using System.Text;

namespace Puppeteer.EventSourcing.Playbill
{
	// Fluent builder to declare a Playbill schema. The dev writes:
	//   .Playbill("RestApi", s => s
	//        .Required<string>("ip")
	//        .Required<string>("user")
	//        .Optional<string>("requestId"));
	//
	// Internally it builds a canonical declarations text compatible with the
	// V2 Parameters parser, with a '?' suffix on the field name to
	// mark optionality. The presence/absence of the suffix is interpreted
	// ONLY at the Playbill level — V2 actions neither process it nor need to know it.
	public sealed class PlaybillSchemaBuilder
	{
		private readonly List<(string Name, Type Type, bool Required)> fields = new List<(string, Type, bool)>();
		private readonly HashSet<string> seenNames = new HashSet<string>(StringComparer.Ordinal);

		public PlaybillSchemaBuilder Required<T>(string name)
		{
			AddField(name, typeof(T), required: true);
			return this;
		}

		public PlaybillSchemaBuilder Optional<T>(string name)
		{
			AddField(name, typeof(T), required: false);
			return this;
		}

		private void AddField(string name, Type type, bool required)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(name);
			ArgumentNullException.ThrowIfNull(type);
			if (name.Contains('?')) throw new LanguageException($"Playbill field name '{name}' must not contain '?' (use Required<T>/Optional<T> to indicate optionality).");
			if (!seenNames.Add(name)) throw new LanguageException($"Playbill field '{name}' declared more than once in the same schema.");
			fields.Add((name, type, required));
		}

		// Builds the canonical declarations text. Optional fields get '?' suffix
		// in the name. Format compatible with V2 Parameters parser (which treats
		// the '?' as opaque part of the identifier — the parser does not assign
		// special meaning to it; only Playbill code interprets it).
		internal string BuildDeclarations()
		{
			if (fields.Count == 0) throw new LanguageException("Playbill schema must declare at least one field.");

			var sb = new StringBuilder();
			bool first = true;
			foreach (var (name, type, required) in fields)
			{
				if (!first) sb.Append(',');
				sb.Append("In,").Append(name);
				if (!required) sb.Append('?');
				sb.Append(':').Append(TypeToDslName(type));
				first = false;
			}
			return sb.ToString();
		}

		// Internal accessor for runtime validation at WithPlaybill setter time.
		internal IReadOnlyList<(string Name, Type Type, bool Required)> Fields => fields;

		private static string TypeToDslName(Type t)
		{
			if (t == typeof(string)) return "string";
			if (t == typeof(int)) return "int";
			if (t == typeof(long)) return "long";
			if (t == typeof(bool)) return "bool";
			if (t == typeof(decimal)) return "decimal";
			if (t == typeof(double)) return "double";
			if (t == typeof(DateTime)) return "datetime";
			throw new LanguageException($"Playbill field type '{t.FullName}' is not supported. Supported: string, int, long, bool, decimal, double, datetime.");
		}
	}
}
