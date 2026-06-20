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

        // Wrapper para que Choreography (Stage / Performance / Ensemble) propague
        // el logger inyectado por el host hasta el Actor que vive bajo el hook.
        // El sink es per-handler (no singleton); F6 del refactor de logger.
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
        // Optional facade — null si el Stage no tiene Playbill configurado
        // (audit-off). Cuando se asigna, Choreography (Stage) lo usa para
        // (a) suscribirse a OnSchemaRegistered/OnRecordWritten y broadcastear
        // PlaybillSchemaCue/PlaybillCue como Director, y (b) aplicar cues
        // entrantes como Cast via WriteRecordRaw / RegisterSchemaRaw.
        //
        // El Playbill instance es per-Stage (cada Stage en el cluster tiene
        // su propio backend local — InMemory dict / FS subdir / SQL DB).
        // El setter solo configura la referencia; la suscripcion del Stage
        // a los callbacks ocurre en Stage.AttachPlaybill.
        public Playbill Playbill { get; set; }

        // Wrapper para que Performance.Start(asFollower:true) pueda activar
        // el flag desde Choreography (que no tiene InternalsVisibleTo de
        // Puppeteer). Ver ActorHandler.SuppressReactionJournaling para la
        // semantica (Etapa 1: gate de ExecuteTell para no journalize en
        // followers; invariante 1-escritor del journal canonico).
        public bool SuppressReactionJournaling
        {
            get { return handler.SuppressReactionJournaling; }
            set { handler.SuppressReactionJournaling = value; }
        }

        // Wrapper para que Choreography (sin InternalsVisibleTo de Puppeteer)
        // configure el provider del rol vivo que usa el gate de
        // ReactionActivation. El Stage P2P pasa () => IsDirector; la Performance
        // Theater () => !isFollower. Ver ActorHandler.SetActingAsDirectorProvider.
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

        // Fase 4.5 refactor Playbill: ip/user dejan de auto-inyectarse como parametros
        // del script. Quedan en la firma del API (compat binaria con callers existentes)
        // pero el handler ya defaultea Ip/User internamente. Scripts que necesiten estos
        // valores deben declararlos como parametros de usuario y pasarlos en userParameters.
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
