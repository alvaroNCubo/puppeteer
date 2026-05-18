using Puppeteer.EventSourcing;
using Puppeteer.EventSourcing.DB;
using Puppeteer.EventSourcing.Interpreter.Libraries;
using System;
using System.Collections.Generic;

namespace Puppeteer
{
	public class ActorFollower : IActorEventJournalClient
	{
		private readonly Actor actor;
		private List<Rule> rules;
		private readonly int followerId;
		private long lastProcessedEntryId = 0;
		private bool conEstado;

		internal static ActorFollower CreateFollowerSinActor(Actor actor, int followerId)
		{
			return new ActorFollower(actor, followerId, conEstado: false);
		}

		internal static ActorFollower CreateFollowerConActor(Actor actor, int followerId)
		{
			return new ActorFollower(actor, followerId, conEstado: true);
		}

		private ActorFollower(Actor actor, int followerId, bool conEstado)
		{
			ArgumentNullException.ThrowIfNull(actor);
			if (followerId <= 0) throw new LanguageException("Follower Id must be upper than zero");

			this.actor = actor;
			this.followerId = followerId;
			this.conEstado = conEstado;
		}

		internal int FollowerId
		{
			get
			{
				return followerId;
			}
		}

		internal long LastProcessedEntryId
		{
			get
			{
				return lastProcessedEntryId;
			}
			set
			{
				if (value <= 0) throw new LanguageException($"Last processed entry id '{value}' must be greater than zero");
				lastProcessedEntryId = value;
			}
		}

		internal Actor Actor
		{
			get
			{
				return actor;
			}
		}

		public void AddRule(Rule rule)
		{
			if (rules == null) rules = new List<Rule>();
			rule.Follower = this;
			rules.Add(rule);
		}

		internal void ClearAllRules()
		{
			if (rules == null) return;
			rules.Clear();
		}

		private void Init()
		{
			if (rules == null) return;

			foreach (Rule rule in rules)
			{
				rule.Init();
			}
		}

		internal void MatchRules(Program program, Actor actor)
		{
			Script script = new Script(program);
			if (rules == null) return;

			foreach (Rule rule in rules)
			{
				if (rule is RuleWithActor)
				{
					((RuleWithActor)rule).When(script, actor);
				}
				else if (rule is RuleWithoutActor)
				{
					((RuleWithoutActor)rule).When(script);
				}
				else
				{
					throw new Exception($"{rule.GetType()} not implemented yet");
				}

			}
		}

		internal void MatchRules(string command, long entryId, DateTime now)
		{
			Script script = new Script(command, entryId, now);
			if (rules == null) return;
			foreach (RuleWithoutActor rule in rules)
			{
				rule.When(script);
			}
		}

		internal void MatchRules(Program program)
		{
			Script script = new Script(program);
			if (rules == null) return;
			foreach (RuleWithoutActor rule in rules)
			{
				rule.When(script);
			}
		}

		public void Run(DatabaseType dbtype, string connectionString)
		{
			if (rules == null) return;

			_continueReplay = true;

			Console.WriteLine($"Starting {GetType()}'s follower");

			Init();

			actor.Handler.EventSourcingStorage(dbtype, connectionString, this);

			Finish();

			PartialUpdate();

			Console.WriteLine($"{GetType()}'s follower has been finished");
		}


		string IActorEventJournalClient.ActorName => actor.Name;

		long IActorEventJournalClient.GetLastProcessedEntryId(int followerId)
		{
			if (followerId <= 0) throw new LanguageException($"Follower Id '{followerId}' must be greater than zero");
			if (followerId != this.followerId) throw new LanguageException($"Follower Id '{followerId}' does not match with this follower's id '{this.followerId}'");

			return (actor as IActorEventJournalClient).GetLastProcessedEntryId(followerId);
		}

		bool IActorEventJournalClient.IsNew
		{
			set
			{
				(actor as IActorEventJournalClient).IsNew = value;
			}
		}

		bool IActorEventJournalClient.IsActionKnown(int actionId)
		{
			return (actor as IActorEventJournalClient).IsActionKnown(actionId);
		}

		void IActorEventJournalClient.AddKnownAction(int actionId, string actionScript, string parameters)
		{
			(actor as IActorEventJournalClient).AddKnownAction(actionId, actionScript, parameters);
		}

		void IActorEventJournalClient.AddKnownActionFromDefine(int actionId, string defineStatementText)
		{
			(actor as IActorEventJournalClient).AddKnownActionFromDefine(actionId, defineStatementText);
		}

		private long _lastEntryProcessedByFollower;
		void IActorEventJournalClient.BeginJournalReplay(long totalEventsToApply)
		{
			if (totalEventsToApply < 0) throw new LanguageException($"Total events to apply '{totalEventsToApply}' cannot be negative.");

			_lastEntryProcessedByFollower = (actor as IActorEventJournalClient).GetLastProcessedEntryId(followerId);

			(actor as IActorEventJournalClient).BeginJournalReplay(totalEventsToApply);
		}

		private volatile bool _continueReplay = false;
		bool IActorEventJournalClient.CanContinueReplay(long currentEntryId)
		{
			return _continueReplay;
		}

		void IActorEventJournalClient.ReplayEvent(EventData retornableEventData)
		{
			if (conEstado) (actor as IActorEventJournalClient).ReplayEvent(retornableEventData);

			Program rentedPrograma = actor.Handler.GenerateAndRentProgram(retornableEventData);

			retornableEventData.ReturnToEventDataPool();

			// Phase 5 of the Action refactor: GenerateAndRentProgram now returns
			// null for orphan Invocations (cache miss on the actionId). Skip the
			// rest of the follower-replay pipeline — the orphan path is
			// otherwise unreachable post-Fase-4 by construction.
			if (rentedPrograma == null) return;


			bool needsToMatchRules = lastProcessedEntryId > _lastEntryProcessedByFollower;

			if (needsToMatchRules)
			{
				if (conEstado)
				{
					MatchRules(rentedPrograma, actor);
				}
				else
				{
					MatchRules(rentedPrograma);
				}
			}

			actor.Handler.ReturnProgram(rentedPrograma);
		}

		void IActorEventJournalClient.EndJournalReplay(bool forcedToEnd)
		{
			(actor as IActorEventJournalClient).EndJournalReplay(forcedToEnd);
		}

		internal void PartialUpdate()
		{
			foreach (Rule rule in rules)
			{
				rule.PartialUpdate();
			}
		}

		private void Finish()
		{
			foreach (Rule rule in rules)
			{
				rule.Finish();
			}
		}

		public void ForceToEnd()
		{
			_continueReplay = false;
		}
	}
}
