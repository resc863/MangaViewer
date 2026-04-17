using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MangaViewer.Services
{
    internal static class OllamaRequestLoadCoordinator
    {
        private static readonly object s_gate = new();
        private static readonly LinkedList<Waiter> s_waiters = new();
        private static readonly SortedSet<int> s_availableSlotIds = new();
        private static readonly HashSet<int> s_activeSlotIds = new();
        private static int s_maxConcurrentRequests = Math.Clamp(SettingsProvider.Get("LlamaServerMaxConcurrentRequests", 1), 1, 32);
        private static bool s_slotEraseEnabled = SettingsProvider.Get("LlamaServerSlotEraseEnabled", true);

        private static readonly TimeSpan NonThinkingQueueStep = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan ThinkingQueueStep = TimeSpan.FromSeconds(25);
        private static readonly TimeSpan NonThinkingMaxBoost = TimeSpan.FromMinutes(4);
        private static readonly TimeSpan ThinkingMaxBoost = TimeSpan.FromMinutes(8);

        static OllamaRequestLoadCoordinator()
        {
            ReconcileSlotPoolNoLock();
        }

        private sealed class Waiter
        {
            public required TaskCompletionSource<RequestLease> CompletionSource { get; init; }
            public required TimeSpan EffectiveTimeout { get; init; }
            public required int QueueDepth { get; init; }
            public required CancellationToken CancellationToken { get; init; }
            public LinkedListNode<Waiter>? Node { get; set; }
            public CancellationTokenRegistration CancellationRegistration { get; set; }

            public void Cancel()
            {
                bool removed = false;
                lock (s_gate)
                {
                    if (Node != null)
                    {
                        s_waiters.Remove(Node);
                        Node = null;
                        CancellationRegistration.Dispose();
                        removed = true;
                    }
                }

                if (removed)
                    CompletionSource.TrySetCanceled(CancellationToken);
            }
        }

        internal readonly struct RequestLease : IDisposable
        {
            private readonly bool _active;
            private readonly int _slotId;

            public RequestLease(TimeSpan effectiveTimeout, int queueDepth, int slotId, bool slotEraseEnabled, bool active)
            {
                EffectiveTimeout = effectiveTimeout;
                QueueDepth = queueDepth;
                _slotId = slotId;
                SlotEraseEnabled = slotEraseEnabled;
                _active = active;
            }

            public TimeSpan EffectiveTimeout { get; }

            public int QueueDepth { get; }

            public int SlotId => _slotId;

            public bool SlotEraseEnabled { get; }

            public void Dispose()
            {
                if (_active)
                    ReleaseSlot(_slotId);
            }
        }

        public static async Task<RequestLease> AcquireAsync(TimeSpan baseTimeout, bool thinkingEnabled, CancellationToken cancellationToken = default)
        {
            var completionSource = new TaskCompletionSource<RequestLease>(TaskCreationOptions.RunContinuationsAsynchronously);
            Waiter waiter;

            lock (s_gate)
            {
                ReconcileSlotPoolNoLock();

                int queueDepth = s_activeSlotIds.Count + s_waiters.Count + 1;
                waiter = new Waiter
                {
                    CompletionSource = completionSource,
                    EffectiveTimeout = ComputeTimeout(baseTimeout, Math.Max(0, queueDepth - 1), thinkingEnabled),
                    QueueDepth = queueDepth,
                    CancellationToken = cancellationToken
                };

                waiter.Node = s_waiters.AddLast(waiter);
                if (cancellationToken.CanBeCanceled)
                {
                    waiter.CancellationRegistration = cancellationToken.Register(static state => ((Waiter)state!).Cancel(), waiter);
                }

                DispatchWaitersNoLock();
            }

            return await completionSource.Task.ConfigureAwait(false);
        }

        public static TimeSpan GetSuggestedTimeout(TimeSpan baseTimeout, bool thinkingEnabled)
        {
            lock (s_gate)
            {
                int pending = s_activeSlotIds.Count + s_waiters.Count;
                return ComputeTimeout(baseTimeout, pending, thinkingEnabled);
            }
        }

        public static int MaxConcurrentRequests
        {
            get
            {
                lock (s_gate)
                    return s_maxConcurrentRequests;
            }
        }

        public static bool SlotEraseEnabled
        {
            get
            {
                lock (s_gate)
                    return s_slotEraseEnabled;
            }
        }

        public static void SetMaxConcurrentRequests(int value)
        {
            int clamped = Math.Clamp(value, 1, 32);
            lock (s_gate)
            {
                if (s_maxConcurrentRequests == clamped)
                    return;

                s_maxConcurrentRequests = clamped;
                ReconcileSlotPoolNoLock();
                DispatchWaitersNoLock();
            }
        }

        public static void SetSlotEraseEnabled(bool enabled)
        {
            lock (s_gate)
                s_slotEraseEnabled = enabled;
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

        private static void DispatchWaitersNoLock()
        {
            while (s_waiters.First != null && s_availableSlotIds.Count > 0)
            {
                var waiter = s_waiters.First.Value;
                s_waiters.RemoveFirst();
                waiter.Node = null;
                waiter.CancellationRegistration.Dispose();

                int slotId = s_availableSlotIds.Min;
                s_availableSlotIds.Remove(slotId);
                s_activeSlotIds.Add(slotId);

                waiter.CompletionSource.TrySetResult(new RequestLease(
                    waiter.EffectiveTimeout,
                    waiter.QueueDepth,
                    slotId,
                    s_slotEraseEnabled,
                    active: true));
            }
        }

        private static void ReleaseSlot(int slotId)
        {
            lock (s_gate)
            {
                if (!s_activeSlotIds.Remove(slotId))
                    return;

                if (slotId >= 0 && slotId < s_maxConcurrentRequests)
                    s_availableSlotIds.Add(slotId);

                DispatchWaitersNoLock();
            }
        }

        private static void ReconcileSlotPoolNoLock()
        {
            s_availableSlotIds.RemoveWhere(static slotId => slotId < 0 || slotId >= s_maxConcurrentRequests);
            for (int slotId = 0; slotId < s_maxConcurrentRequests; slotId++)
            {
                if (!s_activeSlotIds.Contains(slotId))
                    s_availableSlotIds.Add(slotId);
            }
        }
    }
}
