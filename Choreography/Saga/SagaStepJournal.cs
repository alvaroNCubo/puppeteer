using System;
using System.Collections.Concurrent;

namespace Choreography.Saga
{
    // La idempotencia de SagaStepJournal es SOLO intra-proceso.
    // Tras un restart el diccionario arranca vacio y el mismo evento reentregado
    // por el sentinel dispara la re-ejecucion del step (incluyendo I/O externo
    // como broadcast de TX en blockchain). Para proteccion cross-restart el
    // dominio debe rechazar via Check (ej: Check(!swap.ApproveBroadcasted)) de
    // modo que el PerformCommand falle si la etapa ya quedo registrada en el
    // journal. Los checkpoints viven en el journal del actor, no aqui.
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
