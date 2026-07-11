using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Puppeteer.EventSourcing.Follower;

namespace Puppeteer.EventSourcing.DB
{
	// PlainText backend for the actor's journal. Designed for the AI-first CLI's
	// "scratch / lab" mode: minimum infrastructure (a writable file), maximum
	// legibility (the user reads the journal with notepad/vi), CLI as the sole
	// authoritative writer. The journal is sacred — humans never edit the file
	// manually; all mutations go through CLI verbs (attach/REPL, elide).
	//
	// Format (one user PerformCommand maps to one journal entry):
	//
	//   // Puppeteer Journal V2 — PlainText backend
	//   // DO NOT EDIT MANUALLY. Use puppeteer attach --txt and puppeteer elide.
	//   // Format: <dsl-script>  // id=<idRange> kind=<...> action=<N|-> at=<iso8601>
	//   // Elided:
	//   #
	//   obj = Base(123);                 // id=1-2 kind=atom action=1 at=2026-06-02T14:22:08Z
	//   obj.DoSomething();                 // id=3-4 kind=atom action=2 at=2026-06-02T14:22:09Z
	//
	// An entry ends with `<script>  // <metadata>`. A parameter-bearing command journals
	// a Define whose DSL body renders across MULTIPLE physical lines (`define action N
	// (...) as\r{\r...\r}\rend;`) with the `// <metadata>` comment appended only after the
	// body's final segment. The reader (SplitIntoLogicalLines) reassembles an entry's
	// physical lines back into one logical entry before parsing, so a bare `end;` line is
	// never handed to the parser on its own.
	// Comment lines (starting with `//` after trim) BETWEEN entries are skipped by the
	// parser except for the special header keys (Elided:). Blank lines also skipped.
	//
	// Entry IDs are preserved with gaps after elision (Option B signed). When
	// `puppeteer elide --entry N` physically removes a line, the header's
	// `Elided:` list records the dropped IDs; subsequent entries continue from
	// the highest-seen ID + 1, never reusing.
	//
	// Reaction registry / checkpoints / materialization: kept in-memory (reused
	// from the InMemory backend's auxiliary stores). PlainText is the journal of
	// commands; reactions are runtime decoration declared per session. This is
	// intentional for the scratch/lab use case; reactions that must persist
	// across sessions should use FileSystem or SQL backends.
	internal sealed class DiaryStorageTxt : DiaryStorage
	{
		internal const string HEADER_TITLE = "// Puppeteer Journal V2 — PlainText backend";
		internal const string HEADER_WARNING = "// DO NOT EDIT MANUALLY. Use puppeteer attach --txt and puppeteer elide.";
		internal const string HEADER_FORMAT_HINT = "// Format: <dsl-script>  // id=<idRange> kind=<...> action=<N|-> at=<iso8601>";
		internal const string HEADER_ELIDED_PREFIX = "// Elided:";
		internal const string CONTENT_SEPARATOR = "#"; // marks the end of header, beginning of entries

		private readonly string filePath;
		private readonly List<EventData> events = new List<EventData>();
		private readonly SortedSet<long> elidedIds = new SortedSet<long>();
		private long nextEntryId = 1;

		// Reaction registry + checkpoints + frontiers live in-memory only. Lost
		// between sessions; the scratch use case re-declares per session.
		private readonly Dictionary<int, long> followerCheckpoints = new Dictionary<int, long>();
		private readonly Dictionary<string, long> reactionRegistry = new Dictionary<string, long>();
		private readonly Dictionary<(long, int), (long detected, long confirmed)> reactionCheckpoints = new Dictionary<(long, int), (long, long)>();
		private readonly Dictionary<long, (long highWater, long closedFrontier)> reactionFrontiers = new Dictionary<long, (long, long)>();
		private readonly Dictionary<long, string> reactionMatchSnapshots = new Dictionary<long, string>();
		private long nextReactionId = 1;

		internal string FilePath => filePath;
		internal IReadOnlySet<long> ElidedIds => elidedIds;

		internal DiaryStorageTxt(IActorEventJournalClient eventJournalClient, string connectionString)
			: base(eventJournalClient, connectionString)
		{
			// Connection string is the file path directly, or `path=<file>` for
			// uniformity with other backends. Both are accepted.
			string path = connectionString;
			const string PathPrefix = "path=";
			if (path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase))
				path = path.Substring(PathPrefix.Length);
			filePath = path;

			eventElisionStorage = new EventElisionStorageInMemory(eventJournalClient);
			eventMaterializationStorage = new EventMaterializationStorageInMemory(eventJournalClient);
			materializationCheckpointStorage = new MaterializationCheckpointStorageInMemory(eventJournalClient);
			outboxStorage = new OutboxStorageInMemory();

			VerifyTamperState();
			LoadFromFile();

			// Materialize the file with header on attach when the user opens a
			// brand-new path. The .txt being the canonical journal implies the
			// user intends to start one here; making it exist immediately is the
			// honest representation of that intent. If the session does nothing
			// and exits, an empty journal with just the header is what remains —
			// the operator can delete it manually if they changed their mind.
			EnsureFileWithHeader();
		}

		// Tamper detection: a sidecar `<file>.sha` records the SHA256 of the journal
		// at last clean close. At open we recompute and compare. A mismatch means
		// somebody (or something) modified the file outside the CLI — the sacred-
		// file invariant was bypassed. We do not block; we report. The operator
		// who edited manually accepts the consequence consciously.
		//
		// The sidecar is rewritten on every Append/Elide so it always reflects the
		// last CLI-authorized state.
		private string ShaSidecarPath => filePath + ".sha";

		private void VerifyTamperState()
		{
			if (!File.Exists(filePath)) return;        // brand new — nothing to verify
			if (!File.Exists(ShaSidecarPath)) return;  // no prior sidecar — first attach to an existing file

			try
			{
				string expectedSha = File.ReadAllText(ShaSidecarPath).Trim();
				string actualSha = ComputeSha256();
				if (!string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
				{
					EventJournalClient.Logger?.Error(exception: null, message:
						$"PlainText journal '{filePath}' was modified externally since last attach. " +
						"The sacred-file invariant was bypassed. Continuing with the current file contents; " +
						"if this was unintentional, restore from a backup before further writes.");
				}
			}
			catch (Exception ex)
			{
				EventJournalClient.Logger?.Error(exception: null, message:$"PlainText tamper-check skipped: {ex.Message}");
			}
		}

		private void UpdateShaSidecar()
		{
			try
			{
				string sha = ComputeSha256();
				File.WriteAllText(ShaSidecarPath, sha);
			}
			catch (Exception ex)
			{
				EventJournalClient.Logger?.Error(exception: null, message:$"PlainText sidecar write skipped: {ex.Message}");
			}
		}

		private string ComputeSha256()
		{
			using var sha = System.Security.Cryptography.SHA256.Create();
			using var stream = File.OpenRead(filePath);
			byte[] hash = sha.ComputeHash(stream);
			return Convert.ToHexString(hash).ToLowerInvariant();
		}

		private void LoadFromFile()
		{
			events.Clear();
			elidedIds.Clear();
			nextEntryId = 1;

			if (!File.Exists(filePath))
				return;

			string[] lines = File.ReadAllLines(filePath);
			long maxIdSeen = 0;

			foreach (LogicalLine logical in SplitIntoLogicalLines(lines))
			{
				if (!logical.IsEntry)
				{
					// Header / separator / blank. Parse the Elided list, ignore the rest.
					string line = logical.RawLines[0].Trim();
					if (line.StartsWith(HEADER_ELIDED_PREFIX, StringComparison.Ordinal))
					{
						string payload = line.Substring(HEADER_ELIDED_PREFIX.Length).Trim();
						if (payload.Length > 0)
						{
							foreach (string token in payload.Split(','))
							{
								string t = token.Trim();
								if (t.Length == 0) continue;
								if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out long elidedId))
								{
									elidedIds.Add(elidedId);
									if (elidedId > maxIdSeen) maxIdSeen = elidedId;
								}
							}
						}
					}
					continue;
				}

				// Entry: the writer renders a Define/atom body with embedded CR
				// separators (`define … as\r{…}\rend;`), so File.ReadAllLines splits one
				// logical entry across several physical lines with the `// id=…` metadata
				// on the last one. SplitIntoLogicalLines rejoined them; parse the whole.
				ParsedLine parsed = ParseEntryLine(logical.Reassembled);
				if (parsed == null) continue;

				foreach (var ev in parsed.ProduceEvents(EventDataPool))
				{
					events.Add(ev);
					if (ev.EntryId > maxIdSeen) maxIdSeen = ev.EntryId;
				}
			}

			nextEntryId = maxIdSeen + 1;
		}

		// One logical journal element: either a passthrough line (header / separator /
		// blank) or an entry that may span several physical lines. See
		// SplitIntoLogicalLines for why entries can be multi-line.
		private sealed class LogicalLine
		{
			public bool IsEntry;
			public List<string> RawLines;
			// Physical lines rejoined with the writer's in-body separator (CR). The
			// parser treats CR/LF as whitespace, so this faithfully reconstructs the
			// `define … as\r{…}\rend;  // <meta>` sentence the writer emitted.
			public string Reassembled => string.Join("\r", RawLines);

			public static LogicalLine Other(string raw) => new LogicalLine { IsEntry = false, RawLines = new List<string> { raw } };
			public static LogicalLine Entry(List<string> raws) => new LogicalLine { IsEntry = true, RawLines = raws };
		}

		// Groups the file's physical lines into logical elements. A parameter-bearing
		// command journals a Define whose DSL body is rendered with embedded CR
		// separators (`define action N (…) as\r{\r…\r}\rend;`) and the `// id=… kind=…
		// at=…` metadata comment appended only after the body's final segment. Because
		// File.ReadAllLines splits on \r, \n and \r\n, that single entry arrives as
		// several physical lines — only the last carrying metadata. We accumulate
		// physical lines into one entry until a line carries a valid metadata tail
		// (IsEntryTerminator), so the parser sees the whole sentence instead of a
		// trailing fragment like `end;`. Header/separator/blank lines that appear
		// BETWEEN entries pass through unchanged; the same shapes appearing WITHIN an
		// entry's body are kept as continuation lines.
		private static IEnumerable<LogicalLine> SplitIntoLogicalLines(string[] lines)
		{
			List<string> pending = null;

			foreach (string raw in lines)
			{
				if (pending == null)
				{
					string line = raw.Trim();
					if (line.Length == 0 || line == CONTENT_SEPARATOR || line.StartsWith("//", StringComparison.Ordinal))
					{
						yield return LogicalLine.Other(raw);
						continue;
					}

					pending = new List<string> { raw };
					if (IsEntryTerminator(raw))
					{
						yield return LogicalLine.Entry(pending);
						pending = null;
					}
					continue;
				}

				// Mid-entry: every physical line (including blanks or `//`-prefixed body
				// fragments) belongs to the current entry until its metadata tail closes it.
				pending.Add(raw);
				if (IsEntryTerminator(raw))
				{
					yield return LogicalLine.Entry(pending);
					pending = null;
				}
			}

			// An unterminated tail (metadata never seen) is malformed; surface it so the
			// caller's ParseEntryLine can null it out / preserve it rather than dropping.
			if (pending != null)
				yield return LogicalLine.Entry(pending);
		}

		// True when this physical line ends an entry: it carries a metadata comment
		// whose mandatory keys (id, kind, at) are all present after the last `//`.
		// Body fragments never satisfy this (they either have no `//` or the tail does
		// not parse as journal metadata), so it is a safe entry-boundary marker.
		private static bool IsEntryTerminator(string raw)
		{
			int metaIdx = raw.LastIndexOf("//", StringComparison.Ordinal);
			if (metaIdx < 0) return false;
			var keys = ParseMetadata(raw.Substring(metaIdx + 2).Trim());
			return keys.ContainsKey("id") && keys.ContainsKey("kind") && keys.ContainsKey("at");
		}

		// Parses a single entry line: <script>  TAB+  // id=<range> kind=<...> action=<N|-> at=<iso>
		// Returns null if the line cannot be parsed (treated as a malformed/comment
		// line and skipped). The metadata is mandatory — we never accept bare DSL
		// without provenance.
		private static ParsedLine ParseEntryLine(string raw)
		{
			int metaIdx = raw.LastIndexOf("//", StringComparison.Ordinal);
			if (metaIdx < 0) return null;

			string body = raw.Substring(0, metaIdx).TrimEnd();
			string meta = raw.Substring(metaIdx + 2).Trim();
			if (body.Length == 0) return null;

			var keys = ParseMetadata(meta);
			if (!keys.TryGetValue("id", out string idRange) ||
				!keys.TryGetValue("kind", out string kind) ||
				!keys.TryGetValue("at", out string atStr))
				return null;

			if (!DateTime.TryParse(atStr, CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime occurredAt))
				return null;

			(long firstId, long secondId) = ParseIdRange(idRange);
			if (firstId <= 0) return null;

			int actionId = 0;
			if (keys.TryGetValue("action", out string actionStr) && actionStr != "-")
				int.TryParse(actionStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out actionId);

			string exposeData = keys.TryGetValue("expose", out string expRaw) && expRaw.Length > 0
				? UnescapeMetaValue(expRaw)
				: null;

			return new ParsedLine
			{
				Body = body,
				Kind = kind,
				FirstId = firstId,
				SecondId = secondId,
				ActionId = actionId,
				OccurredAt = occurredAt,
				ExposeData = exposeData,
				ArgumentsForInvocation = keys.TryGetValue("args", out string argsStr) ? UnescapeMetaValue(argsStr) : null
			};
		}

		private static Dictionary<string, string> ParseMetadata(string meta)
		{
			// Simple key=value pairs separated by spaces. Values may be quoted to
			// contain spaces (only used for args and expose). Keys are lowercase.
			var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			int i = 0;
			while (i < meta.Length)
			{
				while (i < meta.Length && char.IsWhiteSpace(meta[i])) i++;
				if (i >= meta.Length) break;
				int keyStart = i;
				while (i < meta.Length && meta[i] != '=' && !char.IsWhiteSpace(meta[i])) i++;
				if (i >= meta.Length || meta[i] != '=') break;
				string key = meta.Substring(keyStart, i - keyStart);
				i++; // skip '='

				string value;
				if (i < meta.Length && meta[i] == '"')
				{
					i++;
					int valStart = i;
					while (i < meta.Length && meta[i] != '"') i++;
					value = meta.Substring(valStart, i - valStart);
					if (i < meta.Length) i++;
				}
				else
				{
					int valStart = i;
					while (i < meta.Length && !char.IsWhiteSpace(meta[i])) i++;
					value = meta.Substring(valStart, i - valStart);
				}
				result[key] = value;
			}
			return result;
		}

		private static (long, long) ParseIdRange(string range)
		{
			int dash = range.IndexOf('-');
			if (dash < 0)
			{
				return long.TryParse(range, NumberStyles.Integer, CultureInfo.InvariantCulture, out long single)
					? (single, 0)
					: (0, 0);
			}
			string first = range.Substring(0, dash);
			string second = range.Substring(dash + 1);
			long.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out long f);
			long.TryParse(second, NumberStyles.Integer, CultureInfo.InvariantCulture, out long s);
			return (f, s);
		}

		private static string EscapeMetaValue(string s)
		{
			if (s == null) return null;
			return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
		}

		private static string UnescapeMetaValue(string s)
		{
			if (s == null) return null;
			return s.Replace("\\\"", "\"").Replace("\\\\", "\\");
		}

		private sealed class ParsedLine
		{
			public string Body;
			public string Kind;
			public long FirstId;
			public long SecondId;
			public int ActionId;
			public DateTime OccurredAt;
			public string ExposeData;
			public string ArgumentsForInvocation;

			public IEnumerable<EventData> ProduceEvents(EventDataPool pool)
			{
				switch (Kind)
				{
					case "script":
						{
							var ev = pool.RentScript();
							ev.EntryId = FirstId;
							ev.OccurredAt = OccurredAt;
							ev.Script = Body;
							ev.ExposeData = ExposeData;
							yield return ev;
							break;
						}
					case "define":
						{
							var ev = pool.RentDefine();
							ev.EntryId = FirstId;
							ev.OccurredAt = OccurredAt;
							ev.ActionId = ActionId;
							ev.DefineStatementText = Body;
							ev.ExposeData = ExposeData;
							yield return ev;
							break;
						}
					case "invocation":
						{
							var ev = pool.RentAction();
							ev.EntryId = FirstId;
							ev.OccurredAt = OccurredAt;
							ev.ActionId = ActionId;
							ev.Arguments = ArgumentsForInvocation ?? "{}";
							ev.ExposeData = ExposeData;
							yield return ev;
							break;
						}
					case "atom":
						{
							// Atom: one user command journaled as a Define + Invocation pair.
							// The line shows the Define body; the Invocation has empty params
							// (V2 porous — literals baked into the Define body).
							var define = pool.RentDefine();
							define.EntryId = FirstId;
							define.OccurredAt = OccurredAt;
							define.ActionId = ActionId;
							define.DefineStatementText = Body;
							define.ExposeData = null;
							yield return define;

							var invocation = pool.RentAction();
							invocation.EntryId = SecondId > 0 ? SecondId : FirstId + 1;
							invocation.OccurredAt = OccurredAt;
							invocation.ActionId = ActionId;
							invocation.Arguments = ArgumentsForInvocation ?? "{}";
							invocation.ExposeData = ExposeData;
							yield return invocation;
							break;
						}
				}
			}
		}

		// ── Writes ──────────────────────────────────────────────────────────

		protected internal override void WriteScriptEntry(long entryId, string script, DateTime now, string exposeData = null)
		{
			ArgumentNullException.ThrowIfNull(script);

			long id = AssignAndAdvanceCounter(entryId);
			var ev = EventDataPool.RentScript();
			ev.EntryId = id;
			ev.Script = script;
			ev.OccurredAt = now;
			ev.ExposeData = exposeData;
			events.Add(ev);

			AppendLine(script, $"id={id} kind=script action=- at={FormatIso(now)}" + ExposeMeta(exposeData));

			if (OnRecordWritten != null)
			{
				byte[] record = EncodeScriptRecord(id, script, now, exposeData);
				OnRecordWritten.Invoke(id, record);
			}
		}

		protected internal override Task WriteScriptEntryAsync(long entryId, string script, DateTime now, string exposeData = null)
		{
			WriteScriptEntry(entryId, script, now, exposeData);
			return Task.CompletedTask;
		}

		protected internal override void WriteDefineEntry(int actionId, string defineStatementText, long entryId, DateTime now, string exposeData = null)
		{
			ArgumentNullException.ThrowIfNull(defineStatementText);

			long id = AssignAndAdvanceCounter(entryId);
			var ev = EventDataPool.RentDefine();
			ev.EntryId = id;
			ev.ActionId = actionId;
			ev.DefineStatementText = defineStatementText;
			ev.OccurredAt = now;
			ev.ExposeData = exposeData;
			events.Add(ev);

			AppendLine(defineStatementText, $"id={id} kind=define action={actionId} at={FormatIso(now)}" + ExposeMeta(exposeData));

			if (OnRecordWritten != null)
			{
				byte[] record = EncodeDefineRecord(actionId, defineStatementText, id, now, exposeData);
				OnRecordWritten.Invoke(id, record);
			}
		}

		protected internal override Task WriteDefineEntryAsync(int actionId, string defineStatementText, long entryId, DateTime now, string exposeData = null)
		{
			WriteDefineEntry(actionId, defineStatementText, entryId, now, exposeData);
			return Task.CompletedTask;
		}

		protected internal override void WriteInvocationEntry(int actionId, long entryId, DateTime now, string arguments, string exposeData = null)
		{
			ArgumentNullException.ThrowIfNull(arguments);

			long id = AssignAndAdvanceCounter(entryId);
			var ev = EventDataPool.RentAction();
			ev.EntryId = id;
			ev.ActionId = actionId;
			ev.Arguments = arguments;
			ev.OccurredAt = now;
			ev.ExposeData = exposeData;
			events.Add(ev);

			// For an invocation-only entry, we render `invoke action:<N>` as the body
			// (no DSL body is available — that lives in the Define). The metadata
			// carries the arguments.
			string body = $"invoke action:{actionId}";
			AppendLine(body, $"id={id} kind=invocation action={actionId} at={FormatIso(now)} args={EscapeMetaValue(arguments)}" + ExposeMeta(exposeData));

			if (OnRecordWritten != null)
			{
				byte[] record = EncodeInvocationRecord(actionId, id, now, arguments, exposeData);
				OnRecordWritten.Invoke(id, record);
			}
		}

		protected internal override Task WriteInvocationEntryAsync(int actionId, long entryId, DateTime now, string arguments, string exposeData = null)
		{
			WriteInvocationEntry(actionId, entryId, now, arguments, exposeData);
			return Task.CompletedTask;
		}

		// Atomic write of Define + Invocation. In PlainText this is the dominant
		// path (V2 porous: every user command becomes Define + first Invocation).
		// We render ONE line: the Define body (with literals baked in) plus a
		// metadata range id=<firstId>-<secondId>.
		protected internal override void WriteDefineWithFirstInvocation(int actionId, string defineStatementText, long defineEntryId, long invocationEntryId, DateTime now, string arguments, string exposeData = null)
		{
			ArgumentNullException.ThrowIfNull(defineStatementText);
			ArgumentNullException.ThrowIfNull(arguments);

			long firstId = AssignAndAdvanceCounter(defineEntryId);
			long secondId = AssignAndAdvanceCounter(invocationEntryId);

			var define = EventDataPool.RentDefine();
			define.EntryId = firstId;
			define.ActionId = actionId;
			define.DefineStatementText = defineStatementText;
			define.OccurredAt = now;
			define.ExposeData = null;
			events.Add(define);

			var invocation = EventDataPool.RentAction();
			invocation.EntryId = secondId;
			invocation.ActionId = actionId;
			invocation.Arguments = arguments;
			invocation.OccurredAt = now;
			invocation.ExposeData = exposeData;
			events.Add(invocation);

			string body = ExtractAtomBody(defineStatementText);
			string meta = $"id={firstId}-{secondId} kind=atom action={actionId} at={FormatIso(now)}";
			if (!string.IsNullOrEmpty(arguments) && arguments != "{}")
				meta += " args=" + EscapeMetaValue(arguments);
			meta += ExposeMeta(exposeData);
			AppendLine(body, meta);

			if (OnRecordWritten != null)
			{
				byte[] defineRecord = EncodeDefineRecord(actionId, defineStatementText, firstId, now, null);
				byte[] invocationRecord = EncodeInvocationRecord(actionId, secondId, now, arguments, exposeData);
				OnRecordWritten.Invoke(firstId, defineRecord);
				OnRecordWritten.Invoke(secondId, invocationRecord);
			}
		}

		protected internal override Task WriteDefineWithFirstInvocationAsync(int actionId, string defineStatementText, long defineEntryId, long invocationEntryId, DateTime now, string arguments, string exposeData = null)
		{
			WriteDefineWithFirstInvocation(actionId, defineStatementText, defineEntryId, invocationEntryId, now, arguments, exposeData);
			return Task.CompletedTask;
		}

		// Extract the body of a Define statement so the .txt shows DSL-shaped text
		// rather than the verbose `define action N (...) as <body> end;` wrapper.
		// Fallback: if the wrapper isn't recognized, emit the full statement.
		private static string ExtractAtomBody(string defineStatementText)
		{
			if (string.IsNullOrEmpty(defineStatementText)) return defineStatementText;

			int asIdx = defineStatementText.IndexOf(" as ", StringComparison.Ordinal);
			if (asIdx < 0) return defineStatementText;
			int endIdx = defineStatementText.LastIndexOf(" end;", StringComparison.Ordinal);
			if (endIdx < asIdx) endIdx = defineStatementText.LastIndexOf("end;", StringComparison.Ordinal);
			if (endIdx < asIdx) return defineStatementText;

			string body = defineStatementText.Substring(asIdx + 4, endIdx - (asIdx + 4)).Trim();
			return body;
		}

		// The caller (ActorHandler) manages the canonical entry-id counter and passes
		// the next ID via the entryId parameter on each Write*. Storages with their
		// own counter (InMemory) ignore it; PlainText honors it so the actor's
		// CurrentEntryId stays in sync with what was journaled. We still track our
		// own nextEntryId for ListActorNames / rehydrate continuation.
		private long AssignAndAdvanceCounter(long entryId)
		{
			long id = entryId > 0 ? entryId : nextEntryId;
			if (id >= nextEntryId) nextEntryId = id + 1;
			return id;
		}

		private static string FormatIso(DateTime dt)
		{
			return dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
		}

		private static string ExposeMeta(string exposeData)
		{
			if (string.IsNullOrEmpty(exposeData)) return "";
			return " expose=" + EscapeMetaValue(exposeData);
		}

		private void AppendLine(string body, string meta)
		{
			EnsureFileWithHeader();
			string line = body.TrimEnd() + "  // " + meta;
			File.AppendAllText(filePath, line + Environment.NewLine);
			UpdateShaSidecar();
		}

		private void EnsureFileWithHeader()
		{
			if (File.Exists(filePath) && new FileInfo(filePath).Length > 0) return;

			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath)));
			var sb = new StringBuilder();
			sb.AppendLine(HEADER_TITLE);
			sb.AppendLine(HEADER_WARNING);
			sb.AppendLine(HEADER_FORMAT_HINT);
			sb.AppendLine(HEADER_ELIDED_PREFIX + " " + FormatElidedList());
			sb.AppendLine(CONTENT_SEPARATOR);
			File.WriteAllText(filePath, sb.ToString());
			UpdateShaSidecar();
		}

		private string FormatElidedList()
		{
			if (elidedIds.Count == 0) return "";
			return string.Join(",", elidedIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
		}

		// ── Rehydration ─────────────────────────────────────────────────────

		protected internal override long RehydrateFromEvent(long afterEntryId, bool includeExposeData = false)
		{
			EventJournalClient.IsNew = events.Count == 0;

			long lastEntryId = afterEntryId;
			bool firstPassCompleted = false;
			bool forcedToEnd = false;

			while (!forcedToEnd && !firstPassCompleted && EventJournalClient.CanContinueReplay(lastEntryId))
			{
				IEnumerable<EventData> orderedEvents = events
					.Where(evt => evt.EntryId > lastEntryId)
					.OrderBy(evt => evt.EntryId);

				int eventCount = orderedEvents.Count();
				EventJournalClient.BeginJournalReplay(eventCount);

				foreach (var evt in orderedEvents)
				{
					if (!EventJournalClient.CanContinueReplay(lastEntryId))
					{
						forcedToEnd = true;
						break;
					}

					if (elidedIds.Contains(evt.EntryId) || eventElisionStorage.IsEventElided(evt.EntryId))
					{
						lastEntryId = evt.EntryId;
						continue;
					}

					if (evt is DefineEventData defineEvt)
					{
						EventJournalClient.AddKnownActionFromDefine(defineEvt.ActionId, defineEvt.DefineStatementText);
						lastEntryId = evt.EntryId;
						continue;
					}

					EventData tempEvent = CloneForReplay(evt);
					EventJournalClient.ReplayEvent(tempEvent);
					lastEntryId = evt.EntryId;
				}

				firstPassCompleted = true;
			}

			EventJournalClient.EndJournalReplay(forcedToEnd);

			// Continue past any physically-elided IDs above the last live entry so
			// the actor's counter starts beyond every ID ever issued — including
			// the gaps. Without this, a re-attach after an Elide could let the
			// actor re-issue the elided ID for a fresh entry.
			foreach (long elidedId in elidedIds)
				if (elidedId > lastEntryId) lastEntryId = elidedId;

			return lastEntryId;
		}

		protected internal override Task<long> RehydrateFromEventAsync(long afterEntryId, bool includeExposeData = false)
		{
			return Task.FromResult(RehydrateFromEvent(afterEntryId, includeExposeData));
		}

		private EventData CloneForReplay(EventData evt)
		{
			EventData clone;
			if (evt is ScriptEventData s)
			{
				var sc = EventDataPool.RentScript();
				sc.Script = s.Script;
				clone = sc;
			}
			else if (evt is ActionEventData a)
			{
				var ac = EventDataPool.RentAction();
				ac.ActionId = a.ActionId;
				ac.Arguments = a.Arguments;
				clone = ac;
			}
			else
			{
				throw new LanguageException($"Unknown event kind for replay: {evt.GetType().Name}");
			}
			clone.EntryId = evt.EntryId;
			clone.OccurredAt = evt.OccurredAt;
			clone.ExposeData = evt.ExposeData;
			return clone;
		}

		// ── IActorIntrospection support ─────────────────────────────────────

		protected internal override void ReadRecordsAfter(long afterEntryId, List<MaterializationRecord> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			if (afterEntryId < 0) throw new LanguageException($"afterEntryId {afterEntryId} must be zero or greater.");

			result.Clear();
			foreach (var evt in events)
			{
				if (evt.EntryId <= afterEntryId) continue;
				result.Add(ProjectToRecord(evt));
			}
			result.Sort((a, b) => a.EntryId.CompareTo(b.EntryId));
		}

		protected internal override Task ReadRecordsAfterAsync(long afterEntryId, List<MaterializationRecord> result)
		{
			ReadRecordsAfter(afterEntryId, result);
			return Task.CompletedTask;
		}

		private static MaterializationRecord ProjectToRecord(EventData evt)
		{
			if (evt is ScriptEventData s)
				return new MaterializationRecord(s.EntryId, MaterializationRecordKind.Script, s.OccurredAt, s.Script, 0, null, null, s.ExposeData);
			if (evt is DefineEventData d)
				return new MaterializationRecord(d.EntryId, MaterializationRecordKind.Define, d.OccurredAt, null, d.ActionId, null, d.DefineStatementText, d.ExposeData);
			if (evt is ActionEventData a)
				return new MaterializationRecord(a.EntryId, MaterializationRecordKind.Invocation, a.OccurredAt, null, a.ActionId, a.Arguments, null, a.ExposeData);
			throw new LanguageException($"Unknown EventData kind: {evt.GetType().Name}");
		}

		protected internal override void ReadReactionRegistry(List<MaterializationReactionDefinition> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			result.Clear();
			foreach (var kvp in reactionRegistry)
				result.Add(new MaterializationReactionDefinition(kvp.Value, kvp.Key));
			result.Sort((a, b) => a.ReactionId.CompareTo(b.ReactionId));
		}

		protected internal override void ReadReactionCheckpoints(List<MaterializationReactionCheckpoint> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			result.Clear();
			foreach (var kvp in reactionCheckpoints)
				result.Add(new MaterializationReactionCheckpoint(kvp.Key.Item1, kvp.Key.Item2, kvp.Value.detected, kvp.Value.confirmed));
		}

		// ── Reaction registry / checkpoints (in-memory only) ────────────────

		protected internal override long GetLastProcessedEntryId(int followerId)
		{
			return followerCheckpoints.TryGetValue(followerId, out long entryId) ? entryId : 0;
		}

		protected internal override void SaveLastProcessedEntryId(int followerId, long entryId)
		{
			followerCheckpoints[followerId] = entryId;
		}

		protected internal override long GetOrCreateReactionId(string formattedReaction)
		{
			ArgumentNullException.ThrowIfNull(formattedReaction);
			if (reactionRegistry.TryGetValue(formattedReaction, out long existing))
				return existing;
			long newId = nextReactionId++;
			reactionRegistry[formattedReaction] = newId;
			return newId;
		}

		protected internal override (long detected, long confirmed) GetReactionCheckpoint(long reactionId, int seekLevel)
		{
			if (reactionId <= 0) throw new LanguageException("reactionId must be greater than zero.");
			if (seekLevel < 0) throw new LanguageException("seekLevel must be zero or greater.");
			return reactionCheckpoints.TryGetValue((reactionId, seekLevel), out var cp) ? cp : (0, 0);
		}

		protected internal override void SaveReactionConfirmedCheckpoint(long reactionId, int seekLevel, long entryId)
		{
			if (reactionId <= 0) throw new LanguageException("reactionId must be greater than zero.");
			if (seekLevel < 0) throw new LanguageException("seekLevel must be zero or greater.");
			var cur = reactionCheckpoints.TryGetValue((reactionId, seekLevel), out var c) ? c : (0L, 0L);
			reactionCheckpoints[(reactionId, seekLevel)] = (cur.Item1, entryId);
		}

		protected internal override long GetReactionLastProcessedEntryId(long reactionId, int pattern)
		{
			return reactionCheckpoints.TryGetValue((reactionId, pattern), out var cp) ? cp.detected : 0;
		}

		protected internal override void SaveReactionLastProcessedEntryId(long reactionId, int pattern, long entryId)
		{
			var cur = reactionCheckpoints.TryGetValue((reactionId, pattern), out var c) ? c : (0L, 0L);
			reactionCheckpoints[(reactionId, pattern)] = (entryId, cur.Item2);
		}

		protected internal override bool MarkEventsAsElidedWithCheckpoint(CheckpointCommit commit)
		{
			ArgumentNullException.ThrowIfNull(commit);
			// Logical elision via reactions — persisted only in-memory. Survives the
			// current session; lost on re-attach. Physical elision (the path that
			// survives across sessions) is `puppeteer elide --entry N`, which
			// rewrites the .txt file.
			long reactionId = commit.ReactionId;
			long[] eventIds = commit.EventIds;
			DateTime timestamp = commit.Timestamp;
			CheckpointVector newCheckpoint = commit.CheckpointVector;

			bool isGreater = false;
			for (int seekLevel = 0; seekLevel < newCheckpoint.SeekCount; seekLevel++)
			{
				long newDetected = newCheckpoint.Get(seekLevel);
				var (currentDetected, _) = GetReactionCheckpoint(reactionId, seekLevel);
				if (newDetected > currentDetected) { isGreater = true; break; }
				if (newDetected < currentDetected) { isGreater = false; break; }
			}
			if (!isGreater) return false;

			eventElisionStorage.MarkEventsAsElided(eventIds, (int)reactionId, timestamp);
			for (int seekLevel = 0; seekLevel < newCheckpoint.SeekCount; seekLevel++)
			{
				var (_, confirmed) = GetReactionCheckpoint(reactionId, seekLevel);
				long newDetected = newCheckpoint.Get(seekLevel);
				reactionCheckpoints[(reactionId, seekLevel)] = (newDetected, confirmed);
			}
			return true;
		}

		// ── Surface that PlainText explicitly does not support ──────────────

		internal override void ChangePrimaryKey()
		{
			throw new LanguageException("ChangePrimaryKey is not supported by the PlainText backend. Use a FileSystem or SQL backend for migration workflows.");
		}

		protected internal override MemoryStream Archive(DateTime startDate, DateTime endDate)
		{
			throw new LanguageException("Archive is not supported by the PlainText backend.");
		}

		protected internal override IEnumerable<string> ListActorNames(string name)
		{
			// PlainText is one-file-one-actor by design; there is no shared registry.
			yield break;
		}

		protected internal override void Trim(DateTime trimmedDown)
		{
			throw new LanguageException("Date-based Trim is not supported by the PlainText backend. Use `puppeteer elide` for entry-targeted removal.");
		}

		// Physical elision API for the `puppeteer elide` CLI verb. Removes the line
		// for the given entry ID range from the file, updates the header's Elided
		// list, and keeps the in-memory events list in sync. Atomicity: writes a
		// new file and replaces; on failure the original is intact.
		internal void PhysicallyElideEntries(IEnumerable<long> entryIds, bool keepBackup)
		{
			ArgumentNullException.ThrowIfNull(entryIds);
			if (!File.Exists(filePath))
				throw new LanguageException($"PlainText journal file not found: {filePath}");

			var ids = new SortedSet<long>(entryIds);
			if (ids.Count == 0) return;

			string[] originalLines = File.ReadAllLines(filePath);
			var resultLines = new List<string>();
			bool headerSeen = false;

			foreach (LogicalLine logical in SplitIntoLogicalLines(originalLines))
			{
				if (!logical.IsEntry)
				{
					string raw = logical.RawLines[0];
					if (raw.Trim().StartsWith(HEADER_ELIDED_PREFIX, StringComparison.Ordinal))
					{
						foreach (var id in ids) elidedIds.Add(id);
						resultLines.Add(HEADER_ELIDED_PREFIX + " " + FormatElidedList());
						headerSeen = true;
						continue;
					}
					resultLines.Add(raw);
					continue;
				}

				ParsedLine parsed = ParseEntryLine(logical.Reassembled);
				if (parsed == null)
				{
					// Unparseable/malformed entry: preserve every physical line verbatim.
					resultLines.AddRange(logical.RawLines);
					continue;
				}

				bool drop = ids.Contains(parsed.FirstId) || (parsed.SecondId > 0 && ids.Contains(parsed.SecondId));
				if (drop) continue; // drops ALL physical lines of a multi-line entry

				resultLines.AddRange(logical.RawLines);
			}

			if (!headerSeen)
				throw new LanguageException("PlainText journal file is missing the Elided header line; refusing to elide on a corrupt file.");

			if (keepBackup)
			{
				string backup = filePath + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".bak";
				File.Copy(filePath, backup, overwrite: false);
			}

			File.WriteAllLines(filePath, resultLines);
			events.RemoveAll(e => ids.Contains(e.EntryId));
			UpdateShaSidecar();
		}
	}
}
