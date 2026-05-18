using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Choreography.Dispatch;
using Choreography.Observability;
using Puppeteer;

namespace Choreography.Saga
{
    public sealed class SagaDefinition
    {
        internal string Name { get; }
        internal List<SagaStep> Steps { get; } = new();
        internal Dispatch.Dispatch Dispatch { get; }
        internal SagaStepJournal StepJournal { get; }
        internal KeyLock KeyLock { get; }

        internal SagaDefinition(string name, Dispatch.Dispatch dispatch,
            SagaStepJournal stepJournal, KeyLock keyLock)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
            StepJournal = stepJournal ?? throw new ArgumentNullException(nameof(stepJournal));
            KeyLock = keyLock ?? throw new ArgumentNullException(nameof(keyLock));
        }

        public SagaEventBuilder<TMessage> On<TMessage>(Func<TMessage, string> instanceKeySelector)
            where TMessage : IDispatchMessage
        {
            ArgumentNullException.ThrowIfNull(instanceKeySelector);
            return new SagaEventBuilder<TMessage>(this, instanceKeySelector);
        }
    }

    public sealed class SagaEventBuilder<TMessage> where TMessage : IDispatchMessage
    {
        private readonly SagaDefinition saga;
        private readonly Func<TMessage, string> instanceKeySelector;

        internal SagaEventBuilder(SagaDefinition saga, Func<TMessage, string> instanceKeySelector)
        {
            this.saga = saga;
            this.instanceKeySelector = instanceKeySelector;
        }

        public SagaDefinition Task(string stepName, Action<ActorV2, TMessage> handler)
        {
            ArgumentNullException.ThrowIfNullOrWhiteSpace(stepName);
            ArgumentNullException.ThrowIfNull(handler);

            var step = new SagaStep(stepName, TMessage.TypeId);
            saga.Steps.Add(step);

            var sagaHandler = new SagaSyncHandler<TMessage>(
                saga, stepName, instanceKeySelector, handler);
            saga.Dispatch.RegisterHandler(TMessage.TypeId, sagaHandler);

            return saga;
        }

        public SagaDefinition Task(string stepName, Func<ActorV2, TMessage, Task> handler)
        {
            ArgumentNullException.ThrowIfNullOrWhiteSpace(stepName);
            ArgumentNullException.ThrowIfNull(handler);

            var step = new SagaStep(stepName, TMessage.TypeId);
            saga.Steps.Add(step);

            var sagaHandler = new SagaAsyncHandler<TMessage>(
                saga, stepName, instanceKeySelector, handler);
            saga.Dispatch.RegisterHandler(TMessage.TypeId, sagaHandler);

            return saga;
        }

        public SagaDefinition Task(string stepName, Func<ActorV2, TMessage, CancellationToken, Task> handler)
        {
            ArgumentNullException.ThrowIfNullOrWhiteSpace(stepName);
            ArgumentNullException.ThrowIfNull(handler);

            var step = new SagaStep(stepName, TMessage.TypeId);
            saga.Steps.Add(step);

            var sagaHandler = new SagaAsyncCancellableHandler<TMessage>(
                saga, stepName, instanceKeySelector, handler);
            saga.Dispatch.RegisterHandler(TMessage.TypeId, sagaHandler);

            return saga;
        }
    }

    internal sealed class SagaStep
    {
        internal string Name { get; }
        internal int TypeId { get; }

        internal SagaStep(string name, int typeId)
        {
            Name = name;
            TypeId = typeId;
        }
    }

    internal sealed class SagaSyncHandler<TMessage> : IDispatchHandler
        where TMessage : IDispatchMessage
    {
        private readonly SagaDefinition saga;
        private readonly string stepName;
        private readonly Func<TMessage, string> instanceKeySelector;
        private readonly Action<ActorV2, TMessage> handler;

        internal SagaSyncHandler(SagaDefinition saga, string stepName,
            Func<TMessage, string> instanceKeySelector, Action<ActorV2, TMessage> handler)
        {
            this.saga = saga;
            this.stepName = stepName;
            this.instanceKeySelector = instanceKeySelector;
            this.handler = handler;
        }

        public string HandlerName => $"{saga.Name}.{stepName}";

        public void Execute(ActorV2 actor, string rawMessage, CancellationToken ct)
        {
            var msg = (TMessage)TMessage.Deserialize(rawMessage);
            var instanceKey = instanceKeySelector(msg);

            if (saga.StepJournal.StepAlreadyCompleted(saga.Name, instanceKey, stepName))
            {
                SagaTracer.Instance.OnStepSkippedByIdempotency(saga.Name, stepName, instanceKey);
                return;
            }

            using (SagaTracer.Instance.Idle.WaitingForKeyLock.Begin($"saga={saga.Name} key={instanceKey}"))
            using (saga.KeyLock.AcquireAsync(instanceKey, ct).GetAwaiter().GetResult())
            {
                if (saga.StepJournal.StepAlreadyCompleted(saga.Name, instanceKey, stepName))
                {
                    SagaTracer.Instance.OnStepSkippedByIdempotency(saga.Name, stepName, instanceKey);
                    return;
                }

                using var span = SagaTracer.Instance.StartStepSpan(saga.Name, stepName, instanceKey);
                try
                {
                    handler(actor, msg);
                    saga.StepJournal.MarkStepCompleted(saga.Name, instanceKey, stepName);
                    span.SetOutcome(FlowOutcome.Success);
                }
                catch (Exception ex)
                {
                    span.SetOutcome(FlowOutcome.Failure);
                    SagaTracer.Instance.OnStepFailed(saga.Name, stepName, instanceKey, ex);
                    throw;
                }
            }
        }
    }

    internal sealed class SagaAsyncHandler<TMessage> : IDispatchHandler
        where TMessage : IDispatchMessage
    {
        private readonly SagaDefinition saga;
        private readonly string stepName;
        private readonly Func<TMessage, string> instanceKeySelector;
        private readonly Func<ActorV2, TMessage, Task> handler;

        internal SagaAsyncHandler(SagaDefinition saga, string stepName,
            Func<TMessage, string> instanceKeySelector, Func<ActorV2, TMessage, Task> handler)
        {
            this.saga = saga;
            this.stepName = stepName;
            this.instanceKeySelector = instanceKeySelector;
            this.handler = handler;
        }

        public string HandlerName => $"{saga.Name}.{stepName}";

        public void Execute(ActorV2 actor, string rawMessage, CancellationToken ct)
        {
            var msg = (TMessage)TMessage.Deserialize(rawMessage);
            var instanceKey = instanceKeySelector(msg);

            if (saga.StepJournal.StepAlreadyCompleted(saga.Name, instanceKey, stepName))
            {
                SagaTracer.Instance.OnStepSkippedByIdempotency(saga.Name, stepName, instanceKey);
                return;
            }

            using (SagaTracer.Instance.Idle.WaitingForKeyLock.Begin($"saga={saga.Name} key={instanceKey}"))
            using (saga.KeyLock.AcquireAsync(instanceKey, ct).GetAwaiter().GetResult())
            {
                if (saga.StepJournal.StepAlreadyCompleted(saga.Name, instanceKey, stepName))
                {
                    SagaTracer.Instance.OnStepSkippedByIdempotency(saga.Name, stepName, instanceKey);
                    return;
                }

                using var span = SagaTracer.Instance.StartStepSpan(saga.Name, stepName, instanceKey);
                try
                {
                    handler(actor, msg).GetAwaiter().GetResult();
                    saga.StepJournal.MarkStepCompleted(saga.Name, instanceKey, stepName);
                    span.SetOutcome(FlowOutcome.Success);
                }
                catch (Exception ex)
                {
                    span.SetOutcome(FlowOutcome.Failure);
                    SagaTracer.Instance.OnStepFailed(saga.Name, stepName, instanceKey, ex);
                    throw;
                }
            }
        }
    }

    internal sealed class SagaAsyncCancellableHandler<TMessage> : IDispatchHandler
        where TMessage : IDispatchMessage
    {
        private readonly SagaDefinition saga;
        private readonly string stepName;
        private readonly Func<TMessage, string> instanceKeySelector;
        private readonly Func<ActorV2, TMessage, CancellationToken, Task> handler;

        internal SagaAsyncCancellableHandler(SagaDefinition saga, string stepName,
            Func<TMessage, string> instanceKeySelector, Func<ActorV2, TMessage, CancellationToken, Task> handler)
        {
            this.saga = saga;
            this.stepName = stepName;
            this.instanceKeySelector = instanceKeySelector;
            this.handler = handler;
        }

        public string HandlerName => $"{saga.Name}.{stepName}";

        public void Execute(ActorV2 actor, string rawMessage, CancellationToken ct)
        {
            var msg = (TMessage)TMessage.Deserialize(rawMessage);
            var instanceKey = instanceKeySelector(msg);

            if (saga.StepJournal.StepAlreadyCompleted(saga.Name, instanceKey, stepName))
            {
                SagaTracer.Instance.OnStepSkippedByIdempotency(saga.Name, stepName, instanceKey);
                return;
            }

            using (SagaTracer.Instance.Idle.WaitingForKeyLock.Begin($"saga={saga.Name} key={instanceKey}"))
            using (saga.KeyLock.AcquireAsync(instanceKey, ct).GetAwaiter().GetResult())
            {
                if (saga.StepJournal.StepAlreadyCompleted(saga.Name, instanceKey, stepName))
                {
                    SagaTracer.Instance.OnStepSkippedByIdempotency(saga.Name, stepName, instanceKey);
                    return;
                }

                using var span = SagaTracer.Instance.StartStepSpan(saga.Name, stepName, instanceKey);
                try
                {
                    handler(actor, msg, ct).GetAwaiter().GetResult();
                    saga.StepJournal.MarkStepCompleted(saga.Name, instanceKey, stepName);
                    span.SetOutcome(FlowOutcome.Success);
                }
                catch (Exception ex)
                {
                    span.SetOutcome(FlowOutcome.Failure);
                    SagaTracer.Instance.OnStepFailed(saga.Name, stepName, instanceKey, ex);
                    throw;
                }
            }
        }
    }
}
