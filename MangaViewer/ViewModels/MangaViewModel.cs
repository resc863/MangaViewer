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

namespace MangaViewer.ViewModels
{
    public partial class MangaViewModel : BaseViewModel, IDisposable
    {
        private readonly MangaManager _mangaManager = new();
        private readonly ImageCacheService _imageCache = ImageCacheService.Instance;
        private readonly OcrService _ocrService = OcrService.Instance;
        private readonly BookmarkService _bookmarkService = BookmarkService.Instance;
        private readonly LibraryService _libraryService = new();
        private readonly TranslationService _translationService = TranslationService.Instance;

        private BitmapImage? _leftImageSource;
        private BitmapImage? _rightImageSource;
        private int _selectedThumbnailIndex = -1;
        private bool _isPaneOpen;
        private bool _isBookmarkFilterActive;
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
        private readonly SemaphoreSlim _ocrRunGate = new(1, 1);
        private CancellationTokenSource? _pageLoadCts;
        private int _pageLoadVersion;

        private readonly Dictionary<int, bool> _pageOcrStates = new();
        private readonly Dictionary<int, bool> _pageTranslationStates = new();
        private static readonly TimeSpan TranslationPerSideTimeout = TimeSpan.FromSeconds(120);
        private CancellationTokenSource? _adjacentPrefetchCts;
        private bool _forceRefreshOllamaOcrOnNextRun;

        private string _ocrStatusMessage = string.Empty;
        private bool _isInfoBarOpen;
        private InfoBarSeverity _ocrSeverity = InfoBarSeverity.Informational;
        private string _progressPopupMessage = string.Empty;
        private bool _isProgressPopupOpen;
        private int _progressPopupVersion;

        public ReadOnlyObservableCollection<MangaPageViewModel> Thumbnails => _mangaManager.Pages;
        public ObservableCollection<MangaPageViewModel> Bookmarks { get; } = new();
        public IReadOnlyList<MangaPageViewModel> VisibleThumbnails => IsBookmarkFilterActive ? Bookmarks : Thumbnails;
        public bool HasVisibleThumbnails => VisibleThumbnails.Count > 0;
        public bool IsBookmarkFilterEmpty => IsBookmarkFilterActive && Bookmarks.Count == 0;

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
                        if (!IsOcrRunning)
                            _ = RunTranslationAsync();
                    }
                    else
                    {
                        ClearTranslationState(notifyPageView: true);
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
        public string ProgressPopupMessage { get => _progressPopupMessage; private set => SetProperty(ref _progressPopupMessage, value); }
        public bool IsProgressPopupOpen { get => _isProgressPopupOpen; private set => SetProperty(ref _isProgressPopupOpen, value); }
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
                    SyncDisplayedThumbnailSelection();
                    if (value >= 0)
                        _mangaManager.SetCurrentPageFromImageIndex(value);
                }
            }
        }

        private MangaPageViewModel? _selectedThumbnailItem;
        public MangaPageViewModel? SelectedThumbnailItem
        {
            get => _selectedThumbnailItem;
            set
            {
                if (SetProperty(ref _selectedThumbnailItem, value) && value != null)
                    NavigateToThumbnail(value);
            }
        }

        public bool IsPaneOpen { get => _isPaneOpen; set => SetProperty(ref _isPaneOpen, value); }
        public bool IsBookmarkFilterActive
        {
            get => _isBookmarkFilterActive;
            set
            {
                if (SetProperty(ref _isBookmarkFilterActive, value))
                {
                    OnPropertyChanged(nameof(VisibleThumbnails));
                    OnPropertyChanged(nameof(HasVisibleThumbnails));
                    OnPropertyChanged(nameof(IsBookmarkFilterEmpty));
                    SyncDisplayedThumbnailSelection();
                }
            }
        }
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

        public string DirectionButtonText => _mangaManager.IsRightToLeft
            ? LocalizationHelper.GetString("Reader.Direction.Reverse", "읽기 방향: 역방향")
            : LocalizationHelper.GetString("Reader.Direction.Forward", "읽기 방향: 정방향");
        public string CoverButtonText => _mangaManager.IsCoverSeparate
            ? LocalizationHelper.GetString("Reader.Cover.Single", "표지: 한 장으로 보기")
            : LocalizationHelper.GetString("Reader.Cover.Double", "표지: 두 장으로 보기");

        public void RefreshLocalizedTexts()
        {
            OnPropertyChanged(nameof(DirectionButtonText));
            OnPropertyChanged(nameof(CoverButtonText));
        }

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
        public AsyncRelayCommand RefreshTranslationCommand { get; }
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
            ToggleBookmarkPaneCommand = new RelayCommand(_ => IsBookmarkFilterActive = !IsBookmarkFilterActive);
            ToggleNavPaneCommand = new RelayCommand(_ => IsNavOpen = !IsNavOpen);
            GoLeftCommand = new RelayCommand(_ => NavigateLogicalLeft(), _ => _mangaManager.TotalImages > 0);
            GoRightCommand = new RelayCommand(_ => NavigateLogicalRight(), _ => _mangaManager.TotalImages > 0);
            RefreshTranslationCommand = new AsyncRelayCommand(async _ => await RefreshCurrentPageTranslationAsync(), _ => CanRefreshCurrentPageTranslation());
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
            _translationService.ClearCache();
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
            OnPropertyChanged(nameof(VisibleThumbnails));
            OnPropertyChanged(nameof(HasVisibleThumbnails));
            OnPropertyChanged(nameof(IsBookmarkFilterEmpty));
            SyncDisplayedThumbnailSelection();
            UpdateCommandStates();
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

            try
            {
                _bookmarkService.LoadForFolder(_mangaManager.CurrentFolderPath);
                RebuildBookmarksFromStore();
            }
            catch { }

            UpdateCommandStates();

            MangaFolderLoaded?.Invoke(this, EventArgs.Empty);
            // Trigger initial page load
            OnPageChanged();
        }

        private void RebuildBookmarksFromStore()
        {
            Bookmarks.Clear();

            var distinct = new HashSet<MangaPageViewModel>();
            foreach (var p in _bookmarkService.GetAll())
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                int index = _mangaManager.FindImageIndexByPath(p);
                if (index < 0 || index >= Thumbnails.Count) continue;

                var thumbnail = Thumbnails[index];
                if (!distinct.Add(thumbnail)) continue;
                Bookmarks.Add(thumbnail);
            }

            OnPropertyChanged(nameof(VisibleThumbnails));
            OnPropertyChanged(nameof(HasVisibleThumbnails));
            OnPropertyChanged(nameof(IsBookmarkFilterEmpty));
            SyncDisplayedThumbnailSelection();
            UpdateCommandStates();
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
                if (TryRestoreCurrentPageOcrFromCache())
                {
                    if (IsTranslationActive)
                    {
                            ClearTranslationState(notifyPageView: false);
                        _ = RunTranslationAsync();
                    }
                }
                else
                {
                    _ = RunOcrAsync();
                }
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
                double w = IsTwoPageMode ? _leftWrapperWidth : _singleWrapperWidth;
                double h = IsTwoPageMode ? _leftWrapperHeight : _singleWrapperHeight;
                await LoadPageImageAsync(leftPath, w, h, token, localVersion, () => LeftImageFilePath, img => LeftImageSource = img).ConfigureAwait(true);
            }

            if (!string.IsNullOrEmpty(rightPath))
            {
                await LoadPageImageAsync(rightPath, _rightWrapperWidth, _rightWrapperHeight, token, localVersion, () => RightImageFilePath, img => RightImageSource = img).ConfigureAwait(true);
            }
        }

        private async Task LoadPageImageAsync(string path, double width, double height, CancellationToken token, int localVersion, Func<string?> currentPathAccessor, Action<BitmapImage?> applyImage)
        {
            token.ThrowIfCancellationRequested();

            var img = await _imageCache.GetForViewportAsync(path, Math.Max(1, width), Math.Max(1, height), _rasterizationScale).ConfigureAwait(true);
            if (!token.IsCancellationRequested && localVersion == _pageLoadVersion && string.Equals(currentPathAccessor(), path, StringComparison.Ordinal))
                applyImage(img);
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
            else
                SyncDisplayedThumbnailSelection();
        }

        public int GetThumbnailIndex(MangaPageViewModel? page)
            => page == null ? -1 : Thumbnails.IndexOf(page);

        private void SyncDisplayedThumbnailSelection()
        {
            MangaPageViewModel? selected = null;
            if (_selectedThumbnailIndex >= 0 && _selectedThumbnailIndex < Thumbnails.Count)
            {
                var current = Thumbnails[_selectedThumbnailIndex];
                if (!IsBookmarkFilterActive || Bookmarks.Contains(current))
                    selected = current;
            }

            if (!ReferenceEquals(_selectedThumbnailItem, selected))
            {
                _selectedThumbnailItem = selected;
                OnPropertyChanged(nameof(SelectedThumbnailItem));
            }
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
            RefreshTranslationCommand.RaiseCanExecuteChanged();
            AddBookmarkCommand.RaiseCanExecuteChanged();
            RemoveBookmarkCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsOcrToggleEnabled));
        }

        private bool CanRefreshCurrentPageTranslation()
            => IsTranslationActive
                && IsOcrActive
                && !IsOcrRunning
                && (!string.IsNullOrWhiteSpace(LeftImageFilePath) || !string.IsNullOrWhiteSpace(RightImageFilePath));

        private async Task RefreshCurrentPageTranslationAsync()
        {
            if (!CanRefreshCurrentPageTranslation())
                return;

            bool hasOcrContent = _leftOcrBoxes.Count > 0
                || _rightOcrBoxes.Count > 0
                || !string.IsNullOrWhiteSpace(LeftOcrText)
                || !string.IsNullOrWhiteSpace(RightOcrText);

            if (!hasOcrContent)
            {
                SetOcrStatus("현재 페이지 OCR 결과가 없어 번역을 다시 요청할 수 없습니다.", InfoBarSeverity.Warning, true);
                return;
            }

            ClearTranslationState(notifyPageView: true);
            await RunTranslationAsync(forceRefresh: true).ConfigureAwait(true);
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
                        await LoadPageImageAsync(LeftImageFilePath, w, h, token, localVersion, () => LeftImageFilePath, img => LeftImageSource = img).ConfigureAwait(true);
                }

                if (!string.IsNullOrEmpty(RightImageFilePath))
                {
                    if (_rightWrapperWidth > 0 && _rightWrapperHeight > 0)
                        await LoadPageImageAsync(RightImageFilePath, _rightWrapperWidth, _rightWrapperHeight, token, localVersion, () => RightImageFilePath, img => RightImageSource = img).ConfigureAwait(true);
                }
            }
            catch { }
        }

        private async Task RunOcrAsync()
        {
            Interlocked.Increment(ref _ocrVersion);

            if (!await _ocrRunGate.WaitAsync(0).ConfigureAwait(true))
            {
                CancelOcr();
                return;
            }

            try
            {
                int retryVersion = -1;
                int retryCountForVersion = 0;

                while (IsOcrActive)
                {
                    int localVersion = Volatile.Read(ref _ocrVersion);
                    if (retryVersion != localVersion)
                    {
                        retryVersion = localVersion;
                        retryCountForVersion = 0;
                    }

                    var originalPaths = _mangaManager.GetImagePathsForCurrentPage();
                    if (originalPaths.Count == 0)
                        break;

                    var paths = new List<string>(originalPaths);
                    if (_mangaManager.IsRightToLeft && paths.Count == 2)
                    {
                        (paths[0], paths[1]) = (paths[1], paths[0]);
                    }

                    IsOcrRunning = true;
                    OnPropertyChanged(nameof(IsControlEnabled));
                    _ocrCts = new CancellationTokenSource();
                    var token = _ocrCts.Token;
                    bool shouldRetryCurrentVersion = false;

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

                        if (IsTranslationActive)
                            _ = RunTranslationThenPrefetchAsync(localVersion);
                        else
                            StartAdjacentPagePrefetch();
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

                        if (localVersion == Volatile.Read(ref _ocrVersion)
                            && IsOcrActive
                            && retryCountForVersion < 1)
                        {
                            retryCountForVersion++;
                            shouldRetryCurrentVersion = true;
                            SetOcrStatus("OCR 재시도 중...", InfoBarSeverity.Warning, true);
                        }
                    }
                    finally
                    {
                        IsOcrRunning = false;
                        OnPropertyChanged(nameof(IsControlEnabled));
                        OcrCompleted?.Invoke(this, EventArgs.Empty);
                        _ocrCts?.Dispose();
                        _ocrCts = null;
                    }

                    if (shouldRetryCurrentVersion)
                    {
                        await Task.Delay(200).ConfigureAwait(true);
                        continue;
                    }

                    if (localVersion == Volatile.Read(ref _ocrVersion))
                        break;
                }
            }
            finally
            {
                _ocrRunGate.Release();
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
                if (!result.IsSuccessful)
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StatusMessage) ? "OCR request did not complete." : result.StatusMessage);

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
                ShowProgressPopup(BuildPerPageProgressMessage("OCR", completedPages, totalPages, paths[pageIndex]));

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
            string fallbackStatusMessage = string.Empty;

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
                if (!result.IsSuccessful)
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StatusMessage) ? "OCR request did not complete." : result.StatusMessage);

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
                ShowProgressPopup(BuildPerPageProgressMessage("OCR", completedPages, totalPages, paths[pageIndex]));

                if (!string.IsNullOrWhiteSpace(result.StatusMessage))
                    fallbackStatusMessage = result.StatusMessage;

                RaiseOllamaOcrTextVisibilityChanged();
                PageViewChanged?.Invoke(this, EventArgs.Empty);

                if (completedPages < totalPages)
                    SetOcrStatus($"Hybrid OCR 실행 중... ({completedPages}/{totalPages})", InfoBarSeverity.Informational, false);
            }

            ThrowIfOcrRunStale(localVersion, token);
            _forceRefreshOllamaOcrOnNextRun = false;
            RaiseOllamaOcrTextVisibilityChanged();
            if (!string.IsNullOrWhiteSpace(fallbackStatusMessage))
                SetOcrStatus(fallbackStatusMessage, InfoBarSeverity.Warning, true);
            else
                SetOcrStatus($"Hybrid OCR 완료: {totalBoxes} boxes", InfoBarSeverity.Success, false);
        }

        private void ThrowIfOcrRunStale(int localVersion, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (localVersion != _ocrVersion)
                throw new OperationCanceledException();
        }

        public Task EnsureOcrBoxTextAsync(BoundingBoxViewModel box)
            => EnsureOcrBoxTextAsync(box, isLeft: _leftOcrBoxes.Contains(box));

        public async Task EnsureOcrBoxTextAsync(BoundingBoxViewModel box, bool isLeft)
        {
            if (box == null || !string.IsNullOrWhiteSpace(box.Text) || _ocrService.Backend != OcrService.OcrBackend.Hybrid)
                return;

            var targetBoxes = isLeft ? _leftOcrBoxes : _rightOcrBoxes;
            if (!targetBoxes.Contains(box))
                return;

            string? imagePath = isLeft ? LeftImageFilePath : RightImageFilePath;
            if (string.IsNullOrWhiteSpace(imagePath))
                return;

            SelectedOcrBox = box;
            SetOcrStatus("선택한 OCR 박스 재요청 중...", InfoBarSeverity.Informational, false);

            try
            {
                string resolvedText = await _ocrService.GetHybridBoxTextAsync(imagePath, box, CancellationToken.None).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(resolvedText))
                {
                    SetOcrStatus("선택한 OCR 박스에서 텍스트를 확인하지 못했습니다.", InfoBarSeverity.Warning, true);
                    return;
                }

                box.Text = resolvedText;
                if (isLeft)
                {
                    LeftOcrText = BuildOcrTextFromBoxes(targetBoxes);
                    if (IsTranslationActive)
                        TranslatedLeftOcrText = BuildTranslatedTextFromBoxes(targetBoxes);
                }
                else
                {
                    RightOcrText = BuildOcrTextFromBoxes(targetBoxes);
                    if (IsTranslationActive)
                        TranslatedRightOcrText = BuildTranslatedTextFromBoxes(targetBoxes);
                }

                PageViewChanged?.Invoke(this, EventArgs.Empty);
                SetOcrStatus("선택한 OCR 박스 갱신 완료", InfoBarSeverity.Success, false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SetOcrStatus("선택한 OCR 박스 재요청 오류: " + ex.Message, InfoBarSeverity.Error, true);
                Log.Error(ex, "EnsureOcrBoxTextAsync failed");
            }
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

        private bool TryRestoreCurrentPageOcrFromCache()
        {
            var originalPaths = _mangaManager.GetImagePathsForCurrentPage();
            if (originalPaths.Count == 0)
                return false;

            var paths = new List<string>(originalPaths);
            if (_mangaManager.IsRightToLeft && paths.Count == 2)
                (paths[0], paths[1]) = (paths[1], paths[0]);

            var cachedResults = new OcrService.OllamaOcrResponse[paths.Count];
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                if (string.IsNullOrWhiteSpace(path))
                {
                    cachedResults[i] = new OcrService.OllamaOcrResponse();
                    continue;
                }

                if (!_ocrService.TryGetCachedCurrentBackendOcr(path, out var cached))
                    return false;

                cachedResults[i] = cached;
            }

            if (cachedResults.Length > 0)
            {
                LeftOcrText = cachedResults[0].Text;
                foreach (var box in cachedResults[0].Boxes)
                    _leftOcrBoxes.Add(box);
            }

            if (cachedResults.Length > 1)
            {
                RightOcrText = cachedResults[1].Text;
                foreach (var box in cachedResults[1].Boxes)
                    _rightOcrBoxes.Add(box);
            }

            RaiseOllamaOcrTextVisibilityChanged();
            PageViewChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        private async Task RunTranslationAsync(bool forceRefresh = false)
        {
            bool hasBoxOcr = _leftOcrBoxes.Count > 0 || _rightOcrBoxes.Count > 0;
            bool hasPlainTextOcr = !string.IsNullOrWhiteSpace(LeftOcrText) || !string.IsNullOrWhiteSpace(RightOcrText);
            if (!hasBoxOcr && !hasPlainTextOcr) return;

            bool leftHasTranslationTarget = hasBoxOcr
                ? _leftOcrBoxes.Any(b => !string.IsNullOrWhiteSpace(b.Text))
                : !string.IsNullOrWhiteSpace(LeftOcrText);
            bool rightHasTranslationTarget = hasBoxOcr
                ? _rightOcrBoxes.Any(b => !string.IsNullOrWhiteSpace(b.Text))
                : !string.IsNullOrWhiteSpace(RightOcrText);

            int totalTranslationTargets = (leftHasTranslationTarget ? 1 : 0) + (rightHasTranslationTarget ? 1 : 0);
            int completedTranslationTargets = 0;

            void NotifyTranslationProgress(bool isLeft)
            {
                if (totalTranslationTargets <= 0)
                    return;

                completedTranslationTargets++;
                string? path = isLeft ? LeftImageFilePath : RightImageFilePath;
                ShowProgressPopup(BuildPerPageProgressMessage("번역", completedTranslationTargets, totalTranslationTargets, path));
            }

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
                    if (_mangaManager.IsRightToLeft)
                    {
                        await TranslateBoxSideAsync(_rightOcrBoxes, RightOcrText, isLeft: false, forceRefresh: forceRefresh).ConfigureAwait(true);
                        if (rightHasTranslationTarget)
                            NotifyTranslationProgress(isLeft: false);
                        await TranslateBoxSideAsync(_leftOcrBoxes, LeftOcrText, isLeft: true, forceRefresh: forceRefresh).ConfigureAwait(true);
                        if (leftHasTranslationTarget)
                            NotifyTranslationProgress(isLeft: true);
                    }
                    else
                    {
                        await TranslateBoxSideAsync(_leftOcrBoxes, LeftOcrText, isLeft: true, forceRefresh: forceRefresh).ConfigureAwait(true);
                        if (leftHasTranslationTarget)
                            NotifyTranslationProgress(isLeft: true);
                        await TranslateBoxSideAsync(_rightOcrBoxes, RightOcrText, isLeft: false, forceRefresh: forceRefresh).ConfigureAwait(true);
                        if (rightHasTranslationTarget)
                            NotifyTranslationProgress(isLeft: false);
                    }
                    PageViewChanged?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(LeftOcrText))
                    {
                        TranslatedLeftOcrText = await TranslateTextAsync(LeftOcrText, forceRefresh: forceRefresh).ConfigureAwait(true);
                        NotifyTranslationProgress(isLeft: true);
                    }

                    if (!string.IsNullOrWhiteSpace(RightOcrText))
                    {
                        TranslatedRightOcrText = await TranslateTextAsync(RightOcrText, forceRefresh: forceRefresh).ConfigureAwait(true);
                        NotifyTranslationProgress(isLeft: false);
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

        private async Task RunTranslationThenPrefetchAsync(int expectedOcrVersion)
        {
            try
            {
                await RunTranslationAsync().ConfigureAwait(true);

                if (!IsTranslationActive || !IsOcrActive)
                    return;

                if (expectedOcrVersion != Volatile.Read(ref _ocrVersion))
                    return;

                StartAdjacentPagePrefetch();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "RunTranslationThenPrefetchAsync failed");
            }
        }

        private async Task TranslateBoxSideAsync(IReadOnlyList<BoundingBoxViewModel> boxes, string? pageOcrText, bool isLeft, bool forceRefresh = false)
        {
            if (boxes.Count == 0)
            {
                if (isLeft) TranslatedLeftOcrText = string.Empty;
                else TranslatedRightOcrText = string.Empty;
                return;
            }

            try
            {
                TimeSpan timeout = TranslationPerSideTimeout;
                var settings = TranslationSettingsService.Instance;
                if (TranslationSettingsService.IsOllamaProvider(settings.ProviderKind))
                {
                    bool thinkingEnabled = !ThinkingLevelHelper.NormalizeOllama(settings.ThinkingLevel)
                        .Equals("Off", StringComparison.OrdinalIgnoreCase);
                    timeout = OllamaRequestLoadCoordinator.GetSuggestedTimeout(timeout, thinkingEnabled);
                }

                using var timeoutCts = new CancellationTokenSource(timeout);
                await TranslateBoundingBoxesWithContextAsync(boxes, pageOcrText, forceRefresh, timeoutCts.Token).ConfigureAwait(true);
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

        private static string BuildOcrTextFromBoxes(IReadOnlyList<BoundingBoxViewModel> boxes)
        {
            var lines = boxes
                .Select(box => box.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();

            return lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
        }

        private static string BuildTranslatedTextFromBoxes(IReadOnlyList<BoundingBoxViewModel> boxes)
        {
            var lines = boxes
                .Select(box => string.IsNullOrWhiteSpace(box.TranslatedText) ? box.Text : box.TranslatedText)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();

            return lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
        }

        private async Task TranslateBoundingBoxesWithContextAsync(IReadOnlyList<BoundingBoxViewModel> boxes, string? pageOcrText, bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            if (boxes.Count == 0) return;

            var indexed = boxes
                .Select((box, index) => new IndexedBox { Box = box, Index = index })
                .Where(x => !string.IsNullOrWhiteSpace(x.Box.Text))
                .ToList();
            if (indexed.Count == 0) return;

            var merged = BuildMergedOverlappingBoxes(indexed);
            if (merged.Count == 0) return;

            var inputs = merged
                .Select(group => new IndexedTranslationInput(group.Index, group.Text))
                .ToArray();
            var translations = await _translationService
                .TranslateIndexedTextAsync(inputs, pageOcrText, forceRefresh, cancellationToken)
                .ConfigureAwait(true);
            ApplyMergedBoxTranslations(merged, translations);
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

        private static void ApplyMergedBoxTranslations(IReadOnlyList<MergedIndexedBox> mergedBoxes, IReadOnlyDictionary<int, string> translations)
        {
            if (mergedBoxes.Count == 0) return;

            foreach (var group in mergedBoxes)
            {
                if (translations.TryGetValue(group.Index, out var translated) && !string.IsNullOrWhiteSpace(translated))
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

        private async Task<string> TranslateTextAsync(string text, bool forceRefresh = false, CancellationToken cancellationToken = default)
            => await _translationService.TranslateTextAsync(text, forceRefresh, cancellationToken).ConfigureAwait(true);

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

            bool prioritizeReverseReading = _mangaManager.IsRightToLeft;
            var ocrPaths = GetAdjacentPageImagePaths(currentPageIndex, ocrRadius, prioritizeReverseReading);
            var translationPaths = GetAdjacentPageImagePaths(currentPageIndex, translationRadius, prioritizeReverseReading);

            _ = Task.Run(async () =>
            {
                try
                {
                    var translationPathSet = new HashSet<string>(translationPaths, StringComparer.OrdinalIgnoreCase);
                    var prefetchedTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var path in ocrPaths)
                    {
                        token.ThrowIfCancellationRequested();

                        var ocrResult = await GetBackendOcrForAdjacentPrefetchAsync(path, token).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(ocrResult.Text) && translationPathSet.Contains(path))
                            prefetchedTexts[path] = ocrResult.Text;

                        translationPathSet.Remove(path);
                    }

                    foreach (var path in translationPathSet)
                    {
                        token.ThrowIfCancellationRequested();

                        var ocrResult = await GetBackendOcrForAdjacentPrefetchAsync(path, token).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(ocrResult.Text))
                            prefetchedTexts[path] = ocrResult.Text;
                    }

                    foreach (var pair in prefetchedTexts)
                    {
                        token.ThrowIfCancellationRequested();
                        await TranslateTextAsync(pair.Value, cancellationToken: token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.Error(ex, "[Prefetch] Adjacent OCR/translation prefetch failed");
                }
            }, token);
        }

        private Task<OcrService.OllamaOcrResponse> GetBackendOcrForAdjacentPrefetchAsync(string imagePath, CancellationToken token)
        {
            if (_ocrService.Backend == OcrService.OcrBackend.Hybrid)
                return _ocrService.GetHybridOcrAsync(imagePath, token);

            return _ocrService.GetOllamaOcrAsync(imagePath, token);
        }

        private List<string> GetAdjacentPageImagePaths(int centerPageIndex, int radius, bool prioritizeReverseReading)
        {
            var result = new List<string>();
            if (radius <= 0 || _mangaManager.TotalPages <= 0) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddPagePaths(int pageIndex)
            {
                if (pageIndex < 0 || pageIndex >= _mangaManager.TotalPages)
                    return;

                var pagePaths = _mangaManager.GetImagePathsForPage(pageIndex);
                for (int i = 0; i < pagePaths.Count; i++)
                {
                    string p = pagePaths[i];
                    if (!string.IsNullOrWhiteSpace(p) && seen.Add(p))
                        result.Add(p);
                }
            }

            for (int offset = 1; offset <= radius; offset++)
            {
                int forward = centerPageIndex + offset;
                int backward = centerPageIndex - offset;

                if (prioritizeReverseReading)
                {
                    AddPagePaths(backward);
                    AddPagePaths(forward);
                }
                else
                {
                    AddPagePaths(forward);
                    AddPagePaths(backward);
                }
            }

            return result;
        }

        private void RestorePageOcrTranslationState(int pageIndex)
        {
            bool transState = _pageTranslationStates.TryGetValue(pageIndex, out bool t) && t;

            if (_isTranslationActive != transState)
            {
                _isTranslationActive = transState;
                OnPropertyChanged(nameof(IsTranslationActive));
                OnPropertyChanged(nameof(IsTranslationVisible));
            }

            if (!_isTranslationActive)
                ClearTranslationState(notifyPageView: false);
        }

        private void ClearTranslationState(bool notifyPageView)
        {
            TranslatedLeftOcrText = string.Empty;
            TranslatedRightOcrText = string.Empty;
            ClearBoxTranslations();

            if (notifyPageView)
                PageViewChanged?.Invoke(this, EventArgs.Empty);
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

            _ocrService.CancelActiveOcrRequests();
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

        private static readonly TimeSpan ProgressPopupDuration = TimeSpan.FromSeconds(1.8);

        private string BuildPerPageProgressMessage(string operationName, int completed, int total, string? imagePath)
        {
            int pageNumber = ResolveImagePageNumber(imagePath);
            if (pageNumber > 0)
                return $"{operationName} 진행: {completed}/{Math.Max(1, total)} (페이지 {pageNumber})";

            return $"{operationName} 진행: {completed}/{Math.Max(1, total)}";
        }

        private int ResolveImagePageNumber(string? imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
                return 0;

            int imageIndex = _mangaManager.FindImageIndexByPath(imagePath);
            return imageIndex >= 0 ? imageIndex + 1 : 0;
        }

        private async void ShowProgressPopup(string message)
        {
            ProgressPopupMessage = message;
            IsProgressPopupOpen = true;

            int version = Interlocked.Increment(ref _progressPopupVersion);
            try
            {
                await Task.Delay(ProgressPopupDuration).ConfigureAwait(true);
            }
            catch
            {
                return;
            }

            if (version == _progressPopupVersion)
                IsProgressPopupOpen = false;
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

            Bookmarks.Add(Thumbnails[imageIndex]);
            OnPropertyChanged(nameof(VisibleThumbnails));
            OnPropertyChanged(nameof(HasVisibleThumbnails));
            OnPropertyChanged(nameof(IsBookmarkFilterEmpty));
            SyncDisplayedThumbnailSelection();
            UpdateCommandStates();
        }

        private void RemoveBookmark(MangaPageViewModel? vm)
        {
            if (vm?.FilePath == null) return;
            if (_bookmarkService.Remove(vm.FilePath))
            {
                var existing = Bookmarks.FirstOrDefault(b => string.Equals(b.FilePath, vm.FilePath, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                    Bookmarks.Remove(existing);

                OnPropertyChanged(nameof(VisibleThumbnails));
                OnPropertyChanged(nameof(HasVisibleThumbnails));
                OnPropertyChanged(nameof(IsBookmarkFilterEmpty));
                SyncDisplayedThumbnailSelection();
                UpdateCommandStates();
            }
        }

        private void NavigateToBookmark(MangaPageViewModel? vm)
            => NavigateToThumbnail(vm);

        private void NavigateToThumbnail(MangaPageViewModel? vm)
        {
            if (vm?.FilePath == null) return;
            int idx = GetThumbnailIndex(vm);
            if (idx < 0)
                idx = _mangaManager.FindImageIndexByPath(vm.FilePath);
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
            _ocrRunGate.Dispose();
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