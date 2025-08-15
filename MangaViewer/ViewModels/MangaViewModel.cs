using MangaViewer.Helpers;
using MangaViewer.Services;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MangaViewer.ViewModels
{
    public class MangaViewModel : BaseViewModel
    {
        private readonly MangaManager _mangaManager = new();
        private readonly OcrService _ocrService = new();

        private BitmapImage? _leftImageSource;
        private BitmapImage? _rightImageSource;
        private int _selectedThumbnailIndex = -1;
        private bool _isPaneOpen = true;
        private bool _isLoading = false;
        private string _ocrButtonText = "텍스트 인식(OCR)";
        private string _notificationText = "";
        private bool _isNotificationVisible = false;
        private bool _isSinglePageMode;
        private bool _isTwoPageMode;

        public ReadOnlyObservableCollection<MangaPageViewModel> Thumbnails => _mangaManager.Pages;
        public ObservableCollection<BoundingBoxViewModel> LeftOcrResults { get; } = new();
        public ObservableCollection<BoundingBoxViewModel> RightOcrResults { get; } = new();

        private double _leftImageOriginalWidth;
        private double _leftImageOriginalHeight;
        private double _rightImageOriginalWidth;
        private double _rightImageOriginalHeight;

        public string? LeftImageFilePath { get; private set; }
        public string? RightImageFilePath { get; private set; }

        public BitmapImage? LeftImageSource { get => _leftImageSource; private set { _leftImageSource = value; OnPropertyChanged(); } }
        public BitmapImage? RightImageSource { get => _rightImageSource; private set { _rightImageSource = value; OnPropertyChanged(); } }
        public bool IsSinglePageMode { get => _isSinglePageMode; private set { _isSinglePageMode = value; OnPropertyChanged(); } }
        public bool IsTwoPageMode { get => _isTwoPageMode; private set { _isTwoPageMode = value; OnPropertyChanged(); } }

        public double LeftImageOriginalWidth { get => _leftImageOriginalWidth; private set { _leftImageOriginalWidth = value; OnPropertyChanged(); } }
        public double LeftImageOriginalHeight { get => _leftImageOriginalHeight; private set { _leftImageOriginalHeight = value; OnPropertyChanged(); } }
        public double RightImageOriginalWidth { get => _rightImageOriginalWidth; private set { _rightImageOriginalWidth = value; OnPropertyChanged(); } }
        public double RightImageOriginalHeight { get => _rightImageOriginalHeight; private set { _rightImageOriginalHeight = value; OnPropertyChanged(); } }

        public int SelectedThumbnailIndex
        {
            get => _selectedThumbnailIndex;
            set
            {
                if (_selectedThumbnailIndex != value)
                {
                    _selectedThumbnailIndex = value;
                    OnPropertyChanged();
                    if (value >= 0) _mangaManager.SetCurrentPageFromImageIndex(value);
                }
            }
        }

        public bool IsPaneOpen { get => _isPaneOpen; set { _isPaneOpen = value; OnPropertyChanged(); } }
        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (_isLoading != value)
                {
                    _isLoading = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsControlEnabled));
                    ((RelayCommand)RunOcrCommand).RaiseCanExecuteChanged();
                }
            }
        }
        public bool IsControlEnabled => !IsLoading;
        public string OcrButtonText { get => _ocrButtonText; private set { _ocrButtonText = value; OnPropertyChanged(); } }
        public string DirectionButtonText => _mangaManager.IsRightToLeft ? "읽기 방향: 역방향" : "읽기 방향: 정방향";
        public string CoverButtonText => _mangaManager.IsCoverSeparate ? "표지: 한 장으로 보기" : "표지: 두 장으로 보기";
        public string NotificationText { get => _notificationText; private set { _notificationText = value; OnPropertyChanged(); } }
        public bool IsNotificationVisible { get => _isNotificationVisible; private set { _isNotificationVisible = value; OnPropertyChanged(); } }

        public int OcrResultsCount => LeftOcrResults.Count + RightOcrResults.Count;

        public RelayCommand OpenFolderCommand { get; }
        public RelayCommand NextPageCommand { get; }
        public RelayCommand PrevPageCommand { get; }
        public RelayCommand ToggleDirectionCommand { get; }
        public RelayCommand ToggleCoverCommand { get; }
        public RelayCommand RunOcrCommand { get; }
        public RelayCommand ClearOcrCommand { get; }
        public RelayCommand TogglePaneCommand { get; }
        public RelayCommand GoLeftCommand { get; }
        public RelayCommand GoRightCommand { get; }

        public MangaViewModel()
        {
            _mangaManager.MangaLoaded += OnMangaLoaded;
            _mangaManager.PageChanged += OnPageChanged;

            OpenFolderCommand = new RelayCommand(async (p) => await OpenFolderAsync(p));
            NextPageCommand = new RelayCommand((_) => _mangaManager.GoToNextPage(), (_) => _mangaManager.TotalImages > 0);
            PrevPageCommand = new RelayCommand((_) => _mangaManager.GoToPreviousPage(), (_) => _mangaManager.TotalImages > 0);
            ToggleDirectionCommand = new RelayCommand((_) => _mangaManager.ToggleDirection(), (_) => _mangaManager.TotalImages > 0);
            ToggleCoverCommand = new RelayCommand((_) => _mangaManager.ToggleCover(), (_) => _mangaManager.TotalImages > 0);
            RunOcrCommand = new RelayCommand(async (_) => await RunOcrAsync(), (_) => !IsLoading && _mangaManager.TotalImages > 0);
            ClearOcrCommand = new RelayCommand((_) => ClearOcr(), (_) => OcrResultsCount > 0);
            TogglePaneCommand = new RelayCommand((_) => IsPaneOpen = !IsPaneOpen);
            GoLeftCommand = new RelayCommand((_) => { if (_mangaManager.IsRightToLeft) NextPageCommand.Execute(null); else PrevPageCommand.Execute(null); }, (_) => _mangaManager.TotalImages > 0);
            GoRightCommand = new RelayCommand((_) => { if (_mangaManager.IsRightToLeft) PrevPageCommand.Execute(null); else NextPageCommand.Execute(null); }, (_) => _mangaManager.TotalImages > 0);
        }

        private async Task OpenFolderAsync(object? windowHandle)
        {
            if (IsLoading || windowHandle == null) return;
            IsLoading = true;
            try
            {
                var folderPicker = new FolderPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, (IntPtr)windowHandle);
                folderPicker.FileTypeFilter.Add("*");
                StorageFolder folder = await folderPicker.PickSingleFolderAsync();
                if (folder != null) { await _mangaManager.LoadFolderAsync(folder); }
            }
            catch (Exception ex) { Debug.WriteLine($"Error opening folder: {ex.Message}"); }
            finally { IsLoading = false; }
        }

        private async Task RunOcrAsync()
        {
            if (IsLoading) return;

            IsLoading = true;
            OcrButtonText = "인식 중...";
            ClearOcr();

            try
            {
                // Process Left Image
                if (!string.IsNullOrEmpty(LeftImageFilePath))
                {
                    StorageFile leftImageFile = await StorageFile.GetFileFromPathAsync(LeftImageFilePath);
                    var leftOcrResults = await _ocrService.RecognizeAsync(leftImageFile);
                    foreach (var result in leftOcrResults)
                    {
                        LeftOcrResults.Add(new BoundingBoxViewModel(result.Text, result.BoundingBox));
                    }
                }

                // Process Right Image
                if (!string.IsNullOrEmpty(RightImageFilePath))
                {
                    StorageFile rightImageFile = await StorageFile.GetFileFromPathAsync(RightImageFilePath);
                    var rightOcrResults = await _ocrService.RecognizeAsync(rightImageFile);
                    foreach (var result in rightOcrResults)
                    {
                        RightOcrResults.Add(new BoundingBoxViewModel(result.Text, result.BoundingBox));
                    }
                }

                // Initial scaling after OCR results are loaded
                // Note: Actual display sizes will be passed from UI's SizeChanged event
                // For initial display, we might need a default size or rely on the UI to trigger UpdateOcrScales.
                // This will be handled by the SizeChanged event in MainWindow.xaml.cs
            }
            catch (Exception ex)
            {
                ShowNotification($"OCR 오류: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
                OcrButtonText = "텍스트 인식(OCR)";
                OnPropertyChanged(nameof(OcrResultsCount));
                ((RelayCommand)ClearOcrCommand).RaiseCanExecuteChanged();
            }
        }

        public void UpdateOcrScales(double leftScaleX, double leftScaleY, double rightScaleX, double rightScaleY)
        {
            // Update Left OCR Results
            foreach (var boundingBoxVm in LeftOcrResults)
            {
                boundingBoxVm.UpdatePosition(leftScaleX, leftScaleY);
            }

            // Update Right OCR Results
            foreach (var boundingBoxVm in RightOcrResults)
            {
                boundingBoxVm.UpdatePosition(rightScaleX, rightScaleY);
            }
        }

        private void ClearOcr()
        {
            LeftOcrResults.Clear();
            RightOcrResults.Clear();
            OnPropertyChanged(nameof(OcrResultsCount));
            ((RelayCommand)ClearOcrCommand).RaiseCanExecuteChanged();
        }

        private async void ShowNotification(string message)
        {
            NotificationText = message;
            IsNotificationVisible = true;
            await Task.Delay(2000);
            IsNotificationVisible = false;
        }

        private void OnMangaLoaded()
        {
            OnPropertyChanged(nameof(Thumbnails));
            ((RelayCommand)NextPageCommand).RaiseCanExecuteChanged();
            ((RelayCommand)PrevPageCommand).RaiseCanExecuteChanged();
            ((RelayCommand)ToggleDirectionCommand).RaiseCanExecuteChanged();
            ((RelayCommand)ToggleCoverCommand).RaiseCanExecuteChanged();
            ((RelayCommand)RunOcrCommand).RaiseCanExecuteChanged();
            ((RelayCommand)GoLeftCommand).RaiseCanExecuteChanged();
            ((RelayCommand)GoRightCommand).RaiseCanExecuteChanged();
        }

        private void OnPageChanged()
        {
            var paths = _mangaManager.GetImagePathsForCurrentPage();
            string? leftPath = paths.Count > 0 ? paths[0] : null;
            string? rightPath = paths.Count > 1 ? paths[1] : null;

            // Handle single cover page in right-to-left mode
            if (_mangaManager.IsRightToLeft && paths.Count == 1 && _mangaManager.CurrentPageIndex == 0)
            {
                // For a single cover in RTL, it should appear on the left.
                // The paths are already correctly assigned (leftPath = cover, rightPath = null)
                // so no swap is needed for this specific case.
            }
            else if (_mangaManager.IsRightToLeft)
            {
                // For other RTL cases (two pages or not the cover), swap as usual.
                (leftPath, rightPath) = (rightPath, leftPath);
            }

            LeftImageFilePath = leftPath;
            RightImageFilePath = rightPath;
            OnPropertyChanged(nameof(LeftImageFilePath));
            OnPropertyChanged(nameof(RightImageFilePath));

            LeftImageSource = !string.IsNullOrEmpty(leftPath) ? new BitmapImage(new Uri(leftPath)) : null;
            RightImageSource = !string.IsNullOrEmpty(rightPath) ? new BitmapImage(new Uri(rightPath)) : null;

            if (LeftImageSource != null)
            {
                LeftImageOriginalWidth = LeftImageSource.PixelWidth;
                LeftImageOriginalHeight = LeftImageSource.PixelHeight;
            }
            else
            {
                LeftImageOriginalWidth = 0;
                LeftImageOriginalHeight = 0;
            }

            if (RightImageSource != null)
            {
                RightImageOriginalWidth = RightImageSource.PixelWidth;
                RightImageOriginalHeight = RightImageSource.PixelHeight;
            }
            else
            {
                RightImageOriginalWidth = 0;
                RightImageOriginalHeight = 0;
            }

            IsSinglePageMode = LeftImageSource != null && RightImageSource == null;
            IsTwoPageMode = LeftImageSource != null && RightImageSource != null;

            int primaryImageIndex = _mangaManager.GetPrimaryImageIndexForPage(_mangaManager.CurrentPageIndex);
            if (_selectedThumbnailIndex != primaryImageIndex) { SelectedThumbnailIndex = primaryImageIndex; }

            ClearOcr(); // Clear OCR results when page changes
            OnPropertyChanged(nameof(DirectionButtonText));
            OnPropertyChanged(nameof(CoverButtonText));
        }
    }
}