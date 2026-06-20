using System;
using System.IO;
using Puppeteer;
using Puppeteer.EventSourcing.DB;

namespace PuppeteerCli.PromptBook
{
	// PromptBook — el actor del CLI mismo. Aplica Puppeteer a si mismo: cada
	// session que la IA abre con `puppeteer attach` queda journaled aqui, junto
	// con su cierre. Es la memoria persistente del operador (IA o humano).
	//
	// Dominio Layer 1 (firmado 2026-06-01): solo Session — OpenSession(target,
	// mode) + CloseSession(reason). Bookmark / Lineage / Note llegan despues.
	// El dominio crece sin re-arquitectura porque PromptBook ya es un actor
	// Puppeteer V2 puro; nuevos verbos son nuevas Actions journaled.
	//
	// Convivencia: cuando se sume Topology (el actor de bifurcaciones), vivira
	// en el namespace PuppeteerCli.Topology dentro del mismo PuppeteerCli.dll.
	// Monorepo a nivel de binario.
	public sealed class PromptBookActor : IDisposable
	{
		private const string ACTOR_NAME = "prompt-book";

		private readonly ActorV2 actor;
		public ActorV2 Actor => actor;

		// Construye (o abre) el PromptBook en journalRoot. Si journalRoot es
		// null, usa DefaultJournalPath() — por usuario, atravesado a todos
		// los targets. El subdirectorio del actor (`prompt-book/`) lo agrega
		// el backend FileSystem por si solo (convencion per-actor).
		public PromptBookActor(string journalRoot = null)
		{
			string root = journalRoot ?? DefaultJournalPath();
			Directory.CreateDirectory(root);

			actor = new ActorV2(ACTOR_NAME);
			actor.ConfigureStorage(DatabaseType.FileSystem, $"path={root}");
		}

		// Default: %LOCALAPPDATA%/PuppeteerCli/PromptBook/ en Windows; el path
		// equivalente en Unix lo provee SpecialFolder.LocalApplicationData. Un
		// solo PromptBook por usuario — atravesado a todos los targets. La IA
		// recuerda cosas de un actor a otro porque su memoria es una sola.
		public static string DefaultJournalPath()
		{
			string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			return Path.Combine(localAppData, "PuppeteerCli", "PromptBook");
		}

		// Registra el inicio de una session attached. Cada attach corre esto
		// una vez al inicio del proceso CLI. El EntryId del journal queda como
		// identificador implicito de la session (recuperable via show entry /
		// chronicle); no se persiste sessionId explicito por ahora — los pares
		// open/close se asocian por orden y por proceso de CLI.
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

		// Registra el cierre de la session attached actual. Default reason
		// 'user-exit'; el CLI puede pasar 'eof' / 'ctrl-c' / 'error' segun
		// como salio el REPL.
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
