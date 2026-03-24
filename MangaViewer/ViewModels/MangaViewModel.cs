using MangaViewer.Helpers;
using MangaViewer.Services;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Microsoft.Windows.Storage.Pickers;
using System.Threading;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using MangaViewer.Services.Thumbnails;
using MangaViewer.Services.Logging;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MangaViewer.ViewModels
{
    public partial class MangaViewModel : BaseViewModel, IDisposable
    {
        private readonly MangaManager _mangaManager = new();
        private readonly ImageCacheService _imageCache = ImageCacheService.Instance;
        private readonly OcrService _ocrService = OcrService.Instance;
        private readonly BookmarkService _bookmarkService = BookmarkService.Instance;
        private readonly LibraryService _libraryService = new();

        private BitmapImage? _leftImageSource;
        private BitmapImage? _rightImageSource;
        private int _selectedThumbnailIndex = -1;
        private bool _isPaneOpen;
        private bool _isBookmarkPaneOpen;
        private bool _isNavOpen;
        private bool _isLoading;
        private bool _isSinglePageMode;
        private bool _isTwoPageMode;
        private bool _isOcrRunning;
        private int _previousPageIndex;
        private bool _isStreamingGallery;

        private double _leftWrapperWidth, _leftWrapperHeight;
        private double _rightWrapperWidth, _rightWrapperHeight;
        private double _singleWrapperWidth, _singleWrapperHeight;
        private double _rasterizationScale = 1.0;

        private CancellationTokenSource? _ocrCts;
        private int _ocrVersion;
        private CancellationTokenSource? _pageLoadCts;
        private int _pageLoadVersion;

        private static readonly ConcurrentDictionary<string, string> _translationCache = new(StringComparer.Ordinal);
        private readonly Dictionary<int, bool> _pageOcrStates = new();
        private readonly Dictionary<int, bool> _pageTranslationStates = new();
        private static readonly TimeSpan TranslationPerSideTimeout = TimeSpan.FromSeconds(120);
        private CancellationTokenSource? _adjacentPrefetchCts;
        private bool _forceRefreshOllamaOcrOnNextRun;

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
        public bool IsOcrRunning
        {
            get => _isOcrRunning;
            private set
            {
                if (SetProperty(ref _isOcrRunning, value))
                {
                    OnPropertyChanged(nameof(IsControlEnabled));
                }
            }
        }
        private bool _isOcrActive;
        public bool IsOcrActive
        {
            get => _isOcrActive;
            set
            {
                if (SetProperty(ref _isOcrActive, value))
                {
                    int pageIndex = _mangaManager.CurrentPageIndex;
                    if (pageIndex >= 0)
                        _pageOcrStates[pageIndex] = value;
                    if (_isOcrActive)
                    {
                        _ = RunOcrAsync();
                    }
                    else
                    {
                        _forceRefreshOllamaOcrOnNextRun = _ocrService.Backend == OcrService.OcrBackend.Vlm && IsOllamaOcrTextVisible;
                        CancelOcr();
                        ClearOcr();
                    }
                    OnPropertyChanged(nameof(IsTranslationToggleEnabled));
                }
            }
        }

        private bool _isTranslationActive;
        public bool IsTranslationActive
        {
            get => _isTranslationActive;
            set
            {
                if (SetProperty(ref _isTranslationActive, value))
                {
                    int pageIndex = _mangaManager.CurrentPageIndex;
                    if (pageIndex >= 0)
                        _pageTranslationStates[pageIndex] = value;
                    OnPropertyChanged(nameof(IsTranslationVisible));
                    if (_isTranslationActive && _isOcrActive)
                    {
                        _ = RunTranslationAsync();
                    }
                    else
                    {
                        TranslatedLeftOcrText = string.Empty;
                        TranslatedRightOcrText = string.Empty;
                        ClearBoxTranslations();
                        PageViewChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }

        public bool IsTranslationToggleEnabled => IsOcrActive;
        public bool IsTranslationVisible => IsTranslationActive && IsOcrActive && (!string.IsNullOrEmpty(TranslatedLeftOcrText) || !string.IsNullOrEmpty(TranslatedRightOcrText));

        private string _translatedLeftOcrText = string.Empty;
        private string _translatedRightOcrText = string.Empty;
        public string TranslatedLeftOcrText { get => _translatedLeftOcrText; private set { SetProperty(ref _translatedLeftOcrText, value); OnPropertyChanged(nameof(IsTranslationVisible)); } }
        public string TranslatedRightOcrText { get => _translatedRightOcrText; private set { SetProperty(ref _translatedRightOcrText, value); OnPropertyChanged(nameof(IsTranslationVisible)); } }

        public string OcrStatusMessage { get => _ocrStatusMessage; private set => SetProperty(ref _ocrStatusMessage, value); }
        public bool IsInfoBarOpen { get => _isInfoBarOpen; private set => SetProperty(ref _isInfoBarOpen, value); }
        public InfoBarSeverity OcrSeverity { get => _ocrSeverity; private set => SetProperty(ref _ocrSeverity, value); }
        public bool IsStreamingGallery
        {
            get => _isStreamingGallery;
            private set
            {
                if (SetProperty(ref _isStreamingGallery, value))
                    OnPropertyChanged(nameof(IsOpenFolderEnabled));
            }
        }
        public bool IsOpenFolderEnabled => IsControlEnabled;

        public int SelectedThumbnailIndex
        {
            get => _selectedThumbnailIndex;
            set
            {
                if (SetProperty(ref _selectedThumbnailIndex, value))
                {
                    ThumbnailDecodeScheduler.Instance.UpdateSelectedIndex(_selectedThumbnailIndex);
                    if (value >= 0)
                        _mangaManager.SetCurrentPageFromImageIndex(value);
                }
            }
        }

        public bool IsPaneOpen { get => _isPaneOpen; set => SetProperty(ref _isPaneOpen, value); }
        public bool IsBookmarkPaneOpen { get => _isBookmarkPaneOpen; set => SetProperty(ref _isBookmarkPaneOpen, value); }
        public bool IsNavOpen { get => _isNavOpen; set => SetProperty(ref _isNavOpen, value); }
        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (SetProperty(ref _isLoading, value))
                {
                    OnPropertyChanged(nameof(IsControlEnabled));
                    OnPropertyChanged(nameof(IsOcrToggleEnabled));
                }
            }
        }
        public bool IsControlEnabled => !IsLoading && !IsOcrRunning;
        public bool IsOcrToggleEnabled => !IsLoading && _mangaManager.TotalImages > 0;

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
        public RelayCommand AddBookmarkCommand { get; }
        public RelayCommand RemoveBookmarkCommand { get; }
        public RelayCommand NavigateToBookmarkCommand { get; }

        private readonly ObservableCollection<BoundingBoxViewModel> _leftOcrBoxes = new();
        private readonly ObservableCollection<BoundingBoxViewModel> _rightOcrBoxes = new();
        public ReadOnlyObservableCollection<BoundingBoxViewModel> LeftOcrBoxes { get; }
        public ReadOnlyObservableCollection<BoundingBoxViewModel> RightOcrBoxes { get; }

        private sealed class IndexedBox
        {
            public required BoundingBoxViewModel Box { get; init; }
            public required int Index { get; init; }
        }

        private sealed class MergedIndexedBox
        {
            public required int Index { get; init; }
            public required IReadOnlyList<IndexedBox> Members { get; init; }
            public required string Text { get; init; }
        }

        private string _leftOcrText = string.Empty;
        private string _rightOcrText = string.Empty;
        public string LeftOcrText { get => _leftOcrText; private set => SetProperty(ref _leftOcrText, value); }
        public string RightOcrText { get => _rightOcrText; private set => SetProperty(ref _rightOcrText, value); }
        public bool IsOllamaMode => _ocrService.Backend == OcrService.OcrBackend.Vlm;
        public bool IsOllamaOcrTextVisible => IsOllamaMode
            && _leftOcrBoxes.Count == 0
            && _rightOcrBoxes.Count == 0
            && (!string.IsNullOrEmpty(_leftOcrText) || !string.IsNullOrEmpty(_rightOcrText));

        private BoundingBoxViewModel? _selectedOcrBox;
        public BoundingBoxViewModel? SelectedOcrBox { get => _selectedOcrBox; set => SetProperty(ref _selectedOcrBox, value); }

        public event EventHandler? OcrCompleted;
        public event EventHandler? PageViewChanged;
        public event EventHandler<int>? PageSlideRequested;
        public event EventHandler? MangaFolderLoaded;

        public LibraryViewModel LibraryViewModel { get; }

        public MangaViewModel()
        {
            LibraryViewModel = new LibraryViewModel(_libraryService);

            LeftOcrBoxes = new ReadOnlyObservableCollection<BoundingBoxViewModel>(_leftOcrBoxes);
            RightOcrBoxes = new ReadOnlyObservableCollection<BoundingBoxViewModel>(_rightOcrBoxes);

            _mangaManager.MangaLoaded += OnMangaLoaded;
            _mangaManager.PageChanged += OnPageChanged;
            _ocrService.SettingsChanged += OnOcrSettingsChanged;
            TranslationSettingsService.Instance.SettingsChanged += OnTranslationSettingsChanged;

            OpenFolderCommand = new AsyncRelayCommand(async p => await OpenFolderAsync(p), _ => IsOpenFolderEnabled);
            NextPageCommand = new RelayCommand(_ => _mangaManager.GoToNextPage(), _ => _mangaManager.TotalImages > 0);
            PrevPageCommand = new RelayCommand(_ => _mangaManager.GoToPreviousPage(), _ => _mangaManager.TotalImages > 0);
            ToggleDirectionCommand = new RelayCommand(_ => { _mangaManager.ToggleDirection(); CancelOcr(); }, _ => _mangaManager.TotalImages > 0);
            ToggleCoverCommand = new RelayCommand(_ => { _mangaManager.ToggleCover(); CancelOcr(); }, _ => _mangaManager.TotalImages > 0);
            TogglePaneCommand = new RelayCommand(_ => IsPaneOpen = !IsPaneOpen);
            ToggleBookmarkPaneCommand = new RelayCommand(_ => IsBookmarkPaneOpen = !IsBookmarkPaneOpen);
            ToggleNavPaneCommand = new RelayCommand(_ => IsNavOpen = !IsNavOpen);
            GoLeftCommand = new RelayCommand(_ => NavigateLogicalLeft(), _ => _mangaManager.TotalImages > 0);
            GoRightCommand = new RelayCommand(_ => NavigateLogicalRight(), _ => _mangaManager.TotalImages > 0);
            AddBookmarkCommand = new RelayCommand(_ => AddCurrentBookmark(), _ => _mangaManager.TotalImages > 0);
            RemoveBookmarkCommand = new RelayCommand(o => RemoveBookmark(o as MangaPageViewModel), _ => Bookmarks.Count > 0);
            NavigateToBookmarkCommand = new RelayCommand(o => NavigateToBookmark(o as MangaPageViewModel));
        }

        private async void OnOcrSettingsChanged(object? sender, EventArgs e)
        {
            OnPropertyChanged(nameof(IsOllamaMode));
            RaiseOllamaOcrTextVisibilityChanged();
            if (IsOcrRunning) return;
            if (LeftImageFilePath == null && RightImageFilePath == null) return;
            if (!IsOcrActive) return;
            await RunOcrAsync();
        }

        private void RaiseOllamaOcrTextVisibilityChanged() => OnPropertyChanged(nameof(IsOllamaOcrTextVisible));

        private void OnTranslationSettingsChanged(object? sender, EventArgs e)
        {
            _translationCache.Clear();
            PageViewChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 스트리밍 갤러리 모드를 시작합니다(플레이스홀더/상태 초기화).
        /// </summary>
        public void BeginStreamingGallery()
        {
            IsStreamingGallery = true;
            CancelOcr();
            _ocrService.ClearCache();
            _mangaManager.Clear();

            _pageOcrStates.Clear();
            _pageTranslationStates.Clear();
            ClearCurrentImages();

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
            if (filePaths == null || filePaths.Count == 0) return;

            bool allMem = true;
            for (int i = 0; i < filePaths.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(filePaths[i]) || !filePaths[i].StartsWith("mem:", StringComparison.OrdinalIgnoreCase)) { allMem = false; break; }
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
                    await LoadFolderCoreAsync(folderPath, navigateToReader: false).ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[LoadLocalFiles] Error");
            }
        }

        #region Folder/Page Loading
        /// <summary>
        /// 폴더 선택 대화상자를 열어 이미지를 로드합니다.
        /// Uses Windows App SDK 1.8+ FolderPicker with WindowId (no InitializeWithWindow interop needed).
        /// </summary>
        private async Task OpenFolderAsync(object? windowHandle)
        {
            if (IsLoading || windowHandle is null) return;
            IsStreamingGallery = false;
            IsLoading = true;
            BeginPageTransition();

            try
            {
                // Windows App SDK 1.8+ picker: uses WindowId directly, no InitializeWithWindow needed
                var hWnd = (IntPtr)windowHandle;
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
                var picker = new FolderPicker(windowId);
                var folder = await picker.PickSingleFolderAsync();
                if (folder != null)
                {
                    await LoadFolderCoreAsync(folder.Path, navigateToReader: true).ConfigureAwait(true);
                }
            }
            catch (Exception ex) { Log.Error(ex, "[Folder] Error"); }
            finally { IsLoading = false; }
        }

        /// <summary>
        /// 라이브러리에서 특정 만화 폴더를 로드합니다.
        /// </summary>
        public async Task LoadMangaFolderAsync(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return;
            IsStreamingGallery = false;
            IsLoading = true;
            BeginPageTransition();

            try
            {
                await LoadFolderCoreAsync(folderPath, navigateToReader: true).ConfigureAwait(true);
            }
            catch (Exception ex) { Log.Error(ex, "[LoadMangaFolder] Error"); }
            finally { IsLoading = false; }
        }

        private async Task LoadFolderCoreAsync(string folderPath, bool navigateToReader)
        {
            _ocrService.ClearCache();
            await _mangaManager.LoadFolderAsync(folderPath).ConfigureAwait(true);

            if (navigateToReader)
                MainWindow.TryNavigate(typeof(Pages.MangaReaderPage), this);
        }
        #endregion

        private void BeginPageTransition()
        {
            CancelOcr();
            ClearCurrentImages();
        }

        private void ClearCurrentImages()
        {
            LeftImageSource = null;
            RightImageSource = null;
            LeftImageFilePath = null;
            RightImageFilePath = null;
            OnPropertyChanged(nameof(LeftImageFilePath));
            OnPropertyChanged(nameof(RightImageFilePath));
            IsSinglePageMode = false;
            IsTwoPageMode = false;
            _previousPageIndex = 0;
            SelectedThumbnailIndex = -1;
        }

        private void OnMangaLoaded()
        {
            _previousPageIndex = 0;
            _pageOcrStates.Clear();
            _pageTranslationStates.Clear();
            OnPropertyChanged(nameof(Thumbnails));
            UpdateCommandStates();

            try
            {
                _bookmarkService.LoadForFolder(_mangaManager.CurrentFolderPath);
                RebuildBookmarksFromStore();
            }
            catch { }

            MangaFolderLoaded?.Invoke(this, EventArgs.Empty);
            // Trigger initial page load
            OnPageChanged();
        }

        private void RebuildBookmarksFromStore()
        {
            Bookmarks.Clear();

            var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in _bookmarkService.GetAll())
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (!distinct.Add(p)) continue;
                Bookmarks.Add(new MangaPageViewModel { FilePath = p });
            }
        }

        private async void OnPageChanged()
        {
            try
            {
                await OnPageChangedCoreAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[OnPageChanged] Error");
            }
        }

        private async Task OnPageChangedCoreAsync()
        {
            int newIndex = _mangaManager.CurrentPageIndex;
            int delta = newIndex - _previousPageIndex;

            CancelOcr();

            var (token, localVersion) = ResetPageLoadCancellation();

            var (leftPath, rightPath) = GetCurrentLeftRightPaths();
            ApplyCurrentPaths(leftPath, rightPath);
            ResetImageSources();
            UpdatePageMode(leftPath, rightPath);

            try
            {
                await LoadPageImagesAsync(leftPath, rightPath, token, localVersion).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested || localVersion != _pageLoadVersion) return;

            ScheduleViewportRefreshIfNeeded();
            UpdateSelectedThumbnail();

            OnPropertyChanged(nameof(DirectionButtonText));
            OnPropertyChanged(nameof(CoverButtonText));

            TryPrefetchAround();
            ClearOcr();

            if (delta != 0)
                PageSlideRequested?.Invoke(this, delta);

            _previousPageIndex = newIndex;
            PageViewChanged?.Invoke(this, EventArgs.Empty);

            RestorePageOcrTranslationState(newIndex);

            if (IsOcrActive)
            {
                _ = RunOcrAsync();
            }
        }

        private (CancellationToken token, int localVersion) ResetPageLoadCancellation()
        {
            _pageLoadCts?.Cancel();
            _pageLoadCts?.Dispose();
            _pageLoadCts = new CancellationTokenSource();
            var token = _pageLoadCts.Token;
            int localVersion = Interlocked.Increment(ref _pageLoadVersion);
            return (token, localVersion);
        }

        private (string? leftPath, string? rightPath) GetCurrentLeftRightPaths()
        {
            var paths = _mangaManager.GetImagePathsForCurrentPage();

            string? leftPath = paths.Count > 0 ? paths[0] : null;
            string? rightPath = paths.Count > 1 ? paths[1] : null;
            if (_mangaManager.IsRightToLeft && paths.Count == 2)
                (leftPath, rightPath) = (rightPath, leftPath);

            return (leftPath, rightPath);
        }

        private void ApplyCurrentPaths(string? leftPath, string? rightPath)
        {
            LeftImageFilePath = leftPath;
            RightImageFilePath = rightPath;
            OnPropertyChanged(nameof(LeftImageFilePath));
            OnPropertyChanged(nameof(RightImageFilePath));
        }

        private void ResetImageSources()
        {
            LeftImageSource = null;
            RightImageSource = null;
        }

        private void UpdatePageMode(string? leftPath, string? rightPath)
        {
            IsSinglePageMode = (!string.IsNullOrEmpty(leftPath)) ^ (!string.IsNullOrEmpty(rightPath));
            IsTwoPageMode = !string.IsNullOrEmpty(leftPath) && !string.IsNullOrEmpty(rightPath);
        }

        private async Task LoadPageImagesAsync(string? leftPath, string? rightPath, CancellationToken token, int localVersion)
        {
            if (!string.IsNullOrEmpty(leftPath))
            {
                token.ThrowIfCancellationRequested();
                double w = IsTwoPageMode ? _leftWrapperWidth : _singleWrapperWidth;
                double h = IsTwoPageMode ? _leftWrapperHeight : _singleWrapperHeight;
                var img = await _imageCache.GetForViewportAsync(leftPath, Math.Max(1, w), Math.Max(1, h), _rasterizationScale).ConfigureAwait(true);
                if (!token.IsCancellationRequested && localVersion == _pageLoadVersion && LeftImageFilePath == leftPath)
                    LeftImageSource = img;
            }

            if (!string.IsNullOrEmpty(rightPath))
            {
                token.ThrowIfCancellationRequested();
                var img = await _imageCache.GetForViewportAsync(rightPath, Math.Max(1, _rightWrapperWidth), Math.Max(1, _rightWrapperHeight), _rasterizationScale).ConfigureAwait(true);
                if (!token.IsCancellationRequested && localVersion == _pageLoadVersion && RightImageFilePath == rightPath)
                    RightImageSource = img;
            }
        }

        private void ScheduleViewportRefreshIfNeeded()
        {
            if (!IsSinglePageMode) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(60).ConfigureAwait(false);
                    await RefreshCurrentPageImagesForViewportAsync().ConfigureAwait(false);
                }
                catch { }
            });
        }

        private void UpdateSelectedThumbnail()
        {
            int primaryImageIndex = _mangaManager.GetPrimaryImageIndexForPage(_mangaManager.CurrentPageIndex);
            if (_selectedThumbnailIndex != primaryImageIndex)
                SelectedThumbnailIndex = primaryImageIndex;
        }

        private void TryPrefetchAround()
        {
            try
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i <= 2; i++)
                {
                    int idxFwd = _mangaManager.CurrentPageIndex + i;
                    if (idxFwd < _mangaManager.TotalPages)
                    {
                        foreach (var p in _mangaManager.GetImagePathsForPage(idxFwd))
                            if (!string.IsNullOrEmpty(p)) set.Add(p);
                    }

                    int idxBack = _mangaManager.CurrentPageIndex - i;
                    if (idxBack >= 0)
                    {
                        foreach (var p in _mangaManager.GetImagePathsForPage(idxBack))
                            if (!string.IsNullOrEmpty(p)) set.Add(p);
                    }
                }

                if (set.Count > 0)
                    _imageCache.Prefetch(set);
            }
            catch (Exception ex) { Log.Error(ex, "[Prefetch] Error"); }
        }

        private void NavigateLogicalLeft()
        {
            if (_mangaManager.IsRightToLeft) _mangaManager.GoToNextPage();
            else _mangaManager.GoToPreviousPage();
        }

        private void NavigateLogicalRight()
        {
            if (_mangaManager.IsRightToLeft) _mangaManager.GoToPreviousPage();
            else _mangaManager.GoToNextPage();
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
            AddBookmarkCommand.RaiseCanExecuteChanged();
            RemoveBookmarkCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsOcrToggleEnabled));
        }

        public void UpdateRasterizationScale(double scale)
        {
            if (scale <= 0) scale = 1.0;
            if (Math.Abs(_rasterizationScale - scale) > 0.0001)
                _rasterizationScale = scale;
        }

        public void UpdateSingleOcrContainerSize(double w, double h)
        {
            if (w <= 0 || h <= 0) return;
            if (Math.Abs(w - _singleWrapperWidth) > .5 || Math.Abs(h - _singleWrapperHeight) > .5)
            {
                _singleWrapperWidth = w;
                _singleWrapperHeight = h;
            }
        }

        public void UpdateLeftOcrContainerSize(double w, double h)
        {
            if (w <= 0 || h <= 0) return;
            if (Math.Abs(w - _leftWrapperWidth) > .5 || Math.Abs(h - _leftWrapperHeight) > .5)
            {
                _leftWrapperWidth = w; _leftWrapperHeight = h;
            }
        }
        public void UpdateRightOcrContainerSize(double w, double h)
        {
            if (w <= 0 || h <= 0) return;
            if (Math.Abs(w - _rightWrapperWidth) > .5 || Math.Abs(h - _rightWrapperHeight) > .5)
            {
                _rightWrapperWidth = w; _rightWrapperHeight = h;
            }
        }

        /// <summary>
        /// Re-decode currently displayed images to match the latest viewport size.
        /// OCR input remains unchanged (uses original file path).
        /// </summary>
        public async Task RefreshCurrentPageImagesForViewportAsync()
        {
            if (LeftImageFilePath == null && RightImageFilePath == null) return;
            if (_pageLoadCts == null) return;

            var token = _pageLoadCts.Token;
            int localVersion = _pageLoadVersion;

            try
            {
                if (!string.IsNullOrEmpty(LeftImageFilePath))
                {
                    double w = IsTwoPageMode ? _leftWrapperWidth : _singleWrapperWidth;
                    double h = IsTwoPageMode ? _leftWrapperHeight : _singleWrapperHeight;
                    if (w > 0 && h > 0)
                    {
                        var img = await _imageCache.GetForViewportAsync(LeftImageFilePath, w, h, _rasterizationScale).ConfigureAwait(true);
                        if (!token.IsCancellationRequested && localVersion == _pageLoadVersion && LeftImageFilePath != null)
                            LeftImageSource = img;
                    }
                }

                if (!string.IsNullOrEmpty(RightImageFilePath))
                {
                    if (_rightWrapperWidth > 0 && _rightWrapperHeight > 0)
                    {
                        var img = await _imageCache.GetForViewportAsync(RightImageFilePath, _rightWrapperWidth, _rightWrapperHeight, _rasterizationScale).ConfigureAwait(true);
                        if (!token.IsCancellationRequested && localVersion == _pageLoadVersion && RightImageFilePath != null)
                            RightImageSource = img;
                    }
                }
            }
            catch { }
        }

        private async Task RunOcrAsync()
        {
            CancelOcr();
            int localVersion = Interlocked.Increment(ref _ocrVersion);
            while (IsOcrRunning)
            {
                await Task.Delay(10);
                if (localVersion != _ocrVersion) return;
            }
            if (localVersion != _ocrVersion) return;

            var originalPaths = _mangaManager.GetImagePathsForCurrentPage();
            if (originalPaths.Count == 0) return;

            var paths = new List<string>(originalPaths);
            if (_mangaManager.IsRightToLeft && paths.Count == 2)
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
                bool forceRefresh = _forceRefreshOllamaOcrOnNextRun;
                _forceRefreshOllamaOcrOnNextRun = false;
                if (_ocrService.Backend == OcrService.OcrBackend.Hybrid)
                {
                    await RunHybridOcrAsync(paths, token, localVersion, forceRefresh);
                }
                else
                {
                    await RunOllamaOcrAsync(paths, token, localVersion, forceRefresh);
                }

                await EnsureVisiblePageOcrCachedAsync(paths, token, localVersion, forceRefresh).ConfigureAwait(true);

                StartAdjacentPagePrefetch();
                if (IsTranslationActive)
                    _ = RunTranslationAsync();
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
                _ocrCts?.Dispose();
                _ocrCts = null;
            }
        }

        private async Task RunWinRtOcrAsync(List<string> paths, CancellationToken token, int localVersion)
        {
            SetOcrStatus($"OCR 실행 중... ({paths.Count} images)", InfoBarSeverity.Informational, true);
            int totalBoxes = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                string p = paths[i];
                if (string.IsNullOrWhiteSpace(p)) continue;
                ThrowIfOcrRunStale(localVersion, token);
                var boxes = await _ocrService.GetOcrAsync(p, token);
                ThrowIfOcrRunStale(localVersion, token);
                foreach (var b in boxes)
                    if (i == 0) _leftOcrBoxes.Add(b); else _rightOcrBoxes.Add(b);
                totalBoxes += boxes.Count;
            }
            ThrowIfOcrRunStale(localVersion, token);
            SetOcrStatus($"OCR 완료: {totalBoxes} boxes", InfoBarSeverity.Success, true);
        }

        private async Task RunOllamaOcrAsync(List<string> paths, CancellationToken token, int localVersion, bool forceRefresh)
        {
            SetOcrStatus("Ollama OCR 실행 중...", InfoBarSeverity.Informational, true);
            int totalBoxes = 0;

            var pending = new List<(int PageIndex, Task<OcrService.OllamaOcrResponse> Task)>();
            if (paths.Count > 0 && !string.IsNullOrWhiteSpace(paths[0]))
                pending.Add((0, _ocrService.GetOllamaOcrAsync(paths[0], token, forceRefresh)));
            if (paths.Count > 1 && !string.IsNullOrWhiteSpace(paths[1]))
                pending.Add((1, _ocrService.GetOllamaOcrAsync(paths[1], token, forceRefresh)));

            int totalPages = pending.Count;
            int completedPages = 0;

            while (pending.Count > 0)
            {
                ThrowIfOcrRunStale(localVersion, token);

                var completedTask = await Task.WhenAny(pending.Select(x => x.Task)).ConfigureAwait(true);
                int pendingIndex = pending.FindIndex(x => ReferenceEquals(x.Task, completedTask));
                if (pendingIndex < 0)
                    continue;

                var (pageIndex, task) = pending[pendingIndex];
                pending.RemoveAt(pendingIndex);

                var result = await task.ConfigureAwait(true);
                ThrowIfOcrRunStale(localVersion, token);
                if (pageIndex == 0)
                {
                    LeftOcrText = result.Text;
                    foreach (var box in result.Boxes)
                        _leftOcrBoxes.Add(box);
                }
                else
                {
                    RightOcrText = result.Text;
                    foreach (var box in result.Boxes)
                        _rightOcrBoxes.Add(box);
                }

                totalBoxes += result.Boxes.Count;
                completedPages++;

                RaiseOllamaOcrTextVisibilityChanged();
                PageViewChanged?.Invoke(this, EventArgs.Empty);

                if (completedPages < totalPages)
                    SetOcrStatus($"Ollama OCR 실행 중... ({completedPages}/{totalPages})", InfoBarSeverity.Informational, true);
            }

            ThrowIfOcrRunStale(localVersion, token);
            bool hasPlainTextOnlyFallback = (_leftOcrBoxes.Count == 0 && !string.IsNullOrWhiteSpace(LeftOcrText))
                || (_rightOcrBoxes.Count == 0 && !string.IsNullOrWhiteSpace(RightOcrText));
            _forceRefreshOllamaOcrOnNextRun = hasPlainTextOnlyFallback;
            RaiseOllamaOcrTextVisibilityChanged();
            SetOcrStatus($"Ollama OCR 완료: {totalBoxes} boxes", InfoBarSeverity.Success, true);
        }

        private async Task RunHybridOcrAsync(List<string> paths, CancellationToken token, int localVersion, bool forceRefresh)
        {
            if (!_ocrService.IsDocLayoutModelInstalled())
            {
                SetOcrStatus("Hybrid OCR 모델이 설치되지 않았습니다. 설정 > OCR에서 'Download PP-DocLayoutV3 model' 버튼으로 설치하세요.", InfoBarSeverity.Warning, true);
                return;
            }

            SetOcrStatus("Hybrid OCR 실행 중...", InfoBarSeverity.Informational, false);
            int totalBoxes = 0;

            var syncContext = SynchronizationContext.Current;
            var previewPublished = new bool[2];

            void PublishPreview(int pageIndex, IReadOnlyList<BoundingBoxViewModel> boxes)
            {
                if (boxes.Count == 0) return;

                void Apply()
                {
                    if (token.IsCancellationRequested || localVersion != _ocrVersion)
                        return;

                    var target = pageIndex == 0 ? _leftOcrBoxes : _rightOcrBoxes;
                    if (target.Count > 0)
                        return;

                    foreach (var box in boxes)
                        target.Add(box);

                    previewPublished[pageIndex] = true;
                    RaiseOllamaOcrTextVisibilityChanged();
                    PageViewChanged?.Invoke(this, EventArgs.Empty);
                }

                if (syncContext != null)
                    syncContext.Post(_ => Apply(), null);
                else
                    Apply();
            }

            var pending = new List<(int PageIndex, Task<OcrService.OllamaOcrResponse> Task)>();
            if (paths.Count > 0 && !string.IsNullOrWhiteSpace(paths[0]))
                pending.Add((0, _ocrService.GetHybridOcrAsync(paths[0], token, forceRefresh, boxes => PublishPreview(0, boxes))));
            if (paths.Count > 1 && !string.IsNullOrWhiteSpace(paths[1]))
                pending.Add((1, _ocrService.GetHybridOcrAsync(paths[1], token, forceRefresh, boxes => PublishPreview(1, boxes))));

            int totalPages = pending.Count;
            int completedPages = 0;

            while (pending.Count > 0)
            {
                ThrowIfOcrRunStale(localVersion, token);

                var completedTask = await Task.WhenAny(pending.Select(x => x.Task)).ConfigureAwait(true);
                int pendingIndex = pending.FindIndex(x => ReferenceEquals(x.Task, completedTask));
                if (pendingIndex < 0)
                    continue;

                var (pageIndex, task) = pending[pendingIndex];
                pending.RemoveAt(pendingIndex);

                var result = await task.ConfigureAwait(true);
                ThrowIfOcrRunStale(localVersion, token);

                if (pageIndex == 0)
                {
                    LeftOcrText = result.Text;
                    if (!previewPublished[0])
                    {
                        foreach (var box in result.Boxes)
                            _leftOcrBoxes.Add(box);
                    }
                }
                else
                {
                    RightOcrText = result.Text;
                    if (!previewPublished[1])
                    {
                        foreach (var box in result.Boxes)
                            _rightOcrBoxes.Add(box);
                    }
                }

                totalBoxes += result.Boxes.Count;
                completedPages++;
                RaiseOllamaOcrTextVisibilityChanged();
                PageViewChanged?.Invoke(this, EventArgs.Empty);

                if (completedPages < totalPages)
                    SetOcrStatus($"Hybrid OCR 실행 중... ({completedPages}/{totalPages})", InfoBarSeverity.Informational, false);
            }

            ThrowIfOcrRunStale(localVersion, token);
            _forceRefreshOllamaOcrOnNextRun = false;
            RaiseOllamaOcrTextVisibilityChanged();
            SetOcrStatus($"Hybrid OCR 완료: {totalBoxes} boxes", InfoBarSeverity.Success, false);
        }

        private void ThrowIfOcrRunStale(int localVersion, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (localVersion != _ocrVersion)
                throw new OperationCanceledException();
        }

        private async Task EnsureVisiblePageOcrCachedAsync(IReadOnlyList<string> visiblePaths, CancellationToken token, int localVersion, bool forceRefresh)
        {
            for (int i = 0; i < visiblePaths.Count; i++)
            {
                string path = visiblePaths[i];
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                if (_ocrService.HasCachedCurrentBackendOcr(path))
                    continue;

                ThrowIfOcrRunStale(localVersion, token);
                var ensured = _ocrService.Backend == OcrService.OcrBackend.Hybrid
                    ? await _ocrService.GetHybridOcrAsync(path, token, forceRefresh).ConfigureAwait(true)
                    : await _ocrService.GetOllamaOcrAsync(path, token, forceRefresh).ConfigureAwait(true);
                ThrowIfOcrRunStale(localVersion, token);

                bool isLeft = i == 0;
                bool hasCurrentSideData = isLeft
                    ? (_leftOcrBoxes.Count > 0 || !string.IsNullOrWhiteSpace(LeftOcrText))
                    : (_rightOcrBoxes.Count > 0 || !string.IsNullOrWhiteSpace(RightOcrText));
                if (hasCurrentSideData)
                    continue;

                if (isLeft)
                {
                    LeftOcrText = ensured.Text;
                    foreach (var box in ensured.Boxes)
                        _leftOcrBoxes.Add(box);
                }
                else
                {
                    RightOcrText = ensured.Text;
                    foreach (var box in ensured.Boxes)
                        _rightOcrBoxes.Add(box);
                }
            }

            RaiseOllamaOcrTextVisibilityChanged();
            PageViewChanged?.Invoke(this, EventArgs.Empty);
        }

        private async Task RunTranslationAsync()
        {
            bool hasBoxOcr = _leftOcrBoxes.Count > 0 || _rightOcrBoxes.Count > 0;
            bool hasPlainTextOcr = !string.IsNullOrWhiteSpace(LeftOcrText) || !string.IsNullOrWhiteSpace(RightOcrText);
            if (!hasBoxOcr && !hasPlainTextOcr) return;

            SetOcrStatus("번역 실행 중...", InfoBarSeverity.Informational, false);

            if (hasBoxOcr)
            {
                foreach (var box in _leftOcrBoxes)
                    box.TranslatedText = string.IsNullOrWhiteSpace(box.Text) ? string.Empty : "번역 중...";
                foreach (var box in _rightOcrBoxes)
                    box.TranslatedText = string.IsNullOrWhiteSpace(box.Text) ? string.Empty : "번역 중...";
                TranslatedLeftOcrText = _leftOcrBoxes.Any(b => !string.IsNullOrWhiteSpace(b.Text)) ? "번역 중..." : string.Empty;
                TranslatedRightOcrText = _rightOcrBoxes.Any(b => !string.IsNullOrWhiteSpace(b.Text)) ? "번역 중..." : string.Empty;
                PageViewChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(LeftOcrText))
                    TranslatedLeftOcrText = "번역 중...";
                if (!string.IsNullOrWhiteSpace(RightOcrText))
                    TranslatedRightOcrText = "번역 중...";
            }

            try
            {
                if (hasBoxOcr)
                {
                    await TranslateBoxSideAsync(_leftOcrBoxes, LeftOcrText, isLeft: true).ConfigureAwait(true);
                    await TranslateBoxSideAsync(_rightOcrBoxes, RightOcrText, isLeft: false).ConfigureAwait(true);
                    PageViewChanged?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(LeftOcrText))
                    {
                        TranslatedLeftOcrText = await TranslateTextAsync(LeftOcrText).ConfigureAwait(true);
                    }

                    if (!string.IsNullOrWhiteSpace(RightOcrText))
                    {
                        TranslatedRightOcrText = await TranslateTextAsync(RightOcrText).ConfigureAwait(true);
                    }
                }

                SetOcrStatus("번역 완료", InfoBarSeverity.Success, false);
            }
            catch (Exception ex)
            {
                string errorMsg = "번역 오류: " + ex.Message;
                if (hasBoxOcr)
                {
                    foreach (var box in _leftOcrBoxes)
                        if (box.TranslatedText == "번역 중...") box.TranslatedText = errorMsg;
                    foreach (var box in _rightOcrBoxes)
                        if (box.TranslatedText == "번역 중...") box.TranslatedText = errorMsg;
                    TranslatedLeftOcrText = BuildTranslatedTextFromBoxes(_leftOcrBoxes);
                    TranslatedRightOcrText = BuildTranslatedTextFromBoxes(_rightOcrBoxes);
                    PageViewChanged?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    if (TranslatedLeftOcrText == "번역 중...")
                        TranslatedLeftOcrText = errorMsg;
                    if (TranslatedRightOcrText == "번역 중...")
                        TranslatedRightOcrText = errorMsg;
                }

                SetOcrStatus(errorMsg, InfoBarSeverity.Error, true);
                Log.Error(ex, "RunTranslationAsync failed");
            }
        }

        private async Task TranslateBoxSideAsync(IReadOnlyList<BoundingBoxViewModel> boxes, string? pageOcrText, bool isLeft)
        {
            if (boxes.Count == 0)
            {
                if (isLeft) TranslatedLeftOcrText = string.Empty;
                else TranslatedRightOcrText = string.Empty;
                return;
            }

            try
            {
                using var timeoutCts = new CancellationTokenSource(TranslationPerSideTimeout);
                await TranslateBoundingBoxesWithContextAsync(boxes, pageOcrText, timeoutCts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                const string timeoutMessage = "번역 시간 초과";
                foreach (var box in boxes)
                    if (box.TranslatedText == "번역 중...") box.TranslatedText = timeoutMessage;
            }

            string translated = BuildTranslatedTextFromBoxes(boxes);
            if (isLeft) TranslatedLeftOcrText = translated;
            else TranslatedRightOcrText = translated;
            PageViewChanged?.Invoke(this, EventArgs.Empty);
        }

        private static string BuildTranslatedTextFromBoxes(IReadOnlyList<BoundingBoxViewModel> boxes)
        {
            var lines = boxes
                .Select(box => string.IsNullOrWhiteSpace(box.TranslatedText) ? box.Text : box.TranslatedText)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();

            return lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
        }

        private async Task TranslateBoundingBoxesWithContextAsync(IReadOnlyList<BoundingBoxViewModel> boxes, string? pageOcrText, CancellationToken cancellationToken = default)
        {
            if (boxes.Count == 0) return;

            var indexed = boxes
                .Select((box, index) => new IndexedBox { Box = box, Index = index })
                .Where(x => !string.IsNullOrWhiteSpace(x.Box.Text))
                .ToList();
            if (indexed.Count == 0) return;

            var merged = BuildMergedOverlappingBoxes(indexed);
            if (merged.Count == 0) return;

            string fullText = string.Join(Environment.NewLine, merged.Select(x => x.Text));
            string pageText = string.IsNullOrWhiteSpace(pageOcrText) ? fullText : pageOcrText.Trim();
            if (!TryBuildTranslationClient(out var settings, out string model, out string systemPrompt, out IChatClient? client))
                return;

            string targetLanguage = string.IsNullOrWhiteSpace(settings.TargetLanguage) ? "Korean" : settings.TargetLanguage.Trim();

            string boxInputKey = string.Join("\u241E", merged.Select(x => x.Text));
            string cacheKey = settings.Provider == "Ollama"
                ? $"box|{settings.Provider}|{settings.OllamaEndpoint}|{model}|think={settings.ThinkingLevel}|lang={targetLanguage}|{pageText}|{boxInputKey}"
                : $"box|{settings.Provider}|{model}|{settings.ThinkingLevel}|lang={targetLanguage}|{pageText}|{boxInputKey}";

            if (_translationCache.TryGetValue(cacheKey, out string? cached))
            {
                ApplyMergedBoxTranslationsFromJson(merged, cached);
                return;
            }

            var boxPayload = new
            {
                page_text = pageText,
                boxes = merged.Select(x => new { index = x.Index, text = x.Text }).ToArray()
            };

            string jsonPayload = JsonSerializer.Serialize(boxPayload);
            string boxSystemPrompt = string.IsNullOrWhiteSpace(systemPrompt)
                ? "You are a professional manga translator."
                : systemPrompt;
            boxSystemPrompt += $" Translate into {targetLanguage}. Return only strict JSON.";

            var messages = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.System, boxSystemPrompt),
                new ChatMessage(ChatRole.User,
                    $"Use page_text as full-page context, but translate each box independently into {targetLanguage}. " +
                    "For every output item, text must be the translation of the matching input box text with the same index. " +
                    "Do not merge boxes, skip boxes, or output full-page summaries. Preserve speaking style and tone. Return JSON only in this schema: " +
                    "{\"translations\":[{\"index\":0,\"text\":\"...\"}]}. " +
                    "Use the exact same index values from input. " +
                    "Do not add markdown, comments, or extra keys.\nInput JSON:\n" + jsonPayload)
            };

            using (client)
            {
                var response = await client.GetResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(true);
                string content = response.Messages.Count > 0 ? (response.Messages[0].Text ?? string.Empty) : string.Empty;
                string normalizedJson = NormalizeJsonText(content);
                _translationCache[cacheKey] = normalizedJson;
                ApplyMergedBoxTranslationsFromJson(merged, normalizedJson);
            }
        }

        private static List<MergedIndexedBox> BuildMergedOverlappingBoxes(IReadOnlyList<IndexedBox> indexed)
        {
            var visited = new bool[indexed.Count];
            var merged = new List<MergedIndexedBox>();

            for (int i = 0; i < indexed.Count; i++)
            {
                if (visited[i]) continue;

                var stack = new Stack<int>();
                var members = new List<IndexedBox>();
                stack.Push(i);
                visited[i] = true;

                while (stack.Count > 0)
                {
                    int current = stack.Pop();
                    members.Add(indexed[current]);

                    for (int j = 0; j < indexed.Count; j++)
                    {
                        if (visited[j]) continue;
                        if (!AreBoxesOverlapping(indexed[current].Box, indexed[j].Box)) continue;
                        visited[j] = true;
                        stack.Push(j);
                    }
                }

                var orderedMembers = members.OrderBy(x => x.Index).ToList();
                string mergedText = string.Join(Environment.NewLine, orderedMembers.Select(x => x.Box.Text));
                if (string.IsNullOrWhiteSpace(mergedText))
                    continue;

                merged.Add(new MergedIndexedBox
                {
                    Index = merged.Count,
                    Members = orderedMembers,
                    Text = mergedText
                });
            }

            return merged;
        }

        private static bool AreBoxesOverlapping(BoundingBoxViewModel a, BoundingBoxViewModel b)
        {
            double ax1 = a.OriginalX;
            double ay1 = a.OriginalY;
            double ax2 = a.OriginalX + a.OriginalW;
            double ay2 = a.OriginalY + a.OriginalH;

            double bx1 = b.OriginalX;
            double by1 = b.OriginalY;
            double bx2 = b.OriginalX + b.OriginalW;
            double by2 = b.OriginalY + b.OriginalH;

            double overlapW = Math.Min(ax2, bx2) - Math.Max(ax1, bx1);
            double overlapH = Math.Min(ay2, by2) - Math.Max(ay1, by1);
            return overlapW > 0 && overlapH > 0;
        }

        private static string NormalizeJsonText(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return "{}";
            string trimmed = content.Trim();
            if (!trimmed.StartsWith("```", StringComparison.Ordinal))
                return ExtractJsonPayload(trimmed);

            int firstLineBreak = trimmed.IndexOf('\n');
            if (firstLineBreak < 0)
                return trimmed.Trim('`').Trim();

            string body = trimmed[(firstLineBreak + 1)..];
            int fenceEnd = body.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0)
                body = body[..fenceEnd];
            return ExtractJsonPayload(body.Trim());
        }

        private static string ExtractJsonPayload(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "{}";

            int objStart = text.IndexOf('{');
            int arrStart = text.IndexOf('[');
            int start = objStart < 0 ? arrStart : arrStart < 0 ? objStart : Math.Min(objStart, arrStart);
            if (start < 0) return text.Trim();

            int objEnd = text.LastIndexOf('}');
            int arrEnd = text.LastIndexOf(']');
            int end = Math.Max(objEnd, arrEnd);
            if (end < start) return text[start..].Trim();

            return text.Substring(start, end - start + 1).Trim();
        }

        private static void ApplyMergedBoxTranslationsFromJson(IReadOnlyList<MergedIndexedBox> mergedBoxes, string json)
        {
            if (mergedBoxes.Count == 0) return;

            var translationMap = new Dictionary<int, string>();
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var root = doc.RootElement;

                JsonElement translationsElement;
                bool found = false;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("translations", out translationsElement) && translationsElement.ValueKind == JsonValueKind.Array)
                {
                    found = true;
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    translationsElement = root;
                    found = true;
                }
                else
                {
                    translationsElement = default;
                }

                if (found)
                {
                    foreach (var item in translationsElement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        if (!item.TryGetProperty("index", out var indexElement)) continue;

                        int index;
                        if (indexElement.ValueKind == JsonValueKind.Number)
                        {
                            if (!indexElement.TryGetInt32(out index)) continue;
                        }
                        else if (indexElement.ValueKind == JsonValueKind.String && int.TryParse(indexElement.GetString(), out int parsed))
                        {
                            index = parsed;
                        }
                        else
                        {
                            continue;
                        }

                        string text = string.Empty;
                        if (item.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                            text = textElement.GetString() ?? string.Empty;
                        else if (item.TryGetProperty("translated", out var translatedElement) && translatedElement.ValueKind == JsonValueKind.String)
                            text = translatedElement.GetString() ?? string.Empty;
                        else if (item.TryGetProperty("translation", out var translationElement) && translationElement.ValueKind == JsonValueKind.String)
                            text = translationElement.GetString() ?? string.Empty;

                        translationMap[index] = text;
                    }
                }
            }
            catch
            {
            }

            foreach (var group in mergedBoxes)
            {
                if (translationMap.TryGetValue(group.Index, out var translated) && !string.IsNullOrWhiteSpace(translated))
                {
                    foreach (var member in group.Members)
                        member.Box.TranslatedText = translated;
                    continue;
                }

                foreach (var member in group.Members)
                {
                    if (member.Box.TranslatedText == "번역 중...")
                        member.Box.TranslatedText = member.Box.Text;
                }
            }
        }

        private bool TryBuildTranslationClient(out TranslationSettingsService settings, out string model, out string systemPrompt, out IChatClient? client)
        {
            settings = TranslationSettingsService.Instance;
            var thinkingLevel = settings.ThinkingLevel;
            model = string.Empty;
            systemPrompt = string.Empty;
            client = null;

            if (settings.Provider == "Google")
            {
                model = settings.GoogleModel;
                systemPrompt = settings.GoogleSystemPrompt;
                client = new MangaViewer.Services.GoogleGenAIChatClient(settings.GoogleApiKey, model, thinkingLevel);
            }
            else if (settings.Provider == "OpenAI")
            {
                model = settings.OpenAIModel;
                systemPrompt = settings.OpenAISystemPrompt;
                client = new MangaViewer.Services.OpenAIChatClient(settings.OpenAIApiKey, model, thinkingLevel: thinkingLevel);
            }
            else if (settings.Provider == "Anthropic")
            {
                model = settings.AnthropicModel;
                systemPrompt = settings.AnthropicSystemPrompt;
                client = new AnthropicChatClient(settings.AnthropicApiKey, model, thinkingLevel);
            }
            else if (settings.Provider == "Ollama")
            {
                model = settings.OllamaModel;
                systemPrompt = settings.OllamaSystemPrompt;
                client = new OllamaChatClient(settings.OllamaEndpoint, model, thinkingLevel);
            }
            else
            {
                return false;
            }

            return true;
        }

        private async Task<string> TranslateTextAsync(string text, CancellationToken cancellationToken = default)
        {
            if (!TryBuildTranslationClient(out var settings, out string model, out string systemPrompt, out IChatClient? client))
            {
                return "번역 공급자를 설정해주세요.";
            }

            string targetLanguage = string.IsNullOrWhiteSpace(settings.TargetLanguage) ? "Korean" : settings.TargetLanguage.Trim();

            string cacheKey = settings.Provider == "Ollama"
                ? $"{settings.Provider}|{settings.OllamaEndpoint}|{model}|think={settings.ThinkingLevel}|lang={targetLanguage}|{text}"
                : $"{settings.Provider}|{model}|{settings.ThinkingLevel}|lang={targetLanguage}|{text}";
            if (_translationCache.TryGetValue(cacheKey, out string? cached))
                return cached;

            var messages = new List<ChatMessage>();
            string effectiveSystemPrompt = string.IsNullOrWhiteSpace(systemPrompt)
                ? $"You are a professional translator. Translate the user text into {targetLanguage}. Return only the translated text."
                : systemPrompt + $" Translate the output into {targetLanguage} and return only the translated text.";
            messages.Add(new ChatMessage(ChatRole.System, effectiveSystemPrompt));
            messages.Add(new ChatMessage(ChatRole.User, text));

            using (client)
            {
                var response = await client.GetResponseAsync(messages, cancellationToken: cancellationToken);
                string result = response.Messages.Count > 0 ? (response.Messages[0].Text ?? "") : "";
                _translationCache[cacheKey] = result;
                return result;
            }
        }

        private void StartAdjacentPagePrefetch()
        {
            int ocrRadius = _ocrService.PrefetchAdjacentPagesEnabled ? _ocrService.PrefetchAdjacentPageCount : 0;
            var translationSettings = TranslationSettingsService.Instance;
            int translationRadius = (IsTranslationActive && translationSettings.PrefetchAdjacentPagesEnabled) ? translationSettings.PrefetchAdjacentPageCount : 0;
            int maxRadius = Math.Max(ocrRadius, translationRadius);
            if (maxRadius <= 0) return;

            _adjacentPrefetchCts?.Cancel();
            _adjacentPrefetchCts?.Dispose();
            _adjacentPrefetchCts = new CancellationTokenSource();
            var token = _adjacentPrefetchCts.Token;
            int currentPageIndex = _mangaManager.CurrentPageIndex;

            var ocrPaths = GetAdjacentPageImagePaths(currentPageIndex, ocrRadius);
            var translationPaths = GetAdjacentPageImagePaths(currentPageIndex, translationRadius);

            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var path in ocrPaths)
                    {
                        token.ThrowIfCancellationRequested();
                        if (_ocrService.Backend == OcrService.OcrBackend.Hybrid)
                            await _ocrService.GetHybridOcrAsync(path, token).ConfigureAwait(false);
                        else
                            await _ocrService.GetOllamaTextAsync(path, token).ConfigureAwait(false);
                    }

                    if (translationPaths.Count == 0)
                        return;

                    foreach (var path in translationPaths)
                    {
                        token.ThrowIfCancellationRequested();
                        var text = await _ocrService.GetOllamaTextAsync(path, token).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(text))
                            await TranslateTextAsync(text, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.Error(ex, "[Prefetch] Adjacent OCR/translation prefetch failed");
                }
            }, token);
        }

        private List<string> GetAdjacentPageImagePaths(int centerPageIndex, int radius)
        {
            var result = new List<string>();
            if (radius <= 0 || _mangaManager.TotalPages <= 0) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int offset = 1; offset <= radius; offset++)
            {
                int back = centerPageIndex - offset;
                if (back >= 0)
                {
                    var backPaths = _mangaManager.GetImagePathsForPage(back);
                    for (int i = 0; i < backPaths.Count; i++)
                    {
                        string p = backPaths[i];
                        if (!string.IsNullOrWhiteSpace(p) && seen.Add(p)) result.Add(p);
                    }
                }

                int forward = centerPageIndex + offset;
                if (forward < _mangaManager.TotalPages)
                {
                    var forwardPaths = _mangaManager.GetImagePathsForPage(forward);
                    for (int i = 0; i < forwardPaths.Count; i++)
                    {
                        string p = forwardPaths[i];
                        if (!string.IsNullOrWhiteSpace(p) && seen.Add(p)) result.Add(p);
                    }
                }
            }

            return result;
        }

        private void RestorePageOcrTranslationState(int pageIndex)
        {
            bool ocrState = _pageOcrStates.TryGetValue(pageIndex, out bool o) && o;
            bool transState = _pageTranslationStates.TryGetValue(pageIndex, out bool t) && t;

            if (_isOcrActive != ocrState)
            {
                _isOcrActive = ocrState;
                OnPropertyChanged(nameof(IsOcrActive));
                OnPropertyChanged(nameof(IsTranslationToggleEnabled));
            }

            if (_isTranslationActive != transState)
            {
                _isTranslationActive = transState;
                OnPropertyChanged(nameof(IsTranslationActive));
                OnPropertyChanged(nameof(IsTranslationVisible));
            }

            if (!_isTranslationActive)
            {
                TranslatedLeftOcrText = string.Empty;
                TranslatedRightOcrText = string.Empty;
                ClearBoxTranslations();
            }
        }

        private void ClearBoxTranslations()
        {
            foreach (var box in _leftOcrBoxes)
                box.TranslatedText = string.Empty;
            foreach (var box in _rightOcrBoxes)
                box.TranslatedText = string.Empty;
        }

        private void CancelOcr()
        {
            if (_ocrCts != null && !_ocrCts.IsCancellationRequested)
            {
                _ocrCts.Cancel();
                SetOcrStatus("OCR 취소 요청...", InfoBarSeverity.Informational, true);
            }

            if (_adjacentPrefetchCts != null && !_adjacentPrefetchCts.IsCancellationRequested)
                _adjacentPrefetchCts.Cancel();
        }

        private void ClearOcr()
        {
            _leftOcrBoxes.Clear();
            _rightOcrBoxes.Clear();
            LeftOcrText = string.Empty;
            RightOcrText = string.Empty;
            RaiseOllamaOcrTextVisibilityChanged();
            OcrCompleted?.Invoke(this, EventArgs.Empty);
        }

        private void SetOcrStatus(string message, InfoBarSeverity severity = InfoBarSeverity.Informational, bool open = true)
        {
            OcrStatusMessage = message;
            OcrSeverity = severity;
            IsInfoBarOpen = open;
        }

        private void AddCurrentBookmark()
        {
            if (_mangaManager.TotalImages <= 0) return;
            int imageIndex = _mangaManager.GetPrimaryImageIndexForPage(_mangaManager.CurrentPageIndex);
            if (imageIndex < 0) return;
            if (imageIndex >= Thumbnails.Count) return;

            var path = Thumbnails[imageIndex].FilePath;
            if (string.IsNullOrWhiteSpace(path)) return;

            if (!_bookmarkService.Add(path)) return;

            foreach (var b in Bookmarks)
                if (string.Equals(b.FilePath, path, StringComparison.OrdinalIgnoreCase)) return;

            Bookmarks.Add(new MangaPageViewModel { FilePath = path });
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
            if (idx >= 0)
            {
                SelectedThumbnailIndex = idx;
            }
        }

        public void CreatePlaceholderPages(int count) => _mangaManager.CreatePlaceholders(count);
        public void ReplacePlaceholderWithFile(int index, string path)
        {
            if (index < 0) return;
            _mangaManager.ReplaceFileAtIndex(index, path);
        }
        public void SetExpectedTotalPages(int total) => _mangaManager.SetExpectedTotal(total);

        public void Dispose()
        {
            CancelOcr();
            _adjacentPrefetchCts?.Dispose();
            _adjacentPrefetchCts = null;
            _ocrCts?.Dispose();
            _ocrCts = null;
            _pageLoadCts?.Cancel();
            _pageLoadCts?.Dispose();
            _pageLoadCts = null;
            _mangaManager.MangaLoaded -= OnMangaLoaded;
            _mangaManager.PageChanged -= OnPageChanged;
            _ocrService.SettingsChanged -= OnOcrSettingsChanged;
            TranslationSettingsService.Instance.SettingsChanged -= OnTranslationSettingsChanged;
            GC.SuppressFinalize(this);
        }
    }
}