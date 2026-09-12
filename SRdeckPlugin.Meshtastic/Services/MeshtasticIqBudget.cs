using System.Numerics;

namespace SRdeckPlugin.Meshtastic.Services;

/// <summary>Bounds owned IQ, including the block currently being decoded.</summary>
internal sealed class MeshtasticIqBudget
{
    internal const long MaximumBytes = 64L * 1024 * 1024;
    internal const long MaximumMicroseconds = 5_000_000;
    private readonly object gate = new();
    private long bytes;
    private long microseconds;

    internal (long Bytes, long Microseconds) Snapshot
    {
        get { lock (gate) return (bytes, microseconds); }
    }

    internal Reservation? TryReserve(int count, int rate)
    {
        // Reserve the shared pool's size class before renting or converting IQ.
        long capacity = BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, count));
        long requestedBytes = capacity * sizeof(short) * 2;
        long duration = ((long)count * 1_000_000 + rate - 1) / rate;
        lock (gate)
        {
            if (capacity == 0 || requestedBytes > MaximumBytes - bytes ||
                duration > MaximumMicroseconds - microseconds) return null;
            bytes += requestedBytes;
            microseconds += duration;
            return new Reservation(this, requestedBytes, duration);
        }
    }

    internal sealed class Reservation(MeshtasticIqBudget owner, long bytes, long microseconds) : IDisposable
    {
        private long reservedBytes = bytes;
        private bool disposed;

        internal bool TrySetActualBytes(long actualBytes)
        {
            lock (owner.gate)
            {
                if (disposed || actualBytes - reservedBytes > MaximumBytes - owner.bytes) return false;
                owner.bytes += actualBytes - reservedBytes;
                reservedBytes = actualBytes;
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner.gate)
            {
                if (disposed) return;
                disposed = true;
                owner.bytes -= reservedBytes;
                owner.microseconds -= microseconds;
            }
        }
    }
}
