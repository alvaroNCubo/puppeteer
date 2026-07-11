using System;
using System.IO;
using Puppeteer;
using Puppeteer.EventSourcing.DB;

namespace PuppeteerCli.PromptBook
{
	// PromptBook — the CLI's own actor. It applies Puppeteer to itself: every
	// session the AI opens with `puppeteer attach` is journaled here, along
	// with its close. It is the operator's persistent memory (AI or human).
	//
	// Layer 1 domain (signed 2026-06-01): only Session — OpenSession(target,
	// mode) + CloseSession(reason). Bookmark / Lineage / Note come later.
	// The domain grows without re-architecture because PromptBook is already a
	// pure Puppeteer V2 actor; new verbs are new journaled Actions.
	//
	// Coexistence: when Topology (the branching actor) is added, it will live
	// in the PuppeteerCli.Topology namespace inside the same PuppeteerCli.dll.
	// A monorepo at the binary level.
	public sealed class PromptBookActor : IDisposable
	{
		private const string ACTOR_NAME = "prompt-book";

		private readonly ActorV2 actor;
		public ActorV2 Actor => actor;

		// Builds (or opens) the PromptBook at journalRoot. If journalRoot is
		// null, uses DefaultJournalPath() — per user, shared across all
		// targets. The actor's subdirectory (`prompt-book/`) is added by
		// the FileSystem backend on its own (per-actor convention).
		public PromptBookActor(string journalRoot = null)
		{
			string root = journalRoot ?? DefaultJournalPath();
			Directory.CreateDirectory(root);

			actor = new ActorV2(ACTOR_NAME);
			actor.ConfigureStorage(DatabaseType.FileSystem, $"path={root}");
		}

		// Default: %LOCALAPPDATA%/PuppeteerCli/PromptBook/ on Windows; the
		// equivalent path on Unix is provided by SpecialFolder.LocalApplicationData.
		// One PromptBook per user — shared across all targets. The AI
		// remembers things from one actor to another because its memory is one.
		public static string DefaultJournalPath()
		{
			string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			return Path.Combine(localAppData, "PuppeteerCli", "PromptBook");
		}

		// Records the start of an attached session. Each attach runs this
		// once at the start of the CLI process. The journal's EntryId serves as
		// the implicit identifier of the session (recoverable via show entry /
		// chronicle); no explicit sessionId is persisted for now — the open/close
		// pairs are associated by order and by CLI process.
		public void OpenSession(string target, string mode)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(target);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(mode);

			actor.Using("{ t = target; m = mode; }")
			     .WithParameters(p =>
			     {
			         p["target", typeof(string)] = target;
			         p["mode", typeof(string)] = mode;
			     })
			     .PerformCommand();
		}

		// Records the close of the current attached session. Default reason
		// 'user-exit'; the CLI can pass 'eof' / 'ctrl-c' / 'error' depending on
		// how the REPL exited.
		public void CloseSession(string reason)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(reason);

			actor.Using("{ r = reason; }")
			     .WithParameters(p =>
			     {
			         p["reason", typeof(string)] = reason;
			     })
			     .PerformCommand();
		}

		public void Dispose()
		{
			actor.GracefulExit();
		}
	}
}
