using MangaViewer.Helpers;
using MangaViewer.Services;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Windows.Storage.Pickers; // use WASDK pickers
using Windows.Storage; // kept for StorageFolder/Path usage
using System.Threading;
using Microsoft.UI.Xaml.Controls; // InfoBarSeverity
using MangaViewer.Services.Thumbnails; // Added for ThumbnailDecodeScheduler
using MangaViewer.Services.Logging;
using System.IO; // new for path operations

namespace MangaViewer.ViewModels
{
    /// <summary>
    /// 앱의 핵심 ViewModel.
    /// - 폴더 로드/스트리밍 추가/페이지 내비게이션/양면 표시/캐시 프리페치
    /// - OCR 실행 및 상태 표시
    /// - 썸네일 선택 연동 및 화면 전환 애니메이션 요청
    /// </summary>
    public partial class MangaViewModel : BaseViewModel, IDisposable
    {
        private readonly MangaManager _mangaManager = new();
        private readonly ImageCacheService _imageCache = ImageCacheService.Instance;
        private readonly OcrService _ocrService = OcrService.Instance;
        private readonly BookmarkService _bookmarkService = BookmarkService.Instance;

        private BitmapImage? _leftImageSource;
        private BitmapImage? _rightImageSource;
        private int _selectedThumbnailIndex = -1;
        private bool _isPaneOpen = true;
        private bool _isBookmarkPaneOpen = false;
        private bool _isNavOpen = false;
        private bool _isLoading;
        private bool _isSinglePageMode;
        private bool _isTwoPageMode;
        private bool _isOcrRunning;
        private int _previousPageIndex;
        private bool _isStreamingGallery; // EH streaming mode

        private double _leftWrapperWidth, _leftWrapperHeight;
        private double _rightWrapperWidth, _rightWrapperHeight;

        private CancellationTokenSource? _ocrCts;

        private string _ocrStatusMessage = string.Empty;
        private bool _isInfoBarOpen;
        private InfoBarSeverity _ocrSeverity = InfoBarSeverity.Informational;

        public ReadOnlyObservableCollection<MangaPageViewModel> Thumbnails => _mangaManager.Pages;

        public ObservableCollection<MangaPageViewModel> Bookmarks { get; } = new();

        public string? LeftImageFilePath { get; private set; }
        public string? RightImageFilePath { get; private set; }
        public BitmapImage? LeftImageSource { get => _leftImageSource; private set => SetProperty(ref _leftImageSource, value); }
        public BitmapImage? RightImageSource { get => _rightImageSource; private set => SetProperty(ref _rightImageSource, value); }
        public bool IsSinglePageMode { get => _isSinglePageMode; private set => SetProperty(ref _isSinglePageMode, value); }
        public bool IsTwoPageMode { get => _isTwoPageMode; private set => SetProperty(ref _isTwoPageMode, value); }
        public bool IsOcrRunning { get => _isOcrRunning; private set { if (SetProperty(ref _isOcrRunning, value)) { RunOcrCommand.RaiseCanExecuteChanged(); OnPropertyChanged(nameof(IsOpenFolderEnabled)); } } }
        public string OcrStatusMessage { get => _ocrStatusMessage; private set => SetProperty(ref _ocrStatusMessage, value); }
        public bool IsInfoBarOpen { get => _isInfoBarOpen; private set => SetProperty(ref _isInfoBarOpen, value); }
        public InfoBarSeverity OcrSeverity { get => _ocrSeverity; private set => SetProperty(ref _ocrSeverity, value); }
        public bool IsStreamingGallery { get => _isStreamingGallery; private set { if (SetProperty(ref _isStreamingGallery, value)) OnPropertyChanged(nameof(IsOpenFolderEnabled)); } }

        public bool IsOpenFolderEnabled => IsControlEnabled; // always allow folder open (streaming mode does not disable)

        /// <summary>
        /// 썸네일 선택 인덱스. 변경 시 스케줄러에 알리고 현재 페이지로 동기화합니다.
        /// </summary>
        public int SelectedThumbnailIndex
        {
            get => _selectedThumbnailIndex;
            set
            {
                if (SetProperty(ref _selectedThumbnailIndex, value))
                {
                    ThumbnailDecodeScheduler.Instance.UpdateSelectedIndex(_selectedThumbnailIndex);
                    if (value >=0)
                        _mangaManager.SetCurrentPageFromImageIndex(value);
                }
            }
        }

        public bool IsPaneOpen { get => _isPaneOpen; set => SetProperty(ref _isPaneOpen, value); }
        public bool IsBookmarkPaneOpen { get => _isBookmarkPaneOpen; set => SetProperty(ref _isBookmarkPaneOpen, value); }
        public bool IsNavOpen { get => _isNavOpen; set => SetProperty(ref _isNavOpen, value); }
        public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) { OnPropertyChanged(nameof(IsControlEnabled)); OnPropertyChanged(nameof(IsOpenFolderEnabled)); } } }
        public bool IsControlEnabled => !IsLoading && !IsOcrRunning;

        public string DirectionButtonText => _mangaManager.IsRightToLeft ? "읽기 방향: 역방향" : "읽기 방향: 정방향";
        public string CoverButtonText => _mangaManager.IsCoverSeparate ? "표지: 한 장으로 보기" : "표지: 두 장으로 보기";

        // Commands
        public AsyncRelayCommand OpenFolderCommand { get; }
        public RelayCommand NextPageCommand { get; }
        public RelayCommand PrevPageCommand { get; }
        public RelayCommand ToggleDirectionCommand { get; }
        public RelayCommand ToggleCoverCommand { get; }
        public RelayCommand TogglePaneCommand { get; }
        public RelayCommand ToggleBookmarkPaneCommand { get; }
        public RelayCommand ToggleNavPaneCommand { get; }
        public RelayCommand GoLeftCommand { get; }
        public RelayCommand GoRightCommand { get; }
        public AsyncRelayCommand RunOcrCommand { get; }
        public RelayCommand AddBookmarkCommand { get; }
        public RelayCommand RemoveBookmarkCommand { get; }
        public RelayCommand NavigateToBookmarkCommand { get; }

        private readonly ObservableCollection<BoundingBoxViewModel> _leftOcrBoxes = new();
        private readonly ObservableCollection<BoundingBoxViewModel> _rightOcrBoxes = new();
        public ReadOnlyObservableCollection<BoundingBoxViewModel> LeftOcrBoxes { get; }
        public ReadOnlyObservableCollection<BoundingBoxViewModel> RightOcrBoxes { get; }

        private BoundingBoxViewModel? _selectedOcrBox;
        public BoundingBoxViewModel? SelectedOcrBox { get => _selectedOcrBox; set => SetProperty(ref _selectedOcrBox, value); }

        public event EventHandler? OcrCompleted;
        public event EventHandler? PageViewChanged;
        public event EventHandler<int>? PageSlideRequested;

        public MangaViewModel()
        {
            LeftOcrBoxes = new ReadOnlyObservableCollection<BoundingBoxViewModel>(_leftOcrBoxes);
            RightOcrBoxes = new ReadOnlyObservableCollection<BoundingBoxViewModel>(_rightOcrBoxes);

            _mangaManager.MangaLoaded += OnMangaLoaded;
            _mangaManager.PageChanged += OnPageChanged;
            _ocrService.SettingsChanged += OnOcrSettingsChanged; // auto refresh

            OpenFolderCommand = new AsyncRelayCommand(async p => await OpenFolderAsync(p), _ => IsOpenFolderEnabled);
            NextPageCommand = new RelayCommand(_ => _mangaManager.GoToNextPage(), _ => _mangaManager.TotalImages >0);
            PrevPageCommand = new RelayCommand(_ => _mangaManager.GoToPreviousPage(), _ => _mangaManager.TotalImages >0);
            ToggleDirectionCommand = new RelayCommand(_ => { _mangaManager.ToggleDirection(); CancelOcr(); }, _ => _mangaManager.TotalImages >0);
            ToggleCoverCommand = new RelayCommand(_ => { _mangaManager.ToggleCover(); CancelOcr(); }, _ => _mangaManager.TotalImages >0);
            TogglePaneCommand = new RelayCommand(_ => IsPaneOpen = !IsPaneOpen);
            ToggleBookmarkPaneCommand = new RelayCommand(_ => IsBookmarkPaneOpen = !IsBookmarkPaneOpen);
            ToggleNavPaneCommand = new RelayCommand(_ => IsNavOpen = !IsNavOpen);
            GoLeftCommand = new RelayCommand(_ => NavigateLogicalLeft(), _ => _mangaManager.TotalImages >0);
            GoRightCommand = new RelayCommand(_ => NavigateLogicalRight(), _ => _mangaManager.TotalImages >0);
            RunOcrCommand = new AsyncRelayCommand(async _ => await RunOcrAsync(), _ => _mangaManager.TotalImages >0 && !IsOcrRunning);
            AddBookmarkCommand = new RelayCommand(_ => AddCurrentBookmark(), _ => _mangaManager.TotalImages >0);
            RemoveBookmarkCommand = new RelayCommand(o => RemoveBookmark(o as MangaPageViewModel), _ => Bookmarks.Count >0);
            NavigateToBookmarkCommand = new RelayCommand(o => NavigateToBookmark(o as MangaPageViewModel));
        }

        private async void OnOcrSettingsChanged(object? sender, EventArgs e)
        {
            if (IsOcrRunning) return;
            if (LeftImageFilePath == null && RightImageFilePath == null) return;
            if (!RunOcrCommand.CanExecute(null)) return;
            await RunOcrAsync();
        }

        /// <summary>
        /// 스트리밍 갤러리 모드를 시작합니다(플레이스홀더/상태 초기화).
        /// </summary>
        public void BeginStreamingGallery()
        {
            IsStreamingGallery = true;
            OnPropertyChanged(nameof(IsOpenFolderEnabled));
            CancelOcr();
            _ocrService.ClearCache();
            _mangaManager.Clear();
            _previousPageIndex =0;
            SelectedThumbnailIndex = -1;
            Bookmarks.Clear();
        }

        /// <summary>
        /// 스트리밍으로 다운로드된 파일 키(또는 경로)를 순서에 따라 추가합니다.
        /// </summary>
        public void AddDownloadedFiles(IEnumerable<string> files)
        {
            _mangaManager.AddDownloadedFiles(files);
            UpdateCommandStates();
        }

        /// <summary>
        /// 로컬 파일 집합을 로드합니다(첫 파일의 폴더를 전체 로드). mem: 키로만 주어지면 스트리밍 파이프라인 재사용.
        /// </summary>
        public async Task LoadLocalFilesAsync(IReadOnlyList<string> filePaths)
        {
            IsStreamingGallery = false; // local load unless mem: detected
            OnPropertyChanged(nameof(IsOpenFolderEnabled));
            if (filePaths == null || filePaths.Count ==0) return;

            bool allMem = true;
            for (int i =0; i < filePaths.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(filePaths[i]) || !filePaths[i].StartsWith("mem:", System.StringComparison.OrdinalIgnoreCase)) { allMem = false; break; }
            }
            if (allMem)
            {
                BeginStreamingGallery();
                SetExpectedTotalPages(filePaths.Count);
                AddDownloadedFiles(filePaths);
                return;
            }

            try
            {
                string? folderPath = Path.GetDirectoryName(filePaths[0]);
                if (!string.IsNullOrWhiteSpace(folderPath))
                {
                    _ocrService.ClearCache();
                    await _mangaManager.LoadFolderAsync(folderPath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LoadLocalFiles] {ex.Message}");
            }
        }

        #region Folder/Page Loading
        /// <summary>
        /// 폴더 선택 대화상자를 열어 이미지를 로드합니다.
        /// </summary>
        private async Task OpenFolderAsync(object? windowHandle)
        {
            if (IsLoading || windowHandle is null) return;
            IsStreamingGallery = false;
            OnPropertyChanged(nameof(IsOpenFolderEnabled));
            IsLoading = true;
            CancelOcr();
            try
            {
                // Windows App SDK picker with WindowId (no InitializeWithWindow interop)
                var hWnd = (IntPtr)windowHandle;
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
                var picker = new FolderPicker(windowId);
                var folder = await picker.PickSingleFolderAsync();
                if (folder != null)
                {
                    _ocrService.ClearCache();
                    await _mangaManager.LoadFolderAsync(folder.Path); // path-based 로드 사용
                }
            }
            catch (Exception ex) { Log.Error(ex, "[Folder] Error"); }
            finally { IsLoading = false; }
        }

        private void OnMangaLoaded()
        {
            _previousPageIndex =0;
            OnPropertyChanged(nameof(Thumbnails));
            UpdateCommandStates();
            try
            {
                _bookmarkService.LoadForFolder(_mangaManager.CurrentFolderPath);
                RebuildBookmarksFromStore();
            }
            catch { }
        }

        private void RebuildBookmarksFromStore()
        {
            Bookmarks.Clear();
            var list = _bookmarkService.GetAll();
            foreach (var p in list)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                bool exists = false;
                foreach (var b in Bookmarks)
                {
                    if (string.Equals(b.FilePath, p, System.StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                }
                if (!exists)
                {
                    Bookmarks.Add(new MangaPageViewModel { FilePath = p });
                }
            }
        }

        /// <summary>
        /// 페이지 인덱스 변경 시 표시 이미지/선택/프리페치를 업데이트합니다.
        /// </summary>
        private void OnPageChanged()
        {
            int newIndex = _mangaManager.CurrentPageIndex;
            int delta = newIndex - _previousPageIndex;
            CancelOcr();
            var paths = _mangaManager.GetImagePathsForCurrentPage();
            string? leftPath = paths.Count >0 ? paths[0] : null;
            string? rightPath = paths.Count >1 ? paths[1] : null;
            if (_mangaManager.IsRightToLeft && paths.Count ==2)
                (leftPath, rightPath) = (rightPath, leftPath);

            LeftImageFilePath = leftPath;
            RightImageFilePath = rightPath;
            OnPropertyChanged(nameof(LeftImageFilePath));
            OnPropertyChanged(nameof(RightImageFilePath));

            LeftImageSource = !string.IsNullOrEmpty(leftPath) ? _imageCache.Get(leftPath) : null;
            RightImageSource = !string.IsNullOrEmpty(rightPath) ? _imageCache.Get(rightPath) : null;

            IsSinglePageMode = (LeftImageSource != null) ^ (RightImageSource != null);
            IsTwoPageMode = LeftImageSource != null && RightImageSource != null;

            int primaryImageIndex = _mangaManager.GetPrimaryImageIndexForPage(_mangaManager.CurrentPageIndex);
            if (_selectedThumbnailIndex != primaryImageIndex)
                SelectedThumbnailIndex = primaryImageIndex;

            OnPropertyChanged(nameof(DirectionButtonText));
            OnPropertyChanged(nameof(CoverButtonText));

            TryPrefetchAhead();
            RunOcrCommand.RaiseCanExecuteChanged();
            ClearOcr();

            if (delta !=0)
                PageSlideRequested?.Invoke(this, delta);

            _previousPageIndex = newIndex;
            PageViewChanged?.Invoke(this, EventArgs.Empty);
        }
        #endregion

        private void TryPrefetchAhead()
        {
            try
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i =1; i <=2; i++)
                {
                    int idx = _mangaManager.CurrentPageIndex + i;
                    if (idx >= _mangaManager.TotalPages) break;
                    foreach (var p in _mangaManager.GetImagePathsForPage(idx))
                        if (!string.IsNullOrEmpty(p)) set.Add(p);
                }
                if (set.Count >0) _imageCache.Prefetch(set);
            }
            catch (Exception ex) { Log.Error(ex, "[Prefetch] Error"); }
        }

        private void NavigateLogicalLeft()
        {
            if (_mangaManager.IsRightToLeft) _mangaManager.GoToNextPage(); else _mangaManager.GoToPreviousPage();
        }
        private void NavigateLogicalRight()
        {
            if (_mangaManager.IsRightToLeft) _mangaManager.GoToPreviousPage(); else _mangaManager.GoToNextPage();
        }

        private void UpdateCommandStates()
        {
            OpenFolderCommand.RaiseCanExecuteChanged();
            NextPageCommand.RaiseCanExecuteChanged();
            PrevPageCommand.RaiseCanExecuteChanged();
            ToggleDirectionCommand.RaiseCanExecuteChanged();
            ToggleCoverCommand.RaiseCanExecuteChanged();
            TogglePaneCommand.RaiseCanExecuteChanged();
            ToggleBookmarkPaneCommand.RaiseCanExecuteChanged();
            GoLeftCommand.RaiseCanExecuteChanged();
            GoRightCommand.RaiseCanExecuteChanged();
            RunOcrCommand.RaiseCanExecuteChanged();
            AddBookmarkCommand.RaiseCanExecuteChanged();
            RemoveBookmarkCommand.RaiseCanExecuteChanged();
        }

        public void UpdateLeftOcrContainerSize(double w, double h)
        {
            if (w <=0 || h <=0) return;
            if (Math.Abs(w - _leftWrapperWidth) > .5 || Math.Abs(h - _leftWrapperHeight) > .5)
            {
                _leftWrapperWidth = w; _leftWrapperHeight = h;
            }
        }
        public void UpdateRightOcrContainerSize(double w, double h)
        {
            if (w <=0 || h <=0) return;
            if (Math.Abs(w - _rightWrapperWidth) > .5 || Math.Abs(h - _rightWrapperHeight) > .5)
            {
                _rightWrapperWidth = w; _rightWrapperHeight = h;
            }
        }

        private void SetOcrStatus(string message, InfoBarSeverity severity = InfoBarSeverity.Informational, bool open = true)
        {
            OcrStatusMessage = message;
            OcrSeverity = severity;
            IsInfoBarOpen = open;
        }

        private void AddCurrentBookmark()
        {
            if (_mangaManager.TotalImages <=0) return;
            int imageIndex = _mangaManager.GetPrimaryImageIndexForPage(_mangaManager.CurrentPageIndex);
            if (imageIndex <0) return;
            if (imageIndex >= Thumbnails.Count) return;
            var path = Thumbnails[imageIndex].FilePath;
            if (string.IsNullOrWhiteSpace(path)) return;
            if (_bookmarkService.Add(path))
            {
                foreach (var b in Bookmarks)
                    if (string.Equals(b.FilePath, path, StringComparison.OrdinalIgnoreCase)) return;
                Bookmarks.Add(new MangaPageViewModel { FilePath = path });
            }
        }

        private void RemoveBookmark(MangaPageViewModel? vm)
        {
            if (vm?.FilePath == null) return;
            if (_bookmarkService.Remove(vm.FilePath))
            {
                Bookmarks.Remove(vm);
            }
        }

        private void NavigateToBookmark(MangaPageViewModel? vm)
        {
            if (vm?.FilePath == null) return;
            int idx = _mangaManager.FindImageIndexByPath(vm.FilePath);
            if (idx >=0)
            {
                SelectedThumbnailIndex = idx;
            }
        }

        private async Task RunOcrAsync()
        {
            if (IsOcrRunning) return;
            CancelOcr();
            var originalPaths = _mangaManager.GetImagePathsForCurrentPage();
            if (originalPaths.Count ==0) return;

            var paths = new List<string>(originalPaths);
            if (_mangaManager.IsRightToLeft && paths.Count ==2)
            {
                (paths[0], paths[1]) = (paths[1], paths[0]);
            }

            IsOcrRunning = true;
            OnPropertyChanged(nameof(IsControlEnabled));
            _ocrCts = new CancellationTokenSource();
            var token = _ocrCts.Token;
            try
            {
                ClearOcr();
                SetOcrStatus($"OCR 실행 중... ({paths.Count} images)", InfoBarSeverity.Informational, true);
                int totalBoxes =0;
                for (int i =0; i < paths.Count; i++)
                {
                    string p = paths[i];
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    token.ThrowIfCancellationRequested();
                    var boxes = await _ocrService.GetOcrAsync(p, token);
                    foreach (var b in boxes)
                        if (i ==0) _leftOcrBoxes.Add(b); else _rightOcrBoxes.Add(b);
                    totalBoxes += boxes.Count;
                }
                SetOcrStatus($"OCR 완료: {totalBoxes} boxes", InfoBarSeverity.Success, true);
            }
            catch (OperationCanceledException)
            {
                SetOcrStatus("OCR 취소됨", InfoBarSeverity.Informational, true);
                ClearOcr();
            }
            catch (Exception ex)
            {
                SetOcrStatus("OCR 오류: " + ex.Message, InfoBarSeverity.Error, true);
                Log.Error(ex, "RunOcrAsync failed");
            }
            finally
            {
                IsOcrRunning = false;
                OnPropertyChanged(nameof(IsControlEnabled));
                OcrCompleted?.Invoke(this, EventArgs.Empty);
                RunOcrCommand.RaiseCanExecuteChanged();
                _ocrCts?.Dispose();
                _ocrCts = null;
            }
        }

        private void CancelOcr()
        {
            if (_ocrCts != null && !_ocrCts.IsCancellationRequested)
            {
                _ocrCts.Cancel();
                SetOcrStatus("OCR 취소 요청...", InfoBarSeverity.Informational, true);
            }
        }

        private void ClearOcr()
        {
            _leftOcrBoxes.Clear();
            _rightOcrBoxes.Clear();
        }

        public void CreatePlaceholderPages(int count) => _mangaManager.CreatePlaceholders(count);
        public void ReplacePlaceholderWithFile(int index, string path)
        {
            if (index <0) return;
            _mangaManager.ReplaceFileAtIndex(index, path);
        }
        public void SetExpectedTotalPages(int total) => _mangaManager.SetExpectedTotal(total);

        public void Dispose()
        {
            CancelOcr();
            _ocrCts?.Dispose();
            _ocrCts = null;
            _mangaManager.MangaLoaded -= OnMangaLoaded;
            _mangaManager.PageChanged -= OnPageChanged;
            _ocrService.SettingsChanged -= OnOcrSettingsChanged;
            GC.SuppressFinalize(this);
        }
    }
}