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
	// puppeteer attach — la fase larga del CLI.
	//
	// A diferencia de los verbos one-shot (show entry / show action), attach abre
	// una SESSION hidratada: construye el actor primary, crea un Shadow aislado,
	// replay del journal del primary hasta el head, y entra a un REPL que mantiene
	// la hidratacion viva mientras la IA opera. Cada sesion queda journaled en el
	// PromptBook (el actor del CLI mismo) — open al inicio, close al exit.
	//
	// Aislamiento por construccion: TODO lo que el REPL ejecuta cae sobre el shadow.
	// El journal del primary queda intacto. La IA puede equivocarse libre.
	//
	// Layer 2 (firmado 2026-06-01): el REPL solo soporta meta-verbos (exit / help).
	// DSL execution (print / cmd / bloques) llega en Layer 3. End-to-end con un
	// dominio real (Compania-Carrito-Orden) llega en Layer 4.
	public static class AttachCommand
	{
		public static int Run(string[] args)
		{
			return RunCore(args, Console.In, Console.Out, Console.Error);
		}

		// Variante testeable: stdin/stdout/stderr inyectados. Tests pasan
		// StringReader/StringWriter para validar el flujo del REPL sin tocar
		// la consola real.
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

			// Shadow corre con storage propio in-memory por default — efimero,
			// se descarta al salir. La idea es lab; persistencia de un fork
			// llega con Topology.
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
				exitReason = Repl(shadow, parsed.ActorName, shadowId, input, stdout, diagnostic);
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

		// ── Parsing ─────────────────────────────────────────────────────────

		private sealed class AttachArgs
		{
			public string PrimaryConnection;
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

			if (string.IsNullOrWhiteSpace(result.PrimaryConnection))
				throw new LanguageException("--primary <connection> is required (e.g. \"path=C:/journals/banco\")");
			if (string.IsNullOrWhiteSpace(result.ActorName))
				throw new LanguageException("--actor-name <name> is required");
			if (!result.Snapshot)
				throw new LanguageException("--snapshot is required (only mode supported today; --live arrives later)");

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

		// ── REPL ────────────────────────────────────────────────────────────
		//
		// Dispatch de tres puertas (firmado 2026-06-01):
		//   - meta-verbo (exit / quit / help / chronicle ...)  →  meta del CLI
		//   - linea/buffer que empieza con `print`             →  PerformQuery, Toon a stdout
		//   - cualquier otra cosa (statement o { ... })        →  PerformCommand sobre el shadow
		//
		// Multi-line: si un buffer abre `{` sin cerrarlo, se sigue leyendo con prompt
		// secundario `... > ` hasta que los brackets balanceen. Strings (entre comillas
		// simples) se ignoran al contar — un `{` dentro de 'abc{def' no abre nivel.
		//
		// Errores: LanguageException u otra excepcion durante PerformCmd/PerformQuery se
		// captura, se loggea a stderr, y el REPL sigue vivo. La idea es que la IA
		// equivoque libre sin tirar el proceso.

		// Devuelve el motivo de salida ('user-exit' / 'eof' / 'error:...') para que el
		// PromptBook lo journalize en CloseSession.
		private static string Repl(Shadow shadow, string primaryName, string shadowId,
			TextReader input, TextWriter stdout, TextWriter diagnostic)
		{
			string primaryPrompt = $"{primaryName}-shadow-{shadowId}> ";
			// Continuation prompt: misma longitud para alinear visualmente en la TTY.
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

				// Solo en el inicio de un input (no en continuacion) tratamos meta-verbos.
				// Dentro de un bloque, 'exit' es contenido del bloque, no meta.
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
					// Mal balance: mas '}' que '{'. Limpiar buffer, avisar, seguir.
					diagnostic.WriteLine($"[puppeteer] Unbalanced brackets (extra '}}'); input discarded.");
					buffer.Clear();
					depth = 0;
					continue;
				}

				if (depth == 0)
				{
					string script = buffer.ToString().TrimEnd();
					buffer.Clear();
					DispatchDsl(shadow, script, stdout, diagnostic);
				}
			}
		}

		// Cuenta delta de '{' menos '}' en una linea, ignorando el contenido de
		// string literals (delimitados por ').
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

		// True si `s` empieza con `token` SEGUIDO de algo que no es identifier-char.
		// Evita falsos positivos como `printer = ...` matcheando `print`.
		private static bool StartsWithToken(string s, string token)
		{
			if (s.Length < token.Length) return false;
			if (!s.StartsWith(token, StringComparison.Ordinal)) return false;
			if (s.Length == token.Length) return true;
			char after = s[token.Length];
			return !char.IsLetterOrDigit(after) && after != '_';
		}

		// Despacha un script ya cerrado al shadow: Query si empieza con `print`,
		// Command si no. Toon ambient activo durante la ejecucion. Errores log a
		// stderr; el REPL sigue vivo.
		//
		// Cast a ActorV2 es seguro por construccion: el primary attached siempre es
		// V2 (lo construye RunCore arriba), y Shadow corre con la misma familia que
		// el primary (ver ActorHandler.CreateShadow). Si en el futuro la cadena
		// permite V1, ese path necesita su propio dispatch.
		private static void DispatchDsl(Shadow shadow, string script, TextWriter stdout, TextWriter diagnostic)
		{
			string trimmedStart = script.TrimStart();
			bool isQuery = StartsWithToken(trimmedStart, "print");

			var actor = (ActorV2)shadow.Actor;

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
