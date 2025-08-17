using MangaViewer.ViewModels;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MangaViewer.Services
{
    /// <summary>
    /// 썸네일 디코딩 우선순위 스케줄러.
    /// 선택한 인덱스 주변(거리 기반) 먼저 디코딩하여 체감 속도 향상.
    /// 간단한 리스트 정렬 방식, 동시성 제한.
    /// </summary>
    public sealed class ThumbnailDecodeScheduler
    {
        private class Request
        {
            public MangaPageViewModel Vm { get; init; } = null!;
            public string Path { get; init; } = string.Empty;
            public int Index { get; init; }          // 전체 목록 내 인덱스
            public int Priority { get; set; }        // 선택 인덱스와의 거리
            public DispatcherQueue Dispatcher { get; init; } = null!;
            public long Order { get; init; }         // FIFO 안정 정렬용
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
                TrySchedule();
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
                foreach (var req in _pending)
                {
                    req.Priority = selectedIndex >= 0 ? Math.Abs(req.Index - selectedIndex) : req.Index;
                }
                SortPending_NoLock();
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    if (_pending[i].Priority > 200)
                    {
                        _pendingKeys.Remove(_pending[i].Path);
                        _pending.RemoveAt(i);
                    }
                }
                TrySchedule();
            }
        }

        private void SortPending_NoLock() =>
            _pending.Sort((a, b) =>
            {
                int c = a.Priority.CompareTo(b.Priority);
                return c != 0 ? c : a.Order.CompareTo(b.Order);
            });

        private void TrySchedule()
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
            catch { /* 실패 무시 */ }
            finally
            {
                lock (_lock)
                {
                    _running--;
                    _runningKeys.Remove(req.Path);
                    TrySchedule();
                }
            }
        }
    }
}
