using System;
using System.Collections.Concurrent;

namespace Choreography.Saga
{
    // The idempotency of SagaStepJournal is intra-process ONLY.
    // After a restart the dictionary starts empty and the same event redelivered
    // by the sentinel triggers re-execution of the step (including external I/O
    // such as a transaction broadcast). For cross-restart protection the
    // domain must reject via Check (e.g. Check(!swap.ApproveBroadcasted)) so
    // that the PerformCommand fails if the step was already recorded in the
    // journal. The checkpoints live in the actor journal, not here.
    internal sealed class SagaStepJournal
    {
        private readonly ConcurrentDictionary<SagaStepKey, DateTime> completedSteps = new();

        internal bool StepAlreadyCompleted(string sagaName, string instanceKey, string stepName)
        {
            var key = new SagaStepKey(sagaName, instanceKey, stepName);
            return completedSteps.ContainsKey(key);
        }

        internal void MarkStepCompleted(string sagaName, string instanceKey, string stepName)
        {
            var key = new SagaStepKey(sagaName, instanceKey, stepName);
            completedSteps.TryAdd(key, DateTime.UtcNow);
        }

        internal void ClearInstance(string sagaName, string instanceKey)
        {
            foreach (var key in completedSteps.Keys)
            {
                if (key.SagaName == sagaName && key.InstanceKey == instanceKey)
                    completedSteps.TryRemove(key, out _);
            }
        }

        private readonly struct SagaStepKey : IEquatable<SagaStepKey>
        {
            internal readonly string SagaName;
            internal readonly string InstanceKey;
            internal readonly string StepName;

            internal SagaStepKey(string sagaName, string instanceKey, string stepName)
            {
                SagaName = sagaName;
                InstanceKey = instanceKey;
                StepName = stepName;
            }

            public bool Equals(SagaStepKey other)
            {
                return SagaName == other.SagaName
                    && InstanceKey == other.InstanceKey
                    && StepName == other.StepName;
            }

            public override bool Equals(object obj) => obj is SagaStepKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(SagaName, InstanceKey, StepName);
        }
    }
}
