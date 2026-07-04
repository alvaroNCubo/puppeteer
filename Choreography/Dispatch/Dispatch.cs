using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Choreography.Input;
using Choreography.Observability;
using Puppeteer;

namespace Choreography.Dispatch
{
    public sealed class Dispatch : IDisposable
    {
        private readonly ActorV2 actor;
        private readonly DispatchOptions options;
        private readonly Dictionary<int, IDispatchHandler> handlers = new();
        private readonly IdempotencyWindow idempotencyWindow;
        private readonly SemaphoreSlim schedulerSemaphore;
        private readonly BlockingCollection<DispatchWorkItem> workQueue;
        private readonly Task[] workerTasks;
        private readonly CancellationTokenSource disposeCts = new();
        private readonly Action<CancellationToken> waitUntilAlive;

        // Bound input sources (the MERGE). Each ConsumeFrom subscribes one
        // IInputSource through a routing and feeds the routed commands into the
        // single serial work queue above — so several sources reduce to ONE serial
        // command flow. Disposed when the Dispatch is disposed.
        private readonly ConcurrentBag<IDisposable> sourceSubscriptions = new();

        internal TaskMonitor Monitor { get; }
        private bool disposed;

        internal Dispatch(ActorV2 actor, DispatchOptions options, Action<CancellationToken> waitUntilAlive)
        {
            this.actor = actor ?? throw new ArgumentNullException(nameof(actor));
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.waitUntilAlive = waitUntilAlive;
            options.Validate();

            idempotencyWindow = new IdempotencyWindow(options.IdempotencyWindowSize, options.IdempotencyTTL);
            Monitor = new TaskMonitor(options.StuckThreshold);
            schedulerSemaphore = new SemaphoreSlim(options.MaxParallelism, options.MaxParallelism);
            workQueue = new BlockingCollection<DispatchWorkItem>(options.MaxParallelism * 4);

            workerTasks = new Task[options.MaxParallelism];
            for (int i = 0; i < options.MaxParallelism; i++)
            {
                workerTasks[i] = Task.Factory.StartNew(
                    () => WorkerLoop(disposeCts.Token),
                    TaskCreationOptions.LongRunning);
            }
        }

        public Dispatch On<TMessage>(Action<ActorV2, TMessage> handler)
            where TMessage : IDispatchMessage
        {
            ArgumentNullException.ThrowIfNull(handler);

            int typeId = TMessage.TypeId;
            if (handlers.ContainsKey(typeId))
                throw new InvalidOperationException($"Handler already registered for type {typeId}");

            handlers[typeId] = new SyncHandler<TMessage>(handler);
            return this;
        }

        public Dispatch On<TMessage>(Func<ActorV2, TMessage, Task> handler)
            where TMessage : IDispatchMessage
        {
            ArgumentNullException.ThrowIfNull(handler);

            int typeId = TMessage.TypeId;
            if (handlers.ContainsKey(typeId))
                throw new InvalidOperationException($"Handler already registered for type {typeId}");

            handlers[typeId] = new AsyncHandler<TMessage>(handler);
            return this;
        }

        public Dispatch On<TMessage>(Func<ActorV2, TMessage, CancellationToken, Task> handler)
            where TMessage : IDispatchMessage
        {
            ArgumentNullException.ThrowIfNull(handler);

            int typeId = TMessage.TypeId;
            if (handlers.ContainsKey(typeId))
                throw new InvalidOperationException($"Handler already registered for type {typeId}");

            handlers[typeId] = new AsyncCancellableHandler<TMessage>(handler);
            return this;
        }

        public void Receive(string messageId, string rawMessage)
        {
            Receive(messageId, rawMessage, null);
        }

        // Overload that signals completion of the handler. onCompleted is
        // invoked with true after the handler commits and false if it threw or
        // was cancelled. A transport bridge uses this to ack the source record
        // ONLY after the receiver's command is durable — the honest delivery
        // semantics for a tell ("delivered" means "the receiver processed it"),
        // never "the broker handed me the bytes". An already-processed message
        // (idempotency hit) reports true so a redelivered record is re-acked;
        // the origin de-duplicates the ack by tell id.
        public void Receive(string messageId, string rawMessage, Action<bool> onCompleted)
        {
            ArgumentNullException.ThrowIfNull(messageId);
            ArgumentNullException.ThrowIfNull(rawMessage);
            if (rawMessage.Length == 0) throw new ArgumentException("Message cannot be empty", nameof(rawMessage));
            if (disposed) throw new ObjectDisposedException(nameof(Dispatch));

            if (idempotencyWindow.AlreadyProcessed(messageId))
            {
                DispatchTracer.Instance.OnIdempotencyHit(messageId);
                onCompleted?.Invoke(true);
                return;
            }

            int typeId = (int)rawMessage[0];

            if (!handlers.TryGetValue(typeId, out var handler))
                throw new LanguageException($"No handler registered for message type {typeId}");

            var workItem = new DispatchWorkItem(handler, rawMessage, messageId, null, null, null, onCompleted);
            workQueue.Add(workItem);
        }

        // Consume FROM an input source through a routing — the seam that replaces
        // the hand-wired `broker.Subscribe(topic, record => dispatch.Receive(...))`
        // bridge. The source is the MEDIUM (Kafka / InProc / Loopback, behind
        // IInputSource); the routing is the receiver's mapping signal -> command.
        // Neither is hardcoded here: Dispatch sees only IInputSource + InputRouting.
        //
        // Calling ConsumeFrom more than once is the MERGE: every bound source feeds
        // the same single serial work queue, so an actor animated by several media
        // still runs one deterministic serial command flow (idempotency, alive-gate,
        // and per-key saga locks all apply uniformly, regardless of which medium a
        // signal arrived on).
        //
        // Routing that returns null DROPS the signal (e.g. an ack/control record on
        // a shared topic): it is neither enqueued nor idempotency-recorded.
        //
        // Returns this Dispatch for fluent chaining of several sources.
        public Dispatch ConsumeFrom(IInputSource source, InputRouting routing)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(routing);
            if (disposed) throw new ObjectDisposedException(nameof(Dispatch));

            IDisposable subscription = source.Start(signal =>
            {
                DispatchCommand? command;
                try
                {
                    command = routing(signal);
                }
                catch (Exception ex)
                {
                    // A routing that throws must not tear down the source: log and
                    // drop this one signal, mirroring the brokers' deliver-safely
                    // contract. The signal stays unconsumed (and, on a real broker,
                    // redeliverable).
                    DispatchTracer.Instance.OnHandlerFailed($"input-routing[{source.SourceName}]", ex);
                    return;
                }

                if (command is not DispatchCommand cmd) return;   // routing dropped it
                if (cmd.MessageId == null || string.IsNullOrEmpty(cmd.RawMessage)) return;

                Receive(cmd.MessageId, cmd.RawMessage);
            });

            sourceSubscriptions.Add(subscription);
            return this;
        }

        internal void ReceiveFromSaga(string messageId, string rawMessage,
            string sagaName, string stepName, string instanceKey)
        {
            ArgumentNullException.ThrowIfNull(messageId);
            ArgumentNullException.ThrowIfNull(rawMessage);
            if (rawMessage.Length == 0) throw new ArgumentException("Message cannot be empty", nameof(rawMessage));
            if (disposed) throw new ObjectDisposedException(nameof(Dispatch));

            if (idempotencyWindow.AlreadyProcessed(messageId))
            {
                DispatchTracer.Instance.OnIdempotencyHit(messageId);
                return;
            }

            int typeId = (int)rawMessage[0];

            if (!handlers.TryGetValue(typeId, out var handler))
                throw new LanguageException($"No handler registered for message type {typeId}");

            var workItem = new DispatchWorkItem(handler, rawMessage, messageId, sagaName, stepName, instanceKey, null);
            workQueue.Add(workItem);
        }

        internal bool HasHandler(int typeId) => handlers.ContainsKey(typeId);

        internal void RegisterHandler(int typeId, IDispatchHandler handler)
        {
            if (handlers.ContainsKey(typeId))
                throw new InvalidOperationException($"Handler already registered for type {typeId}");
            handlers[typeId] = handler;
        }

        private void WorkerLoop(CancellationToken ct)
        {
            foreach (var workItem in workQueue.GetConsumingEnumerable(ct))
            {
                // Block until the actor is alive (primary, handover complete).
                // During follower bootstrap or LockWhileNotSyncronized handover,
                // no fire-and-forget nor saga step may execute. This is the
                // single mechanism that prevents double-execution across
                // red-black machines: the new machine's workers stay parked
                // until UnlockAndRunAlive flips the gate.
                waitUntilAlive?.Invoke(ct);
                ExecuteWorkItem(workItem);
            }
        }

        private void ExecuteWorkItem(DispatchWorkItem workItem)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(disposeCts.Token);
            var taskInfo = Monitor.Register(
                workItem.SagaName ?? "dispatch",
                workItem.StepName ?? workItem.Handler.HandlerName,
                workItem.InstanceKey,
                workItem.MessageId,
                cts);

            IFlowSpan span = DispatchTracer.Instance.StartHandlerSpan(
                workItem.MessageId,
                workItem.Handler.HandlerName,
                workItem.SagaName,
                workItem.StepName,
                workItem.InstanceKey);

            bool succeeded = false;
            try
            {
                workItem.Handler.Execute(actor, workItem.RawMessage, cts.Token);
                Monitor.Complete(taskInfo);
                span.SetOutcome(FlowOutcome.Success);
                succeeded = true;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                taskInfo.MarkCancelled();
                span.SetOutcome(FlowOutcome.Unknown);
            }
            catch (Exception ex)
            {
                Monitor.Fail(taskInfo, ex);
                span.SetOutcome(FlowOutcome.Failure);
                DispatchTracer.Instance.OnHandlerFailed(workItem.Handler.HandlerName, ex);
            }
            finally
            {
                span.Dispose();
                cts.Dispose();
                NotifyCompletion(workItem.OnCompleted, succeeded);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            // Stop the medium first so no new signal is enqueued while we drain.
            foreach (IDisposable subscription in sourceSubscriptions)
            {
                try { subscription.Dispose(); } catch { }
            }

            workQueue.CompleteAdding();
            disposeCts.Cancel();

            try
            {
                Task.WaitAll(workerTasks, TimeSpan.FromSeconds(30));
            }
            catch (AggregateException) { }

            Monitor.Dispose();
            idempotencyWindow.Dispose();
            schedulerSemaphore.Dispose();
            workQueue.Dispose();
            disposeCts.Dispose();
        }

        // A completion callback that throws must not poison the worker loop.
        private static void NotifyCompletion(Action<bool> onCompleted, bool succeeded)
        {
            if (onCompleted == null) return;
            try
            {
                onCompleted(succeeded);
            }
            catch (Exception ex)
            {
                DispatchTracer.Instance.OnHandlerFailed("dispatch-completion", ex);
            }
        }

        private readonly struct DispatchWorkItem
        {
            internal readonly IDispatchHandler Handler;
            internal readonly string RawMessage;
            internal readonly string MessageId;
            internal readonly string SagaName;
            internal readonly string StepName;
            internal readonly string InstanceKey;
            internal readonly Action<bool> OnCompleted;

            internal DispatchWorkItem(IDispatchHandler handler, string rawMessage, string messageId,
                string sagaName, string stepName, string instanceKey, Action<bool> onCompleted)
            {
                Handler = handler;
                RawMessage = rawMessage;
                MessageId = messageId;
                SagaName = sagaName;
                StepName = stepName;
                InstanceKey = instanceKey;
                OnCompleted = onCompleted;
            }
        }
    }

    internal interface IDispatchHandler
    {
        string HandlerName { get; }
        void Execute(ActorV2 actor, string rawMessage, CancellationToken ct);
    }

    internal sealed class SyncHandler<TMessage> : IDispatchHandler
        where TMessage : IDispatchMessage
    {
        private readonly Action<ActorV2, TMessage> handler;

        internal SyncHandler(Action<ActorV2, TMessage> handler)
        {
            this.handler = handler;
        }

        public string HandlerName => typeof(TMessage).Name;

        public void Execute(ActorV2 actor, string rawMessage, CancellationToken ct)
        {
            var msg = (TMessage)TMessage.Deserialize(rawMessage);
            handler(actor, msg);
        }
    }

    internal sealed class AsyncHandler<TMessage> : IDispatchHandler
        where TMessage : IDispatchMessage
    {
        private readonly Func<ActorV2, TMessage, Task> handler;

        internal AsyncHandler(Func<ActorV2, TMessage, Task> handler)
        {
            this.handler = handler;
        }

        public string HandlerName => typeof(TMessage).Name;

        public void Execute(ActorV2 actor, string rawMessage, CancellationToken ct)
        {
            var msg = (TMessage)TMessage.Deserialize(rawMessage);
            handler(actor, msg).GetAwaiter().GetResult();
        }
    }

    internal sealed class AsyncCancellableHandler<TMessage> : IDispatchHandler
        where TMessage : IDispatchMessage
    {
        private readonly Func<ActorV2, TMessage, CancellationToken, Task> handler;

        internal AsyncCancellableHandler(Func<ActorV2, TMessage, CancellationToken, Task> handler)
        {
            this.handler = handler;
        }

        public string HandlerName => typeof(TMessage).Name;

        public void Execute(ActorV2 actor, string rawMessage, CancellationToken ct)
        {
            var msg = (TMessage)TMessage.Deserialize(rawMessage);
            handler(actor, msg, ct).GetAwaiter().GetResult();
        }
    }
}
