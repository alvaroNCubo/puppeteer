using System;

namespace Choreography.StageManager
{
    public readonly struct PerformerId : IEquatable<PerformerId>, IComparable<PerformerId>
    {
        public Guid Value { get; }

        public PerformerId(Guid value)
        {
            if (value == Guid.Empty) throw new ArgumentException("PerformerId cannot be empty", nameof(value));
            Value = value;
        }

        public static PerformerId New() => new PerformerId(Guid.NewGuid());

        public static PerformerId From(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length != 16) throw new ArgumentException("PerformerId requires exactly 16 bytes", nameof(bytes));
            return new PerformerId(new Guid(bytes));
        }

        public byte[] ToBytes() => Value.ToByteArray();

        public int CompareTo(PerformerId other) => Value.CompareTo(other.Value);

        public bool Equals(PerformerId other) => Value.Equals(other.Value);

        public override bool Equals(object obj) => obj is PerformerId other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => Value.ToString("N");

        public static bool operator ==(PerformerId left, PerformerId right) => left.Equals(right);
        public static bool operator !=(PerformerId left, PerformerId right) => !left.Equals(right);
        public static bool operator <(PerformerId left, PerformerId right) => left.CompareTo(right) < 0;
        public static bool operator >(PerformerId left, PerformerId right) => left.CompareTo(right) > 0;
        public static bool operator <=(PerformerId left, PerformerId right) => left.CompareTo(right) <= 0;
        public static bool operator >=(PerformerId left, PerformerId right) => left.CompareTo(right) >= 0;
    }
}
