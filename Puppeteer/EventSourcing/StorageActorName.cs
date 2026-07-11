using System;
using System.Text.RegularExpressions;

namespace Puppeteer.EventSourcing
{
	// Validates that an actor's logical name can serve as a PHYSICAL storage
	// identifier on the target backend. The actor name doubles as the journal
	// table name (SQL) or the per-actor directory / table prefix (FileSystem,
	// Playbill), so a name that is not a legal identifier on the backend would
	// otherwise surface as a cryptic provider error (e.g. a SQL syntax error on a
	// purely numeric name like "1") deep inside the first write — long after the
	// actor was created. These guards fail early, at storage construction, with a
	// message that names the offending value and the rule it broke.
	//
	// Policy (signed): the engine VALIDATES, it does not TRANSFORM. Mapping a
	// rejected name to a safe surrogate would have to be deterministic, injective
	// and stable forever — a rename breaks every existing journal, and a naive
	// "replace the bad chars" collapses distinct names onto one physical store
	// (the same cross-actor collision hazard, one level down). Instead the host
	// that assigns actor addresses picks a representable name.
	//
	// The rule is per backend because the constraints genuinely differ. InMemory
	// keys its storage by the raw name, so it imposes none. SQL and FileSystem do,
	// and their guards are the strict, backend-agnostic subset ("works on any SQL
	// server" / "works as a directory on any OS, including Windows").
	internal static class StorageActorName
	{
		// The longest identifier derived from the actor name is the Playbill shadow
		// table "{name}_PlaybillRecords_new" (a +20-char suffix). MySQL caps
		// identifiers at 64, so the bare name must leave room: 64 - 20 = 44. The
		// cap is applied uniformly (even to diary-only actors that never grow a
		// Playbill) so a name accepted today cannot break later when a Playbill is
		// added to the same actor.
		private const int MaxSqlIdentifierLength = 44;

		private static readonly Regex SqlIdentifierPattern =
			new Regex("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

		internal static void ValidateSqlIdentifier(string actorName, string backend)
		{
			if (string.IsNullOrWhiteSpace(actorName)) throw new LanguageException("Actor name can not be empty.");
			ArgumentNullException.ThrowIfNull(backend);

			if (!SqlIdentifierPattern.IsMatch(actorName))
			{
				throw new LanguageException(
					$"Actor name '{actorName}' is not a valid {backend} storage identifier. " +
					"It doubles as the journal table name, so it must start with a letter and contain " +
					"only letters, digits or underscore. Purely numeric names such as '1' or '2' are " +
					"rejected — use a letter-led name (e.g. 'a1', 'acct1').");
			}

			if (actorName.Length > MaxSqlIdentifierLength)
			{
				throw new LanguageException(
					$"Actor name '{actorName}' is too long for a {backend} storage identifier " +
					$"({actorName.Length} chars). The name doubles as the journal table name and derived " +
					$"tables append a suffix, so it must be at most {MaxSqlIdentifierLength} characters.");
			}
		}

		// Windows reserved device names (case-insensitive): illegal as a path
		// segment even with an extension.
		private static readonly string[] ReservedDeviceNames =
		{
			"CON", "PRN", "AUX", "NUL",
			"COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
			"LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
		};

		private const int MaxFileSystemNameLength = 100;

		internal static void ValidateFileSystemName(string actorName)
		{
			if (string.IsNullOrWhiteSpace(actorName)) throw new LanguageException("Actor name can not be empty.");

			foreach (char c in actorName)
			{
				if (c < 32 || c == '/' || c == '\\' || c == ':' || c == '*' ||
					c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
				{
					throw new LanguageException(
						$"Actor name '{actorName}' is not a valid FileSystem storage identifier. It is used " +
						"as a per-actor directory name, so it may not contain path separators, any of " +
						": * ? \" < > | , or control characters.");
				}
			}

			if (actorName != actorName.Trim() || actorName.EndsWith(".", StringComparison.Ordinal))
			{
				throw new LanguageException(
					$"Actor name '{actorName}' is not a valid FileSystem storage identifier: a directory " +
					"name may not have leading/trailing whitespace or a trailing dot.");
			}

			if (actorName == "." || actorName == "..")
			{
				throw new LanguageException($"Actor name '{actorName}' is not a valid FileSystem storage identifier.");
			}

			foreach (string reserved in ReservedDeviceNames)
			{
				if (string.Equals(actorName, reserved, StringComparison.OrdinalIgnoreCase))
				{
					throw new LanguageException(
						$"Actor name '{actorName}' is a reserved device name and can not be used as a FileSystem directory.");
				}
			}

			if (actorName.Length > MaxFileSystemNameLength)
			{
				throw new LanguageException(
					$"Actor name '{actorName}' is too long for a FileSystem directory name " +
					$"({actorName.Length} chars); max {MaxFileSystemNameLength}.");
			}
		}
	}
}
