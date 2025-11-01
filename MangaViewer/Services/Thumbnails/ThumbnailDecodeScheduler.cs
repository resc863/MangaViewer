using MangaViewer.ViewModels;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MangaViewer.Services.Thumbnails
{
    /// <summary>
    /// 썸네일 디코드 요청을 관리하는 우선순위 스케줄러.
    /// - 화면에 가까운 이미지(거리 기준)부터 우선 처리합니다.
    /// - 동일 경로라도 서로 다른 VM(뷰 항목)이면 각각 스케줄링하여 캐시 적용 기회를 보장합니다.
    /// - 동시 수행 최대 개수(<see cref="_maxConcurrency"/>)를 제한하여 CPU/메모리 사용량을 제어합니다.
    /// </summary>
    public sealed class ThumbnailDecodeScheduler
    {
        private enum RequestGroup
        {
            Thumbnails = 0,
            Bookmarks = 1,
        }

        /// <summary>
        /// 작업 큐에 들어가는 단일 요청.
        /// </summary>
        private sealed class Request
        {
            public string Key = string.Empty; // Unique key per (path, vm)
            public MangaPageViewModel Vm = null!; // 대상 VM
            public string Path = string.Empty; // 원본 경로 또는 mem: 키
            public int Index; // 전체 목록에서의 인덱스(거리 계산용)
            public int Priority; // 우선순위(작을수록 먼저)
            public DispatcherQueue Dispatcher = null!; // UI 디스패처
            public long Order; // 입력 순서 (tie-breaker)
            public RequestGroup Group; // 요청 그룹(일반 썸네일/북마크)
        }

        private static readonly Lazy<ThumbnailDecodeScheduler> _instance = new(() => new ThumbnailDecodeScheduler());
        public static ThumbnailDecodeScheduler Instance => _instance.Value;

        private readonly List<Request> _pending = new();
        private readonly HashSet<string> _pendingKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _runningKeys = new(StringComparer.Ordinal);
        private readonly object _lock = new();

        private long _orderCounter;
        private int _running;
        private int _maxConcurrency;
        public int MaxConcurrency => _maxConcurrency;

        private ThumbnailDecodeScheduler()
        {
            _maxConcurrency = Math.Clamp(Environment.ProcessorCount / 3, 2, 4);
        }

        private static string MakeKey(string path, MangaPageViewModel vm)
        {
            // 서로 다른 VM은 동일 경로라도 독립적으로 스케줄링
            return path + "|vm=" + vm.GetHashCode().ToString();
        }

        /// <summary>
        /// 동시 수행 최대 개수를 설정합니다.
        /// </summary>
        public void SetMaxConcurrency(int value)
        {
            lock (_lock)
            {
                _maxConcurrency = Math.Clamp(value, 1, 16);
                TrySchedule_NoLock();
            }
        }

        /// <summary>
        /// 리스트에 표시되거나 표시 직전일 때 호출되어 디코드 요청을 큐에 추가합니다.
        /// - 이미 썸네일이 있거나 로딩 중이면 무시합니다.
        /// - 동일 (path, vm) 조합이 대기/실행 중이면 중복 추가하지 않습니다.
        /// </summary>
        public void Enqueue(MangaPageViewModel vm, string path, int index, int selectedIndex, DispatcherQueue dispatcher)
        {
            Enqueue(vm, path, index, selectedIndex, dispatcher, isBookmark: false);
        }

        /// <summary>
        /// 북마크 전용 enqueue. 일반 썸네일 대기열 교체/우선순위 갱신의 영향을 받지 않습니다.
        /// </summary>
        public void EnqueueBookmark(MangaPageViewModel vm, string path, DispatcherQueue dispatcher)
        {
            // 북마크는 별도 그룹으로 처리하고 우선순위를 높게 설정(-10)
            Enqueue(vm, path, index: 0, selectedIndex: 0, dispatcher, isBookmark: true);
        }

        private void Enqueue(MangaPageViewModel vm, string path, int index, int selectedIndex, DispatcherQueue dispatcher, bool isBookmark)
        {
            if (string.IsNullOrEmpty(path) || vm.HasThumbnail || vm.IsThumbnailLoading)
                return;

            int distance = isBookmark
                ? -10 // 북마크는 가볍게 우선 처리
                : (selectedIndex >= 0 ? Math.Abs(index - selectedIndex) : index);
            string key = MakeKey(path, vm);

            lock (_lock)
            {
                if (_pendingKeys.Contains(key) || _runningKeys.Contains(key))
                    return;

                _pending.Add(new Request
                {
                    Key = key,
                    Vm = vm,
                    Path = path,
                    Index = index,
                    Priority = distance,
                    Dispatcher = dispatcher,
                    Order = _orderCounter++,
                    Group = isBookmark ? RequestGroup.Bookmarks : RequestGroup.Thumbnails
                });
                _pendingKeys.Add(key);

                SortPending_NoLock();
                TrySchedule_NoLock();
            }
        }

        /// <summary>
        /// 선택 인덱스가 바뀌었을 때 호출하여 대기열의 우선순위를 갱신합니다.
        /// 너무 멀리 떨어진 항목(거리 &gt;200)은 대기열에서 제거합니다.
        /// 북마크 그룹은 영향받지 않습니다.
        /// </summary>
        public void UpdateSelectedIndex(int selectedIndex)
        {
            lock (_lock)
            {
                for (int i = 0; i < _pending.Count; i++)
                {
                    var req = _pending[i];
                    if (req.Group != RequestGroup.Thumbnails) continue; // 북마크는 건드리지 않음
                    req.Priority = selectedIndex >= 0 ? Math.Abs(req.Index - selectedIndex) : req.Index;
                }

                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    var req = _pending[i];
                    if (req.Group != RequestGroup.Thumbnails) continue; // 북마크 보존
                    if (req.Priority > 200)
                    {
                        _pendingKeys.Remove(req.Key);
                        _pending.RemoveAt(i);
                    }
                }

                SortPending_NoLock();
                TrySchedule_NoLock();
            }
        }

        /// <summary>
        /// 대기 중 요청을 전부 교체하고, 현재 뷰포트 중심 우선 순서를 부여한 시드로 재구성합니다.
        /// 실행 중인 요청은 그대로 유지합니다.
        /// 북마크 그룹의 대기 요청은 유지됩니다.
        /// </summary>
        public void ReplacePendingWithViewportFirst(
            IReadOnlyList<(MangaPageViewModel Vm, string Path, int Index)> seeds,
            int pivot,
            DispatcherQueue dispatcher)
        {
            lock (_lock)
            {
                // 기존 대기 중인 "일반 썸네일"만 제거하고 북마크는 유지
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    if (_pending[i].Group == RequestGroup.Thumbnails)
                    {
                        _pendingKeys.Remove(_pending[i].Key);
                        _pending.RemoveAt(i);
                    }
                }

                foreach (var s in seeds)
                {
                    if (string.IsNullOrEmpty(s.Path))
                        continue;

                    string key = MakeKey(s.Path, s.Vm);
                    if (_runningKeys.Contains(key))
                        continue; // 이미 실행 중인 동일 (path, vm)
                    if (_pendingKeys.Contains(key))
                        continue; // 이미 대기 중(북마크 등)

                    if (s.Vm.HasThumbnail || s.Vm.IsThumbnailLoading)
                        continue;

                    int priority = pivot >= 0 ? Math.Abs(s.Index - pivot) : s.Index;
                    if (priority > 200)
                        continue; // 너무 먼 항목 제외

                    var req = new Request
                    {
                        Key = key,
                        Vm = s.Vm,
                        Path = s.Path,
                        Index = s.Index,
                        Priority = priority,
                        Dispatcher = dispatcher,
                        Order = _orderCounter++,
                        Group = RequestGroup.Thumbnails
                    };

                    _pending.Add(req);
                    _pendingKeys.Add(key);
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
            while (_running < _maxConcurrency && _pending.Count > 0)
            {
                var req = _pending[0];
                _pending.RemoveAt(0);
                _pendingKeys.Remove(req.Key);
                _runningKeys.Add(req.Key);
                _running++;
                _ = ProcessAsync(req);
            }
        }

        private async Task ProcessAsync(Request req)
        {
            try
            {
                await req.Vm.EnsureThumbnailAsync(req.Dispatcher);
            }
            catch
            {
                // ignore
            }
            finally
            {
                lock (_lock)
                {
                    _running--;
                    _runningKeys.Remove(req.Key);
                    TrySchedule_NoLock();
                }
            }
        }
    }
}
