using MangaViewer.ViewModels;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MangaViewer.Services.Thumbnails
{
    /// <summary>
    /// 썸네일 디코딩 요청을 관리하는 우선순위 스케줄러.
    /// - 화면에 가까운 인덱스(선택 인덱스 기준 거리)가 우선 실행됩니다.
    /// - 동일 경로(path)의 중복 요청은 병합(coalescing)하여 불필요한 디코딩을 줄입니다.
    /// - 동시 실행 최대 개수(<see cref="_maxConcurrency"/>)를 제한해 CPU/메모리 사용량을 제어합니다.
    /// </summary>
    public sealed class ThumbnailDecodeScheduler
    {
        /// <summary>
        /// 내부 큐에 저장되는 단일 요청 단위.
        /// </summary>
        private sealed class Request
        {
            /// <summary>썸네일을 생성할 대상 <see cref="MangaPageViewModel"/>.</summary>
            public MangaPageViewModel Vm = null!;
            /// <summary>이미지 원본 경로(파일 경로 또는 mem: 키).</summary>
            public string Path = string.Empty;
            /// <summary>해당 항목의 전체 목록 인덱스.</summary>
            public int Index;
            /// <summary>우선순위 값(작을수록 우선). 일반적으로 선택 인덱스와의 거리.</summary>
            public int Priority;
            /// <summary>UI 스레드 전용 작업이 필요한 경우 사용되는 디스패처.</summary>
            public DispatcherQueue Dispatcher = null!;
            /// <summary>입력 순서 보존을 위한 증가값(동일 우선순위 시 tie-breaker).</summary>
            public long Order;
        }

        private static readonly Lazy<ThumbnailDecodeScheduler> _instance = new(() => new ThumbnailDecodeScheduler());
        /// <summary>
        /// 전역 싱글톤 인스턴스.
        /// </summary>
        public static ThumbnailDecodeScheduler Instance => _instance.Value;

        // 요청 대기열 및 상태 추적 컨테이너
        private readonly List<Request> _pending = new();
        private readonly HashSet<string> _pendingKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _runningKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();
        private long _orderCounter;
        private int _running;
        private readonly int _maxConcurrency;
        private int _selectedIndex = -1;

        private ThumbnailDecodeScheduler()
        {
            // 동시 실행 수: 8C/16T 기준으로 /2 => 8, 최소 4, 최대 8
            _maxConcurrency = Math.Clamp(Environment.ProcessorCount / 2, 4, 8);
        }

        /// <summary>
        /// 항목이 화면에 나타나 컨테이너가 생성될 때 호출되어 디코딩 요청을 큐에 추가합니다.
        /// - 이미 썸네일이 존재하거나 로딩 중이면 무시합니다.
        /// - 동일 경로에 대한 중복 대기/실행 요청은 병합합니다.
        /// </summary>
        /// <param name="vm">대상 페이지 ViewModel</param>
        /// <param name="path">원본 경로(파일 또는 mem: 키)</param>
        /// <param name="index">전체 목록에서의 인덱스</param>
        /// <param name="selectedIndex">현재 선택(또는 중앙) 인덱스</param>
        /// <param name="dispatcher">UI 스레드 호출용 디스패처</param>
        public void Enqueue(MangaPageViewModel vm, string path, int index, int selectedIndex, DispatcherQueue dispatcher)
        {
            if (string.IsNullOrEmpty(path) || vm.HasThumbnail || vm.IsThumbnailLoading) return;
            int distance = selectedIndex >= 0 ? Math.Abs(index - selectedIndex) : index;
            lock (_lock)
            {
                // 동일 경로가 이미 대기/실행 중이면 건너뜁니다.
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
        /// 선택 인덱스가 변경되었을 때 호출하여 모든 대기 요청의 우선순위를 갱신합니다.
        /// - 선택 인덱스와의 거리를 새로 계산합니다.
        /// - 너무 멀리 떨어진 항목(거리 &gt; 200)은 대기열에서 제거하여 낭비를 막습니다.
        /// </summary>
        /// <param name="selectedIndex">새로운 선택 인덱스</param>
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

        /// <summary>
        /// 현재 대기 중인 요청을 모두 비우고(실행 중은 유지), 뷰포트 중심 우선으로 주어진 시드 목록으로 재구성합니다.
        /// - 이미 실행 중인 경로는 제외하여 중복 실행을 방지합니다.
        /// - 이미 썸네일이 있거나 로딩 중인 항목은 제외합니다.
        /// - 기존 정책(거리 &gt; 200 제거)도 동일하게 적용합니다.
        /// </summary>
        public void ReplacePendingWithViewportFirst(
            System.Collections.Generic.IReadOnlyList<(MangaViewer.ViewModels.MangaPageViewModel Vm, string Path, int Index)> seeds,
            int pivot,
            DispatcherQueue dispatcher)
        {
            lock (_lock)
            {
                _selectedIndex = pivot;
                _pending.Clear();
                _pendingKeys.Clear();

                foreach (var s in seeds)
                {
                    if (string.IsNullOrEmpty(s.Path)) continue;
                    if (_runningKeys.Contains(s.Path)) continue; // 실행 중 유지
                    if (s.Vm.HasThumbnail || s.Vm.IsThumbnailLoading) continue;

                    int priority = pivot >= 0 ? Math.Abs(s.Index - pivot) : s.Index;
                    var req = new Request
                    {
                        Vm = s.Vm,
                        Path = s.Path,
                        Index = s.Index,
                        Priority = priority,
                        Dispatcher = dispatcher,
                        Order = _orderCounter++
                    };

                    // 거리>200은 넣지 않음(자연 정리)
                    if (req.Priority > 200) continue;

                    _pending.Add(req);
                    _pendingKeys.Add(req.Path);
                }

                SortPending_NoLock();
                TrySchedule_NoLock();
            }
        }

        /// <summary>
        /// 대기열을 우선순위(오름차순) → 입력순서(오름차순) 기준으로 정렬합니다.
        /// </summary>
        private void SortPending_NoLock() => _pending.Sort((a, b) =>
        {
            int c = a.Priority.CompareTo(b.Priority);
            return c != 0 ? c : a.Order.CompareTo(b.Order);
        });

        /// <summary>
        /// 현재 실행 중 개수가 <see cref="_maxConcurrency"/> 미만인 동안 대기 요청을 꺼내 실행합니다.
        /// </summary>
        private void TrySchedule_NoLock()
        {
            while (_running < _maxConcurrency && _pending.Count > 0)
            {
                var req = _pending[0];
                _pending.RemoveAt(0);
                _pendingKeys.Remove(req.Path);
                _runningKeys.Add(req.Path);
                _running++;
                _ = ProcessAsync(req);
            }
        }

        /// <summary>
        /// 단일 요청을 처리합니다. 내부적으로 <see cref="MangaPageViewModel.EnsureThumbnailAsync(DispatcherQueue)"/>를 호출합니다.
        /// 완료 시 실행 중 카운트를 감소시키고 다음 대기 요청을 스케줄합니다.
        /// </summary>
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
