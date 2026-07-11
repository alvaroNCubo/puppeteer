using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Puppeteer;
using Puppeteer.EventSourcing.DB;
using Puppeteer.EventSourcing.Interpreter.Formatters;
using PuppeteerCli.PromptBook;

namespace PuppeteerCli
{
	// puppeteer attach — the CLI's long-lived phase.
	//
	// Unlike the one-shot verbs (show entry / show action), attach opens a
	// hydrated SESSION: it builds the primary actor, creates an isolated Shadow,
	// replays the primary's journal up to the head, and enters a REPL that keeps
	// the hydration alive while the AI operates. Each session is journaled in the
	// PromptBook (the CLI's own actor) — open at start, close at exit.
	//
	// Isolation by construction: EVERYTHING the REPL executes lands on the shadow.
	// The primary's journal stays intact. The AI is free to make mistakes.
	//
	// Layer 2 (signed 2026-06-01): the REPL only supports meta-verbs (exit / help).
	// DSL execution (print / cmd / blocks) arrives in Layer 3. End-to-end with a
	// real domain arrives in Layer 4.
	public static class AttachCommand
	{
		public static int Run(string[] args)
		{
			return RunCore(args, Console.In, Console.Out, Console.Error);
		}

		// Testable variant: injected stdin/stdout/stderr. Tests pass
		// StringReader/StringWriter to validate the REPL flow without touching
		// the real console.
		public static int RunCore(string[] args, TextReader input, TextWriter stdout, TextWriter diagnostic)
		{
			ArgumentNullException.ThrowIfNull(args);
			ArgumentNullException.ThrowIfNull(input);
			ArgumentNullException.ThrowIfNull(stdout);
			ArgumentNullException.ThrowIfNull(diagnostic);

			AttachArgs parsed;
			try
			{
				parsed = ParseArgs(args);
			}
			catch (LanguageException ex)
			{
				diagnostic.WriteLine($"Error: {ex.Message}");
				return 1;
			}

			return parsed.Mode == AttachMode.Txt
				? RunTxtMode(parsed, input, stdout, diagnostic)
				: RunPrimaryMode(parsed, input, stdout, diagnostic);
		}

		// Primary mode: Shadow-isolated against a FileSystem primary journal. The
		// shadow is the operator's playground; the primary stays untouched.
		private static int RunPrimaryMode(AttachArgs parsed, TextReader input, TextWriter stdout, TextWriter diagnostic)
		{
			Assembly[] libraries;
			try
			{
				libraries = LoadLibraries(parsed.LibrariesArg, diagnostic);
			}
			catch (Exception ex)
			{
				diagnostic.WriteLine($"Error loading libraries: {ex.Message}");
				return 1;
			}

			ActorV2 primary = libraries.Length > 0
				? new ActorV2(parsed.ActorName, libraries)
				: new ActorV2(parsed.ActorName);

			diagnostic.WriteLine($"[puppeteer] Hydrating primary from {parsed.PrimaryConnection}...");
			try
			{
				primary.ConfigureStorage(DatabaseType.FileSystem, parsed.PrimaryConnection);
			}
			catch (Exception ex)
			{
				diagnostic.WriteLine($"Error hydrating primary: {ex.Message}");
				return 1;
			}

			long head = primary.CurrentEntryId;
			diagnostic.WriteLine($"[puppeteer] Primary head = {head}.");

			string shadowId = ShortGuid();
			var cfg = new ShadowConfig(
				id: shadowId,
				shadowStorageType: DatabaseType.IN_MEMORY,
				shadowStorageConnection: "memory",
				mode: ShadowMode.PointInTime);

			Shadow shadow;
			try
			{
				shadow = primary.Shadow(cfg);
			}
			catch (Exception ex)
			{
				diagnostic.WriteLine($"Error creating shadow: {ex.Message}");
				return 1;
			}

			diagnostic.WriteLine($"[puppeteer] Shadow created: {shadow.Actor.Name}");

			try
			{
				shadow.SyncUntil(head);
			}
			catch (Exception ex)
			{
				diagnostic.WriteLine($"Error syncing shadow: {ex.Message}");
				shadow.Dispose();
				return 1;
			}

			diagnostic.WriteLine($"[puppeteer] Shadow synced to entry {head}. Hydration complete.");

			using var promptBook = new PromptBookActor(parsed.PromptBookOverride);
			promptBook.OpenSession(parsed.ActorName, "snapshot");
			diagnostic.WriteLine("[puppeteer] PromptBook session opened.");
			diagnostic.WriteLine("[puppeteer] Type 'help' for commands. 'exit' or EOF to quit.");

			string exitReason = "unknown";
			try
			{
				exitReason = Repl((ActorV2)shadow.Actor, parsed.ActorName, shadowId, input, stdout, diagnostic);
			}
			catch (Exception ex)
			{
				exitReason = "error:" + ex.GetType().Name;
				diagnostic.WriteLine($"[puppeteer] REPL crashed ({ex.GetType().Name}): {ex.Message}");
			}
			finally
			{
				promptBook.CloseSession(exitReason);
				shadow.Dispose();
				diagnostic.WriteLine($"[puppeteer] PromptBook session ended ({exitReason}). Shadow discarded.");
			}
			return 0;
		}

		// PlainText mode: the .txt file IS the canonical journal. No Shadow — the
		// human/AI authoring is the truth. Every PerformCommand appends one line.
		// Reactions are runtime-only (the InMemory auxiliary stores), lost between
		// sessions. Use the primary FS mode for workloads that need persisted
		// reactions or distributed replication.
		private static int RunTxtMode(AttachArgs parsed, TextReader input, TextWriter stdout, TextWriter diagnostic)
		{
			Assembly[] libraries;
			try
			{
				libraries = LoadLibrariesForTxtMode(parsed.LibrariesArg, parsed.TxtPath, diagnostic);
			}
			catch (Exception ex)
			{
				diagnostic.WriteLine($"Error loading libraries: {ex.Message}");
				return 1;
			}

			bool isNewFile = !File.Exists(parsed.TxtPath);
			ActorV2 actor = libraries.Length > 0
				? new ActorV2(parsed.ActorName, libraries)
				: new ActorV2(parsed.ActorName);

			diagnostic.WriteLine($"[puppeteer] Opening PlainText journal: {parsed.TxtPath}");
			try
			{
				actor.ConfigureStorage(DatabaseType.PlainText, "path=" + parsed.TxtPath);
			}
			catch (Exception ex)
			{
				diagnostic.WriteLine($"Error opening PlainText journal: {ex.Message}");
				return 1;
			}

			long head = actor.CurrentEntryId;
			if (isNewFile)
			{
				diagnostic.WriteLine("[puppeteer] New journal created.");
				PrintFirstAttachTutorial(diagnostic);
			}
			else
			{
				diagnostic.WriteLine($"[puppeteer] Journal head = {head}.");
			}

			using var promptBook = new PromptBookActor(parsed.PromptBookOverride);
			promptBook.OpenSession(parsed.ActorName, "plaintext");
			diagnostic.WriteLine("[puppeteer] PromptBook session opened. Type 'help' for commands. 'exit' or EOF to quit.");

			string exitReason = "unknown";
			try
			{
				exitReason = Repl(actor, parsed.ActorName, "txt", input, stdout, diagnostic);
			}
			catch (Exception ex)
			{
				exitReason = "error:" + ex.GetType().Name;
				diagnostic.WriteLine($"[puppeteer] REPL crashed ({ex.GetType().Name}): {ex.Message}");
			}
			finally
			{
				promptBook.CloseSession(exitReason);
				diagnostic.WriteLine($"[puppeteer] PromptBook session ended ({exitReason}). Journal closed.");
			}
			return 0;
		}

		// ── Parsing ─────────────────────────────────────────────────────────

		private enum AttachMode { Primary, Txt }

		private sealed class AttachArgs
		{
			public AttachMode Mode;
			public string PrimaryConnection;
			public string TxtPath;
			public string ActorName;
			public bool Snapshot;
			public string LibrariesArg;
			public string PromptBookOverride; // null => default %LOCALAPPDATA% path
		}

		private static AttachArgs ParseArgs(string[] args)
		{
			var result = new AttachArgs();
			for (int i = 0; i < args.Length; i++)
			{
				switch (args[i])
				{
					case "--primary":
						if (++i >= args.Length) throw new LanguageException("--primary requires a connection string");
						result.PrimaryConnection = args[i];
						break;
					case "--txt":
						if (++i >= args.Length) throw new LanguageException("--txt requires a file path");
						result.TxtPath = args[i];
						break;
					case "--actor-name":
						if (++i >= args.Length) throw new LanguageException("--actor-name requires a name");
						result.ActorName = args[i];
						break;
					case "--snapshot":
						result.Snapshot = true;
						break;
					case "--libraries":
						if (++i >= args.Length) throw new LanguageException("--libraries requires a path or comma-separated list");
						result.LibrariesArg = args[i];
						break;
					case "--prompt-book":
						if (++i >= args.Length) throw new LanguageException("--prompt-book requires a directory path");
						result.PromptBookOverride = args[i];
						break;
					default:
						throw new LanguageException($"Unknown flag: {args[i]}");
				}
			}

			bool hasPrimary = !string.IsNullOrWhiteSpace(result.PrimaryConnection);
			bool hasTxt = !string.IsNullOrWhiteSpace(result.TxtPath);

			if (hasPrimary && hasTxt)
				throw new LanguageException("--primary and --txt are mutually exclusive; choose one.");
			if (!hasPrimary && !hasTxt)
				throw new LanguageException("Either --primary <connection> (Shadow mode) or --txt <file> (PlainText scratch mode) is required.");

			if (hasTxt)
			{
				result.Mode = AttachMode.Txt;
				// Default actor-name from the .txt filename (sans extension) when the
				// caller did not pass one. This is the convention-over-configuration
				// step that makes `puppeteer attach --txt mywork.txt` a 3-token command.
				if (string.IsNullOrWhiteSpace(result.ActorName))
					result.ActorName = Path.GetFileNameWithoutExtension(result.TxtPath);
				// --snapshot is not required in --txt mode (no shadow to choose).
				return result;
			}

			result.Mode = AttachMode.Primary;
			if (string.IsNullOrWhiteSpace(result.ActorName))
				throw new LanguageException("--actor-name <name> is required in --primary mode");
			if (!result.Snapshot)
				throw new LanguageException("--snapshot is required in --primary mode (only mode supported today; --live arrives later)");

			return result;
		}

		// ── Library loading ────────────────────────────────────────────────

		private static Assembly[] LoadLibraries(string librariesArg, TextWriter diagnostic)
		{
			if (string.IsNullOrWhiteSpace(librariesArg)) return Array.Empty<Assembly>();

			var assemblies = new List<Assembly>();
			foreach (string raw in librariesArg.Split(',', StringSplitOptions.RemoveEmptyEntries))
			{
				string path = raw.Trim();
				string absolute = Path.GetFullPath(path);
				diagnostic.WriteLine($"[puppeteer] Loading library: {absolute}");
				Assembly asm = Assembly.LoadFrom(absolute);
				assemblies.Add(asm);
			}
			return assemblies.ToArray();
		}

		// In --txt mode, library discovery is auto by default: scan the directory
		// of the .txt file for *.dll and load all of them. This is the convention
		// that makes "low infra" real — if the user drops Domain.dll next to
		// their journal, they don't have to declare it. Explicit --libraries
		// overrides the auto-discovery.
		private static Assembly[] LoadLibrariesForTxtMode(string librariesArg, string txtPath, TextWriter diagnostic)
		{
			if (!string.IsNullOrWhiteSpace(librariesArg))
				return LoadLibraries(librariesArg, diagnostic);

			string dir = Path.GetDirectoryName(Path.GetFullPath(txtPath));
			if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
				return Array.Empty<Assembly>();

			string[] dllPaths = Directory.GetFiles(dir, "*.dll");
			if (dllPaths.Length == 0)
			{
				diagnostic.WriteLine("[puppeteer] No domain DLLs found beside the journal. Only literal-typed DSL will work.");
				return Array.Empty<Assembly>();
			}

			var assemblies = new List<Assembly>();
			foreach (string dllPath in dllPaths)
			{
				try
				{
					Assembly asm = Assembly.LoadFrom(dllPath);
					assemblies.Add(asm);
					diagnostic.WriteLine($"[puppeteer] Auto-loaded library: {Path.GetFileName(dllPath)}");
				}
				catch (Exception ex)
				{
					diagnostic.WriteLine($"[puppeteer] Skipped {Path.GetFileName(dllPath)}: {ex.Message}");
				}
			}
			return assemblies.ToArray();
		}

		private static void PrintFirstAttachTutorial(TextWriter diagnostic)
		{
			diagnostic.WriteLine("[puppeteer]");
			diagnostic.WriteLine("[puppeteer] AI agent (Claude, etc): this is your first-class scratch workspace.");
			diagnostic.WriteLine("[puppeteer]   1. show class <ClassName>   — discover what your loaded domain offers");
			diagnostic.WriteLine("[puppeteer]   2. <expr>;                  — append a command (one journal entry)");
			diagnostic.WriteLine("[puppeteer]   3. print <expr> <name>;     — query without journaling");
			diagnostic.WriteLine("[puppeteer]   4. exit                     — close the session (journal stays)");
			diagnostic.WriteLine("[puppeteer]");
			diagnostic.WriteLine("[puppeteer] Human: same commands work; type 'help' in the REPL for syntax.");
			diagnostic.WriteLine("[puppeteer]");
		}

		// ── REPL ────────────────────────────────────────────────────────────
		//
		// Three-door dispatch (signed 2026-06-01):
		//   - meta-verb (exit / quit / help / chronicle ...)   →  CLI meta
		//   - line/buffer starting with `print`                →  PerformQuery, Toon to stdout
		//   - anything else (statement or { ... })             →  PerformCommand on the shadow
		//
		// Multi-line: if a buffer opens `{` without closing it, reading continues with
		// a secondary prompt `... > ` until the brackets balance. Strings (in single
		// quotes) are ignored while counting — a `{` inside 'abc{def' does not open a level.
		//
		// Errors: a LanguageException or any other exception during PerformCmd/PerformQuery
		// is caught, logged to stderr, and the REPL stays alive. The idea is that the AI
		// is free to make mistakes without killing the process.

		// Returns the exit reason ('user-exit' / 'eof' / 'error:...') so the
		// PromptBook can journal it in CloseSession. The actor may be a Shadow.Actor
		// (Primary mode) or the PlainText actor directly (Txt mode); the REPL does not
		// distinguish.
		private static string Repl(ActorV2 actor, string actorName, string sessionTag,
			TextReader input, TextWriter stdout, TextWriter diagnostic)
		{
			string primaryPrompt = $"{actorName}-{sessionTag}> ";
			// Continuation prompt: same length to align visually in the TTY.
			string contPrompt = new string('.', Math.Max(primaryPrompt.Length - 2, 1)) + "> ";

			var buffer = new StringBuilder();
			int depth = 0;

			while (true)
			{
				diagnostic.Write(buffer.Length == 0 ? primaryPrompt : contPrompt);
				diagnostic.Flush();

				string line = input.ReadLine();
				if (line == null)
				{
					if (buffer.Length > 0)
						diagnostic.WriteLine($"[puppeteer] EOF with unbalanced block; discarding {buffer.Length} chars of pending input.");
					return "eof";
				}

				// Only at the start of an input (not in continuation) do we treat meta-verbs.
				// Inside a block, 'exit' is block content, not meta.
				if (buffer.Length == 0)
				{
					string trimmed = line.Trim();
					if (trimmed.Length == 0) continue;

					if (trimmed == "exit" || trimmed == "quit") return "user-exit";
					if (trimmed == "help") { PrintReplHelp(diagnostic); continue; }
					if (StartsWithToken(trimmed, "chronicle"))
					{
						diagnostic.WriteLine("[puppeteer] 'chronicle' is the human supervision surface; not yet implemented.");
						continue;
					}
				}

				buffer.Append(line);
				buffer.Append('\n');
				depth += CountBracketDelta(line);

				if (depth < 0)
				{
					// Bad balance: more '}' than '{'. Clear the buffer, warn, continue.
					diagnostic.WriteLine($"[puppeteer] Unbalanced brackets (extra '}}'); input discarded.");
					buffer.Clear();
					depth = 0;
					continue;
				}

				if (depth == 0)
				{
					string script = buffer.ToString().TrimEnd();
					buffer.Clear();
					DispatchDsl(actor, script, stdout, diagnostic);
				}
			}
		}

		// Counts the delta of '{' minus '}' in a line, ignoring the content of
		// string literals (delimited by ').
		private static int CountBracketDelta(string line)
		{
			int delta = 0;
			bool inString = false;
			foreach (char c in line)
			{
				if (inString)
				{
					if (c == '\'') inString = false;
				}
				else
				{
					if (c == '\'') inString = true;
					else if (c == '{') delta++;
					else if (c == '}') delta--;
				}
			}
			return delta;
		}

		// True if `s` starts with `token` FOLLOWED by something that is not an identifier-char.
		// Avoids false positives like `printer = ...` matching `print`.
		private static bool StartsWithToken(string s, string token)
		{
			if (s.Length < token.Length) return false;
			if (!s.StartsWith(token, StringComparison.Ordinal)) return false;
			if (s.Length == token.Length) return true;
			char after = s[token.Length];
			return !char.IsLetterOrDigit(after) && after != '_';
		}

		// Dispatches an already-closed script to the actor: Query if it starts with
		// `print`, Command otherwise. Toon ambient active during execution. Errors log
		// to stderr; the REPL stays alive.
		//
		// The actor may be a Shadow.Actor (Primary mode) or an actor over
		// PlainText (Txt mode); the dispatch is identical — both expose the
		// V2 API Using/PerformCommand/PerformQuery.
		private static void DispatchDsl(ActorV2 actor, string script, TextWriter stdout, TextWriter diagnostic)
		{
			string trimmedStart = script.TrimStart();
			bool isQuery = StartsWithToken(trimmedStart, "print");

			using (FormatterContext.Push(new ToonFormatter()))
			{
				try
				{
					if (isQuery)
					{
						string output = actor.Using(script).PerformQuery();
						if (!string.IsNullOrEmpty(output))
						{
							stdout.Write(output);
							if (!output.EndsWith("\n", StringComparison.Ordinal))
								stdout.WriteLine();
						}
						stdout.Flush();
					}
					else
					{
						actor.Using(script).PerformCommand();
					}
				}
				catch (LanguageException ex)
				{
					diagnostic.WriteLine($"[puppeteer] DSL error: {ex.Message}");
				}
				catch (Exception ex)
				{
					diagnostic.WriteLine($"[puppeteer] Execution error ({ex.GetType().Name}): {ex.Message}");
				}
			}
		}

		private static void PrintReplHelp(TextWriter diagnostic)
		{
			diagnostic.WriteLine("REPL meta-verbs (AI-facing):");
			diagnostic.WriteLine("  help                 Show this help.");
			diagnostic.WriteLine("  exit / quit          End the session and discard the shadow.");
			diagnostic.WriteLine("  chronicle ...        (placeholder) human supervision surface — not yet implemented.");
			diagnostic.WriteLine();
			diagnostic.WriteLine("DSL dispatch:");
			diagnostic.WriteLine("  print <expr> <name>; Run a PerformQuery on the shadow; emit TOON to stdout.");
			diagnostic.WriteLine("  <stmt>; / { ... }    Run a PerformCommand on the shadow; mutates only the shadow.");
			diagnostic.WriteLine("  Multi-line: lines inside `{ ... }` accumulate until brackets balance.");
		}

		// ── Helpers ─────────────────────────────────────────────────────────

		private static string ShortGuid()
		{
			return Guid.NewGuid().ToString("N").Substring(0, 6);
		}
	}
}
