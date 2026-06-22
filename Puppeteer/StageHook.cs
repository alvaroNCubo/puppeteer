using Puppeteer.EventSourcing;
using Puppeteer.EventSourcing.DB;
using Puppeteer.EventSourcing.DB.FileSystem;
using Puppeteer.EventSourcing.Follower;
using Puppeteer.EventSourcing.Playbill;
using System;

namespace Puppeteer
{
    public sealed class StageHook
    {
        private readonly ActorHandler handler;
        private readonly EventDataPool eventDataPool = new EventDataPool(64);

        public StageHook(Actor actor)
        {
            ArgumentNullException.ThrowIfNull(actor);
            this.handler = actor.GetHandler();
        }

        // Wrapper so Choreography (Stage / Performance / Ensemble) can propagate
        // the logger injected by the host down to the Actor that lives under the hook.
        // The sink is per-handler (not a singleton); F6 of the logger refactor.
        public void UseLogger(IPuppeteerLogger logger)
        {
            ArgumentNullException.ThrowIfNull(logger);
            handler.UseLogger(logger);
        }

        public IPuppeteerLogger Logger => handler.Logger;

        public long CurrentEntryId => handler.CurrentEntryId;

        public DateTime DateOfLastActivity => handler.DateOfLastActivity;

        public bool IsNew => handler.ItsANewOne;

        public Reactions Reactions => handler.Reactions;

        public Action<long, byte[]> OnRecordWritten
        {
            set { handler.OnRecordWritten = value; }
        }

        // === Playbill (Fase 5) ===
        //
        // Optional facade — null if the Stage has no Playbill configured
        // (audit-off). When assigned, Choreography (Stage) uses it to
        // (a) subscribe to OnSchemaRegistered/OnRecordWritten and broadcast
        // PlaybillSchemaCue/PlaybillCue as Director, and (b) apply incoming
        // cues as Cast via WriteRecordRaw / RegisterSchemaRaw.
        //
        // The Playbill instance is per-Stage (each Stage in the cluster has
        // its own local backend — InMemory dict / FS subdir / SQL DB).
        // The setter only configures the reference; the Stage's subscription
        // to the callbacks happens in Stage.AttachPlaybill.
        public Playbill Playbill { get; set; }

        // Wrapper so Performance.Start(asFollower:true) can activate
        // the flag from Choreography (which has no InternalsVisibleTo of
        // Puppeteer). See ActorHandler.SuppressReactionJournaling for the
        // semantics (Stage 1: gate on ExecuteTell to not journalize on
        // followers; 1-writer invariant of the canonical journal).
        public bool SuppressReactionJournaling
        {
            get { return handler.SuppressReactionJournaling; }
            set { handler.SuppressReactionJournaling = value; }
        }

        // Wrapper so Choreography (without InternalsVisibleTo of Puppeteer)
        // can configure the live-role provider used by the ReactionActivation
        // gate. The P2P Stage passes () => IsDirector; the Theater Performance
        // () => !isFollower. See ActorHandler.SetActingAsDirectorProvider.
        public void SetActingAsDirectorProvider(Func<bool> provider)
        {
            handler.SetActingAsDirectorProvider(provider);
        }

        // Phase 5 of the Action refactor: dropped OnNewActionDefined +
        // WriteRawActionDefinition. Define entries are journal records and replicate
        // through OnRecordWritten → CueEvent → ApplyReplicatedEvent path like any
        // other record. The dispatch in ApplyReplicatedEvent handles Define records
        // by populating the actor's action cache directly.

        public void WriteRawRecord(byte[] record, long entryId)
        {
            handler.WriteRawRecord(record, entryId);
        }

        public void ApplyReplicatedEvent(byte[] record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            // The record from BinaryEventCodec.Encode* includes a 4-byte length prefix.
            // TryDecode expects the buffer starting at the type byte (after the prefix).
            int lengthPrefixSize = 4;
            int bodyLength = record.Length - lengthPrefixSize;
            byte[] body = new byte[bodyLength];
            Buffer.BlockCopy(record, lengthPrefixSize, body, 0, bodyLength);

            // Phase 5 of the Action refactor: Define records flow through the same
            // replication path as Script and Invocation records. Handle them by
            // populating the actor's vocabulary directly — Define entries do not
            // produce EventData (they mutate the actor's cache, not its state).
            EventRecordType peekedType = BinaryEventCodec.PeekRecordType(body);
            if (peekedType == EventRecordType.Define)
            {
                bool defOk = BinaryEventCodec.TryDecodeDefine(body, bodyLength,
                    out _, out _,
                    out int defineActionId, out string defineStatementText, out _);
                if (!defOk) throw new InvalidOperationException("Failed to decode Define journal record");
                ((EventSourcing.DB.IActorEventJournalClient)handler).AddKnownActionFromDefine(defineActionId, defineStatementText);
                return;
            }

            bool success = BinaryEventCodec.TryDecode(body, bodyLength,
                out EventRecordType eventType, out long entryId, out DateTime occurredAt,
                out string scriptOrArguments, out int actionId);

            if (!success) throw new InvalidOperationException("Failed to decode journal record");

            EventData eventData;
            if (eventType == EventRecordType.Script)
            {
                var scriptEvent = eventDataPool.RentScript();
                scriptEvent.EntryId = entryId;
                scriptEvent.OccurredAt = occurredAt;
                scriptEvent.Script = scriptOrArguments;
                eventData = scriptEvent;
            }
            else
            {
                var actionEvent = eventDataPool.RentAction();
                actionEvent.EntryId = entryId;
                actionEvent.OccurredAt = occurredAt;
                actionEvent.ActionId = actionId;
                actionEvent.Arguments = scriptOrArguments;
                eventData = actionEvent;
            }

            handler.ApplyReplicatedEvent(eventData);
        }

        public void InitializeStorage(DatabaseType dbType, string connectionString)
        {
            handler.EventSourcingStorage(dbType, connectionString);
        }

        public string PerformCmd(string script)
        {
            return handler.PerformCmd(script, "", "");
        }

        public string PerformCmd(string script, Parameters parameters)
        {
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            return handler.PerformCmd(script, parameters);
        }

        // Phase 4.5 Playbill refactor: ip/user are no longer auto-injected as script
        // parameters. They remain in the API signature (binary compat with existing
        // callers) but the handler already defaults Ip/User internally. Scripts that
        // need these values must declare them as user parameters and pass them in userParameters.
        public string PerformCmd(string script, DateTime now, string ip, string user)
        {
            Parameters parameters = handler.ParametersPool.Rent();
            try
            {
                return handler.PerformCmd(script, parameters, now);
            }
            finally
            {
                handler.ParametersPool.Return(parameters);
            }
        }

        public string PerformCmd(string script, Parameters userParameters, DateTime now, string ip, string user)
        {
            if (userParameters == null) throw new ArgumentNullException(nameof(userParameters));

            return handler.PerformCmd(script, userParameters, now);
        }

        public string PerformCheckThenCmd(string scriptForChk, string scriptForCmd, DateTime now, string ip, string user)
        {
            if (scriptForChk == null) throw new ArgumentNullException(nameof(scriptForChk));
            if (scriptForCmd == null) throw new ArgumentNullException(nameof(scriptForCmd));

            Parameters parameters = handler.ParametersPool.Rent();
            try
            {
                return handler.PerformCheckThenCmd(scriptForChk, scriptForCmd, parameters, now);
            }
            finally
            {
                handler.ParametersPool.Return(parameters);
            }
        }

        public string PerformCheckThenCmd(string scriptForChk, string scriptForCmd, Parameters userParameters, DateTime now, string ip, string user)
        {
            if (scriptForChk == null) throw new ArgumentNullException(nameof(scriptForChk));
            if (scriptForCmd == null) throw new ArgumentNullException(nameof(scriptForCmd));
            if (userParameters == null) throw new ArgumentNullException(nameof(userParameters));

            return handler.PerformCheckThenCmd(scriptForChk, scriptForCmd, userParameters, now);
        }

        public string PerformQry(string script, Parameters parameters)
        {
            return handler.PerformQry(script, parameters);
        }

        public bool IsAlive => handler.IsAlive;

        public string LockWhileNotSyncronized()
        {
            return handler.LockWhileNotSyncronized();
        }

        public void UnlockAndRunAlive()
        {
            handler.UnlockAndRunAlive();
        }

        public void CatchUpFromJournal(long targetEntryId)
        {
            handler.CatchUpFromJournal(targetEntryId);
        }

        public void GracefulExit()
        {
            handler.GracefulExit();
        }
    }
}
