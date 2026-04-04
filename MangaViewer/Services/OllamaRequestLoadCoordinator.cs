using System;
using System.Threading;

namespace MangaViewer.Services
{
    internal static class OllamaRequestLoadCoordinator
    {
        private static int s_pendingRequestCount;

        private static readonly TimeSpan NonThinkingQueueStep = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan ThinkingQueueStep = TimeSpan.FromSeconds(25);
        private static readonly TimeSpan NonThinkingMaxBoost = TimeSpan.FromMinutes(4);
        private static readonly TimeSpan ThinkingMaxBoost = TimeSpan.FromMinutes(8);

        internal readonly struct RequestLease : IDisposable
        {
            private readonly bool _active;

            public RequestLease(TimeSpan effectiveTimeout, int queueDepth, bool active)
            {
                EffectiveTimeout = effectiveTimeout;
                QueueDepth = queueDepth;
                _active = active;
            }

            public TimeSpan EffectiveTimeout { get; }

            public int QueueDepth { get; }

            public void Dispose()
            {
                if (_active)
                    Interlocked.Decrement(ref s_pendingRequestCount);
            }
        }

        public static RequestLease Acquire(TimeSpan baseTimeout, bool thinkingEnabled)
        {
            int queueDepth = Interlocked.Increment(ref s_pendingRequestCount);
            TimeSpan effectiveTimeout = ComputeTimeout(baseTimeout, Math.Max(0, queueDepth - 1), thinkingEnabled);
            return new RequestLease(effectiveTimeout, queueDepth, active: true);
        }

        public static TimeSpan GetSuggestedTimeout(TimeSpan baseTimeout, bool thinkingEnabled)
        {
            int pending = Volatile.Read(ref s_pendingRequestCount);
            return ComputeTimeout(baseTimeout, pending, thinkingEnabled);
        }

        private static TimeSpan ComputeTimeout(TimeSpan baseTimeout, int queuedAhead, bool thinkingEnabled)
        {
            if (baseTimeout <= TimeSpan.Zero)
                baseTimeout = TimeSpan.FromSeconds(30);

            TimeSpan step = thinkingEnabled ? ThinkingQueueStep : NonThinkingQueueStep;
            TimeSpan maxBoost = thinkingEnabled ? ThinkingMaxBoost : NonThinkingMaxBoost;

            long rawBoostTicks = (long)step.Ticks * Math.Max(0, queuedAhead);
            TimeSpan boost = rawBoostTicks <= 0
                ? TimeSpan.Zero
                : TimeSpan.FromTicks(Math.Min(rawBoostTicks, maxBoost.Ticks));

            return baseTimeout + boost;
        }
    }
}
