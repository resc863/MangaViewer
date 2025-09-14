using MangaViewer.ViewModels;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MangaViewer.Services.Thumbnails
{
    /// <summary>
    /// 썸네일 디코딩 우선순위 스케줄러 (선택 인덱스 인근 우선).
    /// </summary>
    public sealed class ThumbnailDecodeScheduler
    {
        private sealed class Request
        {
            public MangaPageViewModel Vm = null!;
            public string Path = string.Empty;
            public int Index;
            public int Priority;
            public DispatcherQueue Dispatcher = null!;
            public long Order;
        }

        private static readonly Lazy<ThumbnailDecodeScheduler> _instance = new(() => new ThumbnailDecodeScheduler());
        public static ThumbnailDecodeScheduler Instance => _instance.Value;

        private readonly List<Request> _pending = new();
        private readonly HashSet<string> _pendingKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _runningKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();
        private long _orderCounter;
        private int _running;
        private const int MaxConcurrency = 4;
        private int _selectedIndex = -1;

        private ThumbnailDecodeScheduler() { }

        /// <summary>
        /// 항목이 실제로 화면에 등장(컨테이너 생성)할 때 큐잉.
        /// </summary>
        public void Enqueue(MangaPageViewModel vm, string path, int index, int selectedIndex, DispatcherQueue dispatcher)
        {
            if (string.IsNullOrEmpty(path) || vm.HasThumbnail || vm.IsThumbnailLoading) return;
            int distance = selectedIndex >= 0 ? Math.Abs(index - selectedIndex) : index;
            lock (_lock)
            {
                if (_pendingKeys.Contains(path) || _runningKeys.Contains(path)) return;
                _pending.Add(new Request
                {
                    Vm = vm,
                    Path = path,
                    Index = index,
                    Priority = distance,
                    Dispatcher = dispatcher,
                    Order = _orderCounter++
                });
                _pendingKeys.Add(path);
                SortPending_NoLock();
                TrySchedule_NoLock();
            }
        }

        /// <summary>
        /// 선택 인덱스 변경 -> 모든 대기 요청 우선순위 갱신.
        /// 멀리 떨어진(>200) 항목은 드롭하여 낭비 최소화.
        /// </summary>
        public void UpdateSelectedIndex(int selectedIndex)
        {
            lock (_lock)
            {
                if (_selectedIndex == selectedIndex) return;
                _selectedIndex = selectedIndex;
                for (int i = 0; i < _pending.Count; i++)
                {
                    var req = _pending[i];
                    req.Priority = selectedIndex >= 0 ? Math.Abs(req.Index - selectedIndex) : req.Index;
                }
                // 먼 항목 제거
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    if (_pending[i].Priority > 200)
                    {
                        _pendingKeys.Remove(_pending[i].Path);
                        _pending.RemoveAt(i);
                    }
                }
                SortPending_NoLock();
                TrySchedule_NoLock();
            }
        }

        private void SortPending_NoLock() => _pending.Sort((a, b) =>
        {
            int c = a.Priority.CompareTo(b.Priority);
            return c != 0 ? c : a.Order.CompareTo(b.Order);
        });

        private void TrySchedule_NoLock()
        {
            while (_running < MaxConcurrency && _pending.Count > 0)
            {
                var req = _pending[0];
                _pending.RemoveAt(0);
                _pendingKeys.Remove(req.Path);
                _runningKeys.Add(req.Path);
                _running++;
                _ = ProcessAsync(req);
            }
        }

        private async Task ProcessAsync(Request req)
        {
            try { await req.Vm.EnsureThumbnailAsync(req.Dispatcher); }
            catch { }
            finally
            {
                lock (_lock)
                {
                    _running--;
                    _runningKeys.Remove(req.Path);
                    TrySchedule_NoLock();
                }
            }
        }
    }
}
