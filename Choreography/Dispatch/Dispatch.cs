using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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

            var workItem = new DispatchWorkItem(handler, rawMessage, messageId, null, null, null);
            workQueue.Add(workItem);
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

            var workItem = new DispatchWorkItem(handler, rawMessage, messageId, sagaName, stepName, instanceKey);
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

            try
            {
                workItem.Handler.Execute(actor, workItem.RawMessage, cts.Token);
                Monitor.Complete(taskInfo);
                span.SetOutcome(FlowOutcome.Success);
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
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

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

        private readonly struct DispatchWorkItem
        {
            internal readonly IDispatchHandler Handler;
            internal readonly string RawMessage;
            internal readonly string MessageId;
            internal readonly string SagaName;
            internal readonly string StepName;
            internal readonly string InstanceKey;

            internal DispatchWorkItem(IDispatchHandler handler, string rawMessage, string messageId,
                string sagaName, string stepName, string instanceKey)
            {
                Handler = handler;
                RawMessage = rawMessage;
                MessageId = messageId;
                SagaName = sagaName;
                StepName = stepName;
                InstanceKey = instanceKey;
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
