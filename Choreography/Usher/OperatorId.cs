using System;

namespace Choreography.Usher
{
    public readonly struct OperatorId : IEquatable<OperatorId>
    {
        public Guid Value { get; }

        public OperatorId(Guid value)
        {
            if (value == Guid.Empty) throw new ArgumentException("OperatorId cannot be empty", nameof(value));
            Value = value;
        }

        public static OperatorId New() => new OperatorId(Guid.NewGuid());

        public bool Equals(OperatorId other) => Value.Equals(other.Value);
        public override bool Equals(object obj) => obj is OperatorId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString("N");

        public static bool operator ==(OperatorId left, OperatorId right) => left.Equals(right);
        public static bool operator !=(OperatorId left, OperatorId right) => !left.Equals(right);
    }
}
