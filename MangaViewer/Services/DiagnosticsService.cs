using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace MangaViewer.Services
{
    /// <summary>
    /// DiagnosticsService
    /// Purpose: Aggregate success/failure counts and average latency for named operations.
    /// Implementation:
    ///  - Uses ConcurrentDictionary<string, Counter> ensuring thread-safe updates without explicit locks.
    ///  - Counter fields updated atomically via Interlocked operations.
    ///  - Average latency computed lazily on Get (TotalTicks / TotalOps).
    /// Extension Ideas:
    ///  - Add percentile tracking using reservoir sampling.
    ///  - Expose periodic snapshot export (e.g., logging every N minutes).
    ///  - Integrate with UI diagnostics panel for live monitoring.
    /// </summary>
    public sealed class DiagnosticsService
    {
        private static readonly Lazy<DiagnosticsService> _instance = new(() => new DiagnosticsService());
        public static DiagnosticsService Instance => _instance.Value;
        private DiagnosticsService() { }

        private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.OrdinalIgnoreCase);

        private sealed class Counter
        {
            public long SuccessCount;
            public long FailureCount;
            public long TotalTicks; // sum of elapsed ticks
        }

        public void Record(string name, bool success, long elapsedTicks)
        {
            var c = _counters.GetOrAdd(name, _ => new Counter());
            if (success) Interlocked.Increment(ref c.SuccessCount); else Interlocked.Increment(ref c.FailureCount);
            Interlocked.Add(ref c.TotalTicks, elapsedTicks);
        }

        public (long success, long failure, double avgMs) Get(string name)
        {
            if (_counters.TryGetValue(name, out var c))
            {
                long totalOps = c.SuccessCount + c.FailureCount;
                double avg = totalOps > 0 ? (c.TotalTicks / (double)totalOps) / TimeSpan.TicksPerMillisecond : 0;
                return (c.SuccessCount, c.FailureCount, avg);
            }
            return (0,0,0);
        }
    }
}
