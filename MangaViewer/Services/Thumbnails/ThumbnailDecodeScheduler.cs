using MangaViewer.ViewModels;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MangaViewer.Services.Thumbnails
{
    /// <summary>
    /// ����� ���ڵ� ��û�� �����ϴ� �켱���� �����ٷ�.
    /// - ȭ�鿡 ����� �ε���(���� �ε��� ���� �Ÿ�)�� �켱 ����˴ϴ�.
    /// - ���� ���(path)�� �ߺ� ��û�� ����(coalescing)�Ͽ� ���ʿ��� ���ڵ��� ���Դϴ�.
    /// - ���� ���� �ִ� ����(<see cref="MaxConcurrency"/>)�� ������ CPU/�޸� ��뷮�� �����մϴ�.
    /// </summary>
    public sealed class ThumbnailDecodeScheduler
    {
        /// <summary>
        /// ���� ť�� ����Ǵ� ���� ��û ����.
        /// </summary>
        private sealed class Request
        {
            /// <summary>������� ������ ��� <see cref="MangaPageViewModel"/>.</summary>
            public MangaPageViewModel Vm = null!;
            /// <summary>�̹��� ���� ���(���� ��� �Ǵ� mem: Ű).</summary>
            public string Path = string.Empty;
            /// <summary>�ش� �׸��� ��ü ��� �ε���.</summary>
            public int Index;
            /// <summary>�켱���� ��(�������� �켱). �Ϲ������� ���� �ε������� �Ÿ�.</summary>
            public int Priority;
            /// <summary>UI ������ ���� �۾��� �ʿ��� ��� ���Ǵ� ����ó.</summary>
            public DispatcherQueue Dispatcher = null!;
            /// <summary>�Է� ���� ������ ���� ������(���� �켱���� �� tie-breaker).</summary>
            public long Order;
        }

        private static readonly Lazy<ThumbnailDecodeScheduler> _instance = new(() => new ThumbnailDecodeScheduler());
        /// <summary>
        /// ���� �̱��� �ν��Ͻ�.
        /// </summary>
        public static ThumbnailDecodeScheduler Instance => _instance.Value;

        // ��û ��⿭ �� ���� ���� �����̳�
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
        /// �׸��� ȭ�鿡 ��Ÿ�� �����̳ʰ� ������ �� ȣ��Ǿ� ���ڵ� ��û�� ť�� �߰��մϴ�.
        /// - �̹� ������� �����ϰų� �ε� ���̸� �����մϴ�.
        /// - ���� ��ο� ���� �ߺ� ���/���� ��û�� �����մϴ�.
        /// </summary>
        /// <param name="vm">��� ������ ViewModel</param>
        /// <param name="path">���� ���(���� �Ǵ� mem: Ű)</param>
        /// <param name="index">��ü ��Ͽ����� �ε���</param>
        /// <param name="selectedIndex">���� ����(�Ǵ� �߾�) �ε���</param>
        /// <param name="dispatcher">UI ������ ȣ��� ����ó</param>
        public void Enqueue(MangaPageViewModel vm, string path, int index, int selectedIndex, DispatcherQueue dispatcher)
        {
            if (string.IsNullOrEmpty(path) || vm.HasThumbnail || vm.IsThumbnailLoading) return;
            int distance = selectedIndex >= 0 ? Math.Abs(index - selectedIndex) : index;
            lock (_lock)
            {
                // ���� ��ΰ� �̹� ���/���� ���̸� �ǳʶݴϴ�.
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
        /// ���� �ε����� ����Ǿ��� �� ȣ���Ͽ� ��� ��� ��û�� �켱������ �����մϴ�.
        /// - ���� �ε������� �Ÿ��� ���� ����մϴ�.
        /// - �ʹ� �ָ� ������ �׸�(�Ÿ� &gt; 200)�� ��⿭���� �����Ͽ� ���� �����ϴ�.
        /// </summary>
        /// <param name="selectedIndex">���ο� ���� �ε���</param>
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
                // �� �׸� ����
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
        /// ��⿭�� �켱����(��������) �� �Է¼���(��������) �������� �����մϴ�.
        /// </summary>
        private void SortPending_NoLock() => _pending.Sort((a, b) =>
        {
            int c = a.Priority.CompareTo(b.Priority);
            return c != 0 ? c : a.Order.CompareTo(b.Order);
        });

        /// <summary>
        /// ���� ���� �� ������ <see cref="MaxConcurrency"/> �̸��� ���� ��� ��û�� ���� �����մϴ�.
        /// </summary>
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

        /// <summary>
        /// ���� ��û�� ó���մϴ�. ���������� <see cref="MangaPageViewModel.EnsureThumbnailAsync(DispatcherQueue)"/>�� ȣ���մϴ�.
        /// �Ϸ� �� ���� �� ī��Ʈ�� ���ҽ�Ű�� ���� ��� ��û�� �������մϴ�.
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
