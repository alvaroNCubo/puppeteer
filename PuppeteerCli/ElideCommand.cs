using System;
using System.Collections.Generic;
using System.IO;
using Puppeteer;
using Puppeteer.EventSourcing.DB;

namespace PuppeteerCli
{
	// `puppeteer elide` — physical removal of an entry from a PlainText journal.
	// The sacred-file invariant says the .txt is never edited manually; this verb
	// is the authoritative removal path for operators (human or AI).
	//
	// Behavior:
	//   - Validates the entry exists in the journal and is not already elided.
	//   - Optionally snapshots the file to <file>.<timestamp>.bak before
	//     overwriting (--backup).
	//   - Rewrites the file with the targeted line(s) removed and the header's
	//     `// Elided:` list updated.
	//   - The removed ID becomes a gap; subsequent writes never reuse it
	//     (Option B signed — preserve IDs with gaps).
	//
	// Only applies to PlainText journals (--txt). FileSystem / SQL backends have
	// logical elision via reactions (MarkAsSkip) + Distill; physical removal in
	// those backends is the framework's job, not the CLI's.
	public static class ElideCommand
	{
		public static int Run(string[] args)
		{
			return RunCore(args, Console.Out, Console.Error);
		}

		public static int RunCore(string[] args, TextWriter stdout, TextWriter diagnostic)
		{
			ArgumentNullException.ThrowIfNull(args);
			ArgumentNullException.ThrowIfNull(stdout);
			ArgumentNullException.ThrowIfNull(diagnostic);

			string txtPath = null;
			var entriesToElide = new List<long>();
			bool keepBackup = false;
			string actorName = null;

			for (int i = 0; i < args.Length; i++)
			{
				switch (args[i])
				{
					case "--txt":
						if (++i >= args.Length)
						{
							diagnostic.WriteLine("Error: --txt requires a file path");
							return 1;
						}
						txtPath = args[i];
						break;
					case "--entry":
						if (++i >= args.Length)
						{
							diagnostic.WriteLine("Error: --entry requires an id");
							return 1;
						}
						if (!long.TryParse(args[i], out long id) || id <= 0)
						{
							diagnostic.WriteLine($"Error: --entry must be a positive integer; got '{args[i]}'");
							return 1;
						}
						entriesToElide.Add(id);
						break;
					case "--backup":
						keepBackup = true;
						break;
					case "--actor-name":
						if (++i >= args.Length)
						{
							diagnostic.WriteLine("Error: --actor-name requires a name");
							return 1;
						}
						actorName = args[i];
						break;
					default:
						diagnostic.WriteLine($"Error: unknown flag '{args[i]}'");
						return 1;
				}
			}

			if (string.IsNullOrWhiteSpace(txtPath))
			{
				diagnostic.WriteLine("Error: --txt <file> is required");
				return 1;
			}
			if (entriesToElide.Count == 0)
			{
				diagnostic.WriteLine("Error: at least one --entry <id> is required");
				return 1;
			}
			if (!File.Exists(txtPath))
			{
				diagnostic.WriteLine($"Error: journal file not found: {txtPath}");
				return 1;
			}

			if (string.IsNullOrWhiteSpace(actorName))
				actorName = Path.GetFileNameWithoutExtension(txtPath);

			try
			{
				// Open the journal through the backend so it parses the file
				// correctly (header, content, existing elisions) before we mutate it.
				var actor = new ActorV2(actorName);
				actor.ConfigureStorage(DatabaseType.PlainText, "path=" + txtPath);

				// Skip already-elided IDs to keep the operation idempotent.
				var alreadyElided = actor.PlainTextElidedIds();
				var freshIds = new List<long>();
				foreach (long id in entriesToElide)
				{
					if (alreadyElided.Contains(id))
					{
						diagnostic.WriteLine($"[puppeteer] Entry {id} is already elided; skipping.");
						continue;
					}
					freshIds.Add(id);
				}

				if (freshIds.Count == 0)
				{
					diagnostic.WriteLine("[puppeteer] Nothing to elide. File unchanged.");
					return 0;
				}

				actor.PlainTextElide(freshIds, keepBackup);

				stdout.WriteLine($"elided: [{string.Join(", ", freshIds)}]");
				if (keepBackup)
					stdout.WriteLine("backup: kept");
				return 0;
			}
			catch (LanguageException ex)
			{
				diagnostic.WriteLine($"Error: {ex.Message}");
				return 1;
			}
			catch (Exception ex)
			{
				diagnostic.WriteLine($"Error ({ex.GetType().Name}): {ex.Message}");
				return 1;
			}
		}
	}
}
