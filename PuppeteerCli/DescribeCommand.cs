using System;
using System.IO;
using Puppeteer.EventSourcing.Interpreter.Formatters;

namespace PuppeteerCli
{
	// `puppeteer describe` — the AI's primary onboarding contract. Emits the full
	// CLI surface in TOON (machine-readable). The AI cold-starts by invoking this
	// in a fresh chat and parses the response to know every verb, flag, default,
	// and DSL syntax basic.
	//
	// Why a separate verb instead of just --help:
	//   --help is for humans (prose, formatting choices). `describe` is the
	//   structured contract — same source of truth as the code, never drifts.
	//   The AI prefers describe because parsing TOON is cheaper than parsing
	//   narrative.
	//
	// What it lives next to:
	//   - Program.cs PrintUsage() / PrintLanding() — human-facing narrative.
	//   - This file — machine-readable contract.
	//   Both derive from the same source-of-truth declarations below; if either
	//   gets edited, the other should follow.
	public static class DescribeCommand
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

			// No flags yet. `--format=narrative` (for a guided human intro) is a
			// future extension; today only the default machine-readable form.
			if (args.Length > 0)
			{
				diagnostic.WriteLine($"[puppeteer] describe takes no arguments yet; got '{args[0]}'.");
				return 1;
			}

			var sb = new System.Text.StringBuilder();
			var f = new ToonFormatter();
			f.BeginDocument(sb);

			f.Field("cli", "puppeteer");
			f.Field("description", "AI-native CLI for Puppeteer — bimodal journal operation surface.");

			// ── verbs ──────────────────────────────────────────────────────
			f.BeginCollection();
			EmitAttachTxt(f);
			EmitAttachPrimary(f);
			EmitDescribe(f);
			EmitElide(f);
			EmitShowEntry(f);
			EmitShowAction(f);
			EmitIssueInvitation(f);
			EmitChroniclePlaceholder(f);
			f.EndCollection("verbs");

			// ── DSL syntax basics ──────────────────────────────────────────
			f.BeginCollection();
			EmitDslShape(f, "assignment", "x = Expr;");
			EmitDslShape(f, "methodCall", "obj.Method(args)");
			EmitDslShape(f, "constructor", "obj = ClassName(args)");
			EmitDslShape(f, "print", "print Expr Alias;");
			EmitDslShape(f, "block", "{ stmt; stmt; }");
			EmitDslShape(f, "comment", "// text  — at end of line or own line");
			f.EndCollection("dslSyntax");

			// ── DSL literals ───────────────────────────────────────────────
			f.BeginCollection();
			EmitLiteral(f, "int", "42", "decimal '.' separator, no thousand separator");
			EmitLiteral(f, "decimal", "300.50", "period decimal; suffix not required");
			EmitLiteral(f, "string", "'text'", "single quotes only");
			EmitLiteral(f, "date", "MM/dd/yyyy", "culture-invariant; ISO accepted too");
			EmitLiteral(f, "bool", "true | false", "lowercase keywords");
			f.EndCollection("dslLiterals");

			// ── REPL meta-verbs (inside attach session) ────────────────────
			f.BeginCollection();
			EmitReplVerb(f, "exit", "End the session and close the journal.");
			EmitReplVerb(f, "quit", "Alias of exit.");
			EmitReplVerb(f, "help", "Show REPL syntax cheat-sheet to stderr.");
			EmitReplVerb(f, "chronicle ...", "Reserved (not yet implemented).");
			f.EndCollection("replMetaVerbs");

			// ── REPL dispatch rules ────────────────────────────────────────
			f.Field("replDispatch",
				"Line starting with `print` -> PerformQuery (TOON to stdout). " +
				"Anything else -> PerformCommand (journaled). " +
				"`{` opens a multi-line block until `}` balances; comments and string " +
				"literals (single quotes) are honored by the bracket counter.");

			// ── PromptBook (operator journal) ──────────────────────────────
			f.Field("promptBookDefault",
				"%LOCALAPPDATA%/PuppeteerCli/PromptBook/ (Windows) or " +
				"~/.local/share/PuppeteerCli/PromptBook/ (Unix). One PromptBook per user.");

			// ── Output formats ─────────────────────────────────────────────
			f.Field("outputFormat",
				"TOON (Token-Oriented Object Notation). Indentation-based, no closing " +
				"brackets, no commas. Strings always quoted; primitives bare; collections " +
				"as bulleted lists. Default for all `show*`, `describe`, and `print` output.");

			f.EndDocument();
			stdout.Write(sb.ToString());
			stdout.Flush();
			return 0;
		}

		// ── Verb declarations ─────────────────────────────────────────────────
		//
		// Each verb is a Toon item with: name, summary, requiredFlags, optionalFlags,
		// defaults (if applicable), and notes.

		private static void EmitAttachTxt(ToonFormatter f)
		{
			f.BeginCollectionItem();
			f.Field("name", "attach --txt");
			f.Field("summary", "PlainText scratch mode: human-readable journal + REPL.");
			f.Field("requiredFlags", "--txt <file>");
			f.Field("optionalFlags", "--actor-name <name>, --libraries <dll[,dll]>, --prompt-book <dir>");
			f.Field("defaults", "actor-name = basename of --txt; libraries = *.dll in directory of --txt");
			f.Field("notes",
				"The .txt file IS the canonical journal. No Shadow. Each PerformCommand " +
				"appends one line; queries leave no trace. Reactions are runtime-only " +
				"(lost between sessions). Minimum invocation: `puppeteer attach --txt foo.txt`.");
			f.EndCollectionItem();
		}

		private static void EmitAttachPrimary(ToonFormatter f)
		{
			f.BeginCollectionItem();
			f.Field("name", "attach --primary");
			f.Field("summary", "Shadow-isolated session against a FileSystem primary journal.");
			f.Field("requiredFlags", "--primary <conn>, --actor-name <name>, --snapshot");
			f.Field("optionalFlags", "--libraries <dll[,dll]>, --prompt-book <dir>");
			f.Field("notes",
				"--snapshot is required (only mode today; --live arrives later). The shadow " +
				"is in-memory and discarded at exit; the primary's journal stays untouched.");
			f.EndCollectionItem();
		}

		private static void EmitDescribe(ToonFormatter f)
		{
			f.BeginCollectionItem();
			f.Field("name", "describe");
			f.Field("summary", "Emit this surface as TOON (machine-readable).");
			f.Field("requiredFlags", "(none)");
			f.Field("notes", "The AI's primary onboarding command in a fresh chat. Always current — derived from runtime, not docs.");
			f.EndCollectionItem();
		}

		private static void EmitElide(ToonFormatter f)
		{
			f.BeginCollectionItem();
			f.Field("name", "elide");
			f.Field("summary", "Physically remove an entry from a PlainText journal. Preserves the ID as a gap (never re-used).");
			f.Field("requiredFlags", "--txt <file>, --entry <id>");
			f.Field("optionalFlags", "--backup");
			f.Field("notes",
				"Sacred-file invariant: humans don't edit the .txt manually. This verb is the " +
				"authoritative removal path. --backup snapshots the file to <file>.<timestamp>.bak " +
				"before overwriting.");
			f.EndCollectionItem();
		}

		private static void EmitShowEntry(ToonFormatter f)
		{
			f.BeginCollectionItem();
			f.Field("name", "show entry");
			f.Field("summary", "Print one journal entry as TOON. Read-only one-shot.");
			f.Field("requiredFlags", "<id>, --journal <conn>, --actor-name <name>");
			f.EndCollectionItem();
		}

		private static void EmitShowAction(ToonFormatter f)
		{
			f.BeginCollectionItem();
			f.Field("name", "show action");
			f.Field("summary", "Print the active Define entry for an actionId. Latest Define wins.");
			f.Field("requiredFlags", "<actionId>, --journal <conn>, --actor-name <name>");
			f.EndCollectionItem();
		}

		private static void EmitIssueInvitation(ToonFormatter f)
		{
			f.BeginCollectionItem();
			f.Field("name", "issue-invitation");
			f.Field("summary", "Paper 7 Phase 2 — emit onboarding share-links over real-TLS HTTPS.");
			f.Field("requiredFlags", "--listen <https-url>");
			f.Field("optionalFlags", "--advertise <https-url>, --count <N>, --ttl-minutes <M>");
			f.EndCollectionItem();
		}

		private static void EmitChroniclePlaceholder(ToonFormatter f)
		{
			f.BeginCollectionItem();
			f.Field("name", "chronicle");
			f.Field("summary", "Reserved namespace for the human supervision surface. Not yet implemented.");
			f.Field("notes", "Distinct abstraction from the operator CLI; shares this binary as a door. Consumes narratives from the PromptBook journal.");
			f.EndCollectionItem();
		}

		private static void EmitDslShape(ToonFormatter f, string name, string syntax)
		{
			f.BeginCollectionItem();
			f.Field("name", name);
			f.Field("syntax", syntax);
			f.EndCollectionItem();
		}

		private static void EmitLiteral(ToonFormatter f, string type, string example, string note)
		{
			f.BeginCollectionItem();
			f.Field("type", type);
			f.Field("example", example);
			f.Field("note", note);
			f.EndCollectionItem();
		}

		private static void EmitReplVerb(ToonFormatter f, string name, string description)
		{
			f.BeginCollectionItem();
			f.Field("name", name);
			f.Field("description", description);
			f.EndCollectionItem();
		}
	}
}
