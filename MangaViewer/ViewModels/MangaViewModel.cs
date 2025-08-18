using MangaViewer.Helpers;
using MangaViewer.Services;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using Windows.Storage.Pickers;
using Windows.Storage;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Threading;
using Microsoft.UI.Xaml.Controls; // InfoBarSeverity

namespace MangaViewer.ViewModels
{
    /// <summary>
    /// Main ViewModel controlling page navigation, image sources, and OCR overlay data.
    /// Keeps UI thread work minimal: heavy IO/OCR offloaded to services.
    /// </summary>
    public partial class MangaViewModel : BaseViewModel
    {
        private readonly MangaManager _mangaManager = new();
        private readonly ImageCacheService _imageCache = ImageCacheService.Instance;
        private readonly OcrService _ocrService = OcrService.Instance;

        private BitmapImage? _leftImageSource;
        private BitmapImage? _rightImageSource;
        private int _selectedThumbnailIndex = -1;
        private bool _isPaneOpen = true;
        private bool _isNavOpen = false; // navigation view pane starts closed
        private bool _isLoading;
        private bool _isSinglePageMode;
        private bool _isTwoPageMode;
        private bool _isOcrRunning;

        // Wrapper size logging (optional future adaptive layout)
        private double _leftWrapperWidth, _leftWrapperHeight;
        private double _rightWrapperWidth, _rightWrapperHeight;

        private CancellationTokenSource? _ocrCts;

        private string _ocrStatusMessage = string.Empty;
        private bool _isInfoBarOpen;
        private InfoBarSeverity _ocrSeverity = InfoBarSeverity.Informational;

        public ReadOnlyObservableCollection<MangaPageViewModel> Thumbnails => _mangaManager.Pages;

        public string? LeftImageFilePath { get; private set; }
        public string? RightImageFilePath { get; private set; }
        public BitmapImage? LeftImageSource { get => _leftImageSource; private set => SetProperty(ref _leftImageSource, value); }
        public BitmapImage? RightImageSource { get => _rightImageSource; private set => SetProperty(ref _rightImageSource, value); }
        public bool IsSinglePageMode { get => _isSinglePageMode; private set => SetProperty(ref _isSinglePageMode, value); }
        public bool IsTwoPageMode { get => _isTwoPageMode; private set => SetProperty(ref _isTwoPageMode, value); }
        public bool IsOcrRunning { get => _isOcrRunning; private set { if (SetProperty(ref _isOcrRunning, value)) RunOcrCommand.RaiseCanExecuteChanged(); } }
        public string OcrStatusMessage { get => _ocrStatusMessage; private set => SetProperty(ref _ocrStatusMessage, value); }
        public bool IsInfoBarOpen { get => _isInfoBarOpen; private set => SetProperty(ref _isInfoBarOpen, value); }
        public InfoBarSeverity OcrSeverity { get => _ocrSeverity; private set => SetProperty(ref _ocrSeverity, value); }

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
        public bool IsNavOpen { get => _isNavOpen; set => SetProperty(ref _isNavOpen, value); }
        public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) OnPropertyChanged(nameof(IsControlEnabled)); } }
        public bool IsControlEnabled => !IsLoading && !IsOcrRunning;

        public string DirectionButtonText => _mangaManager.IsRightToLeft ? "읽기 방향: 역방향" : "읽기 방향: 정방향";
        public string CoverButtonText => _mangaManager.IsCoverSeparate ? "표지: 한 장으로 보기" : "표지: 두 장으로 보기";

        public RelayCommand OpenFolderCommand { get; }
        public RelayCommand NextPageCommand { get; }
        public RelayCommand PrevPageCommand { get; }
        public RelayCommand ToggleDirectionCommand { get; }
        public RelayCommand ToggleCoverCommand { get; }
        public RelayCommand TogglePaneCommand { get; }
        public RelayCommand ToggleNavPaneCommand { get; }
        public RelayCommand GoLeftCommand { get; }
        public RelayCommand GoRightCommand { get; }
        public RelayCommand RunOcrCommand { get; }

        private readonly ObservableCollection<BoundingBoxViewModel> _leftOcrBoxes = new();
        private readonly ObservableCollection<BoundingBoxViewModel> _rightOcrBoxes = new();
        public ReadOnlyObservableCollection<BoundingBoxViewModel> LeftOcrBoxes { get; }
        public ReadOnlyObservableCollection<BoundingBoxViewModel> RightOcrBoxes { get; }

        private BoundingBoxViewModel? _selectedOcrBox;
        public BoundingBoxViewModel? SelectedOcrBox { get => _selectedOcrBox; set => SetProperty(ref _selectedOcrBox, value); }

        public event EventHandler? OcrCompleted;
        public event EventHandler? PageViewChanged;

        public MangaViewModel()
        {
            LeftOcrBoxes = new ReadOnlyObservableCollection<BoundingBoxViewModel>(_leftOcrBoxes);
            RightOcrBoxes = new ReadOnlyObservableCollection<BoundingBoxViewModel>(_rightOcrBoxes);

            _mangaManager.MangaLoaded += OnMangaLoaded;
            _mangaManager.PageChanged += OnPageChanged;

            OpenFolderCommand = new RelayCommand(async p => await OpenFolderAsync(p));
            NextPageCommand = new RelayCommand(_ => _mangaManager.GoToNextPage(), _ => _mangaManager.TotalImages > 0);
            PrevPageCommand = new RelayCommand(_ => _mangaManager.GoToPreviousPage(), _ => _mangaManager.TotalImages > 0);
            ToggleDirectionCommand = new RelayCommand(_ => { _mangaManager.ToggleDirection(); CancelOcr(); }, _ => _mangaManager.TotalImages > 0);
            ToggleCoverCommand = new RelayCommand(_ => { _mangaManager.ToggleCover(); CancelOcr(); }, _ => _mangaManager.TotalImages > 0);
            TogglePaneCommand = new RelayCommand(_ => IsPaneOpen = !IsPaneOpen);
            ToggleNavPaneCommand = new RelayCommand(_ => IsNavOpen = !IsNavOpen);
            GoLeftCommand = new RelayCommand(_ => NavigateLogicalLeft(), _ => _mangaManager.TotalImages > 0);
            GoRightCommand = new RelayCommand(_ => NavigateLogicalRight(), _ => _mangaManager.TotalImages > 0);
            RunOcrCommand = new RelayCommand(async _ => await RunOcrAsync(), _ => _mangaManager.TotalImages > 0 && !IsOcrRunning);
        }

        #region Folder/Page Loading
        private async Task OpenFolderAsync(object? windowHandle)
        {
            if (IsLoading || windowHandle is null) return;
            IsLoading = true;
            CancelOcr();
            try
            {
                var picker = new FolderPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, (IntPtr)windowHandle);
                picker.FileTypeFilter.Add("*");
                var folder = await picker.PickSingleFolderAsync();
                if (folder != null)
                {
                    _ocrService.ClearCache();
                    await _mangaManager.LoadFolderAsync(folder);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[Folder] Error: {ex.Message}"); }
            finally { IsLoading = false; }
        }

        private void OnMangaLoaded()
        {
            OnPropertyChanged(nameof(Thumbnails));
            UpdateCommandStates();
        }

        private void OnPageChanged()
        {
            CancelOcr();
            var paths = _mangaManager.GetImagePathsForCurrentPage();
            string? leftPath = paths.Count > 0 ? paths[0] : null;
            string? rightPath = paths.Count > 1 ? paths[1] : null;
            if (_mangaManager.IsRightToLeft && !(paths.Count == 1 && _mangaManager.CurrentPageIndex == 0))
                (leftPath, rightPath) = (rightPath, leftPath);

            LeftImageFilePath = leftPath;
            RightImageFilePath = rightPath;
            OnPropertyChanged(nameof(LeftImageFilePath));
            OnPropertyChanged(nameof(RightImageFilePath));

            LeftImageSource = !string.IsNullOrEmpty(leftPath) ? _imageCache.Get(leftPath) : null;
            RightImageSource = !string.IsNullOrEmpty(rightPath) ? _imageCache.Get(rightPath) : null;

            IsSinglePageMode = LeftImageSource != null && RightImageSource == null;
            IsTwoPageMode = LeftImageSource != null && RightImageSource != null;

            int primaryImageIndex = _mangaManager.GetPrimaryImageIndexForPage(_mangaManager.CurrentPageIndex);
            if (_selectedThumbnailIndex != primaryImageIndex)
                SelectedThumbnailIndex = primaryImageIndex;

            OnPropertyChanged(nameof(DirectionButtonText));
            OnPropertyChanged(nameof(CoverButtonText));

            TryPrefetchAhead();
            RunOcrCommand.RaiseCanExecuteChanged();
            ClearOcr();
            PageViewChanged?.Invoke(this, EventArgs.Empty);
        }
        #endregion

        #region Navigation Helpers
        private void TryPrefetchAhead()
        {
            try
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i <= 2; i++)
                {
                    int idx = _mangaManager.CurrentPageIndex + i;
                    if (idx >= _mangaManager.TotalPages) break;
                    foreach (var p in _mangaManager.GetImagePathsForPage(idx))
                        if (!string.IsNullOrEmpty(p)) set.Add(p);
                }
                if (set.Count > 0) _imageCache.Prefetch(set);
            }
            catch (Exception ex) { Debug.WriteLine($"[Prefetch] Error: {ex.Message}"); }
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
            NextPageCommand.RaiseCanExecuteChanged();
            PrevPageCommand.RaiseCanExecuteChanged();
            ToggleDirectionCommand.RaiseCanExecuteChanged();
            ToggleCoverCommand.RaiseCanExecuteChanged();
            GoLeftCommand.RaiseCanExecuteChanged();
            GoRightCommand.RaiseCanExecuteChanged();
            RunOcrCommand.RaiseCanExecuteChanged();
        }
        #endregion

        #region Wrapper Size (debug)
        public void UpdateLeftOcrContainerSize(double w, double h)
        {
            if (w <= 0 || h <= 0) return;
            if (Math.Abs(w - _leftWrapperWidth) > .5 || Math.Abs(h - _leftWrapperHeight) > .5)
            {
                _leftWrapperWidth = w; _leftWrapperHeight = h;
                Debug.WriteLine($"[Wrapper] Left {w}x{h}");
            }
        }
        public void UpdateRightOcrContainerSize(double w, double h)
        {
            if (w <= 0 || h <= 0) return;
            if (Math.Abs(w - _rightWrapperWidth) > .5 || Math.Abs(h - _rightWrapperHeight) > .5)
            {
                _rightWrapperWidth = w; _rightWrapperHeight = h;
                Debug.WriteLine($"[Wrapper] Right {w}x{h}");
            }
        }
        #endregion

        #region OCR
        private void SetOcrStatus(string message, InfoBarSeverity severity = InfoBarSeverity.Informational, bool open = true)
        {
            OcrStatusMessage = message;
            OcrSeverity = severity;
            IsInfoBarOpen = open;
        }

        private async Task RunOcrAsync()
        {
            if (IsOcrRunning) return;
            CancelOcr();
            var paths = _mangaManager.GetImagePathsForCurrentPage();
            if (paths.Count == 0) return;

            IsOcrRunning = true;
            OnPropertyChanged(nameof(IsControlEnabled));
            _ocrCts = new CancellationTokenSource();
            var token = _ocrCts.Token;
            try
            {
                ClearOcr();
                SetOcrStatus($"OCR 실행 중... ({paths.Count} images)", InfoBarSeverity.Informational, true);
                Debug.WriteLine($"[OCR] Start Page={_mangaManager.CurrentPageIndex} Images={paths.Count}");

                int totalBoxes = 0;
                for (int i = 0; i < paths.Count; i++)
                {
                    string p = paths[i];
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    token.ThrowIfCancellationRequested();
                    var boxes = await _ocrService.GetOcrAsync(p, token);
                    foreach (var b in boxes)
                    {
                        if (i == 0) _leftOcrBoxes.Add(b); else _rightOcrBoxes.Add(b);
                    }
                    totalBoxes += boxes.Count;
                    Debug.WriteLine($"[OCR] Image[{i}] '{p}' -> {boxes.Count} boxes");
                }
                SetOcrStatus($"OCR 완료: {totalBoxes} boxes", InfoBarSeverity.Success, true);
            }
            catch (OperationCanceledException)
            {
                SetOcrStatus("OCR 취소됨", InfoBarSeverity.Informational, true);
                Debug.WriteLine("[OCR] Canceled");
                ClearOcr();
            }
            catch (Exception ex)
            {
                SetOcrStatus("OCR 오류: " + ex.Message, InfoBarSeverity.Error, true);
                Debug.WriteLine($"[OCR] Error: {ex.Message}");
            }
            finally
            {
                IsOcrRunning = false;
                OnPropertyChanged(nameof(IsControlEnabled));
                // Log all OCR texts (left then right)
                try
                {
                    foreach (var b in _leftOcrBoxes) Debug.WriteLine("[OCR-Text-L] " + b.Text);
                    foreach (var b in _rightOcrBoxes) Debug.WriteLine("[OCR-Text-R] " + b.Text);
                }
                catch { }
                OcrCompleted?.Invoke(this, EventArgs.Empty);
                RunOcrCommand.RaiseCanExecuteChanged();
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
        #endregion

        #region Helpers
        private static async Task<(int width, int height)> GetImagePixelSizeAsync(string path)
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(path);
                using IRandomAccessStream s = await file.OpenAsync(FileAccessMode.Read);
                var dec = await BitmapDecoder.CreateAsync(s);
                return ((int)dec.PixelWidth, (int)dec.PixelHeight);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Image] GetSize fail: {ex.Message}");
                return (0, 0);
            }
        }

        private void ClearOcr()
        {
            _leftOcrBoxes.Clear();
            _rightOcrBoxes.Clear();
        }
        #endregion
    }
}