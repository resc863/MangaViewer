using MangaViewer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media; // VisualTreeHelper
using MangaViewer.Services.Thumbnails; // moved thumbnail services

namespace MangaViewer.Pages
{
    public sealed partial class SettingsPage : Page
    {
        private readonly OcrService _ocr = OcrService.Instance;
        private readonly TagSettingsService _tagSettings = TagSettingsService.Instance;
        private readonly ThumbnailSettingsService _thumbSettings = ThumbnailSettingsService.Instance;
        private readonly LibraryService _libraryService = new();
        
        private ComboBox _langCombo = null!;
        private ComboBox _groupCombo = null!;
        private ComboBox _writingCombo = null!;
        private ComboBox _ocrBackendCombo = null!;
        private TextBox _ollamaEndpointBox = null!;
        private StackPanel _ollamaSettingsPanel = null!;
        private Slider _tagFontSlider = null!;
        private TextBlock _tagFontValue = null!;

        private Slider _thumbWidthSlider = null!;
        private TextBlock _thumbWidthValue = null!;

        private ListView _cacheList = null!;
        private TextBlock _cacheSummary = null!;
        private NumberBox _cacheMaxCountBox = null!;
        private NumberBox _cacheMaxBytesBox = null!;
        private ObservableCollection<CacheEntryView> _cacheEntries = new();

        private ListView _libraryPathsList = null!;
        private ObservableCollection<string> _libraryPaths = new();

        private sealed class CacheEntryView { public string GalleryId = string.Empty; public int Count; }

        public SettingsPage()
        {
            BuildUi();
            Loaded += SettingsPage_Loaded;
            Unloaded += SettingsPage_Unloaded;
            // Removed direct OCR auto-run here to avoid duplicate with MangaViewModel subscription
            _ocr.SettingsChanged += Ocr_SettingsChanged; // keep for potential UI sync only
            _thumbSettings.SettingsChanged += Thumb_SettingsChanged;
        }

        private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _ocr.SettingsChanged -= Ocr_SettingsChanged;
            _thumbSettings.SettingsChanged -= Thumb_SettingsChanged;
        }

        private void Thumb_SettingsChanged(object? sender, EventArgs e)
        {
            // Clear decoded thumbnail cache so new settings apply progressively
            ThumbnailCacheService.Instance.Clear();
        }

        private void Ocr_SettingsChanged(object? sender, EventArgs e)
        {
            // Only sync UI selections; OCR rerun handled in MangaViewModel
            if (!IsLoaded) return;
            // sync combos if external change occurred
            int langIndex = 0;
            if (string.Equals(_ocr.CurrentLanguage, "ja", StringComparison.OrdinalIgnoreCase)) langIndex = 1;
            else if (string.Equals(_ocr.CurrentLanguage, "ko", StringComparison.OrdinalIgnoreCase)) langIndex = 2;
            else if (string.Equals(_ocr.CurrentLanguage, "en", StringComparison.OrdinalIgnoreCase)) langIndex = 3;
            if (_langCombo.SelectedIndex != langIndex) _langCombo.SelectedIndex = langIndex;
            if (_groupCombo.SelectedIndex != (int)_ocr.GroupingMode) _groupCombo.SelectedIndex = (int)_ocr.GroupingMode;
            if (_writingCombo.SelectedIndex != (int)_ocr.TextWritingMode) _writingCombo.SelectedIndex = (int)_ocr.TextWritingMode;
            if (_ocrBackendCombo.SelectedIndex != (int)_ocr.Backend) _ocrBackendCombo.SelectedIndex = (int)_ocr.Backend;
            UpdateOllamaSettingsVisibility();
        }

        private void BuildUi()
        {
            var stack = new StackPanel { Spacing = 18, Padding = new Thickness(24) };
            
            // Library section
            stack.Children.Add(new TextBlock { Text = "만화 라이브러리", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            
            var libraryBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var addLibBtn = new Button { Content = "라이브러리 폴더 추가" }; 
            addLibBtn.Click += AddLibraryFolder_Click;
            libraryBtnRow.Children.Add(addLibBtn);
            stack.Children.Add(libraryBtnRow);
            
            _libraryPaths = new ObservableCollection<string>();
            _libraryPathsList = new ListView { Height = 160, SelectionMode = ListViewSelectionMode.Single, ItemsSource = _libraryPaths };
            _libraryPathsList.ItemTemplate = (DataTemplate)XamlReader.Load(@"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
<Grid Margin='0,4,0,4' ColumnDefinitions='*,Auto,Auto,Auto'>
<TextBlock VerticalAlignment='Center' Text='{Binding}' TextTrimming='CharacterEllipsis'/>
<Button Content='↑' Padding='8,4,8,4' Grid.Column='1' Margin='4,0,0,0'/>
<Button Content='↓' Padding='8,4,8,4' Grid.Column='2' Margin='4,0,0,0'/>
<Button Content='제거' Padding='12,4,12,4' Grid.Column='3' Margin='8,0,0,0'/>
</Grid></DataTemplate>");
            _libraryPathsList.ContainerContentChanging += LibraryPathsList_ContainerContentChanging;
            stack.Children.Add(_libraryPathsList);
            
            stack.Children.Add(new TextBlock { Text = "OCR 설정", FontSize = 20, Margin = new Thickness(0,24,0,0), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

            _ocrBackendCombo = new ComboBox { Width = 220 };
            _ocrBackendCombo.Items.Add(new ComboBoxItem { Content = "Windows 내장 OCR", Tag = "builtin" });
            _ocrBackendCombo.Items.Add(new ComboBoxItem { Content = "Ollama (glm-ocr)", Tag = "ollama" });
            _ocrBackendCombo.SelectionChanged += OcrBackendCombo_SelectionChanged;
            stack.Children.Add(Row("OCR 엔진:", _ocrBackendCombo));

            _ollamaEndpointBox = new TextBox { Width = 260, PlaceholderText = "http://localhost:11434" };
            _ollamaEndpointBox.LostFocus += OllamaEndpointBox_LostFocus;
            _ollamaSettingsPanel = new StackPanel { Spacing = 8 };
            _ollamaSettingsPanel.Children.Add(Row("Ollama 주소:", _ollamaEndpointBox));
            stack.Children.Add(_ollamaSettingsPanel);

            _langCombo = new ComboBox { Width = 160 };
            _langCombo.Items.Add(new ComboBoxItem { Content = "자동", Tag = "auto" });
            _langCombo.Items.Add(new ComboBoxItem { Content = "일본어", Tag = "ja" });
            _langCombo.Items.Add(new ComboBoxItem { Content = "한국어", Tag = "ko" });
            _langCombo.Items.Add(new ComboBoxItem { Content = "영어", Tag = "en" });
            _langCombo.SelectionChanged += LangCombo_SelectionChanged;
            stack.Children.Add(Row("언어:", _langCombo));

            _groupCombo = new ComboBox { Width = 160 };
            _groupCombo.Items.Add(new ComboBoxItem { Content = "단어", Tag = OcrService.OcrGrouping.Word.ToString() });
            _groupCombo.Items.Add(new ComboBoxItem { Content = "줄", Tag = OcrService.OcrGrouping.Line.ToString() });
            _groupCombo.Items.Add(new ComboBoxItem { Content = "문단", Tag = OcrService.OcrGrouping.Paragraph.ToString() });
            _groupCombo.SelectionChanged += GroupCombo_SelectionChanged;
            stack.Children.Add(Row("그룹:", _groupCombo));

            _writingCombo = new ComboBox { Width = 160 };
            _writingCombo.Items.Add(new ComboBoxItem { Content = "자동", Tag = OcrService.WritingMode.Auto.ToString() });
            _writingCombo.Items.Add(new ComboBoxItem { Content = "가로", Tag = OcrService.WritingMode.Horizontal.ToString() });
            _writingCombo.Items.Add(new ComboBoxItem { Content = "세로", Tag = OcrService.WritingMode.Vertical.ToString() });
            _writingCombo.SelectionChanged += WritingCombo_SelectionChanged;
            stack.Children.Add(Row("텍스트 방향:", _writingCombo));

            // Paragraph gap control (still part of OCR section)
            stack.Children.Add(new Controls.ParagraphGapSliderControl());

            // Translation section header
            stack.Children.Add(new TextBlock { Text = "번역 설정", FontSize = 20, Margin = new Thickness(0, 24, 0, 0), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

            var translationProviderCombo = new ComboBox { Width = 160 };
            translationProviderCombo.Items.Add(new ComboBoxItem { Content = "Google", Tag = "Google" });
            translationProviderCombo.Items.Add(new ComboBoxItem { Content = "OpenAI", Tag = "OpenAI" });
            translationProviderCombo.Items.Add(new ComboBoxItem { Content = "Anthropic", Tag = "Anthropic" });

            var translationApiKeyBox = new PasswordBox { Width = 260 };
            var translationSettings = TranslationSettingsService.Instance;

            // OpenAI / Anthropic 용 모델 텍스트 입력
            var translationModelBox = new TextBox { Width = 260 };

            // Google 전용 모델 콤보 + 목록 가져오기 버튼
            var googleModelCombo = new ComboBox { Width = 200, PlaceholderText = "모델을 선택하세요" };
            var fetchGoogleModelsBtn = new Button { Content = "목록 가져오기", Margin = new Thickness(8, 0, 0, 0) };
            var googleModelStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Opacity = 0.6, FontSize = 12 };
            var googleModelInner = new StackPanel { Orientation = Orientation.Horizontal };
            googleModelInner.Children.Add(googleModelCombo);
            googleModelInner.Children.Add(fetchGoogleModelsBtn);
            googleModelInner.Children.Add(googleModelStatus);

            var textModelRow = Row("모델:", translationModelBox);
            var googleModelRow = Row("모델:", googleModelInner);

            // 저장된 Google 모델 초기값 로드
            if (translationSettings.Provider == "Google" && !string.IsNullOrEmpty(translationSettings.Model))
            {
                var savedId = translationSettings.Model;
                googleModelCombo.Items.Add(new ComboBoxItem { Content = savedId, Tag = savedId });
                googleModelCombo.SelectedIndex = 0;
            }

            Action<string> updateModelRowVisibility = (provider) =>
            {
                textModelRow.Visibility = provider == "Google" ? Visibility.Collapsed : Visibility.Visible;
                googleModelRow.Visibility = provider == "Google" ? Visibility.Visible : Visibility.Collapsed;
            };

            Action updateApiKeyBox = () =>
            {
                var provider = (string)((ComboBoxItem)translationProviderCombo.SelectedItem).Tag;
                translationApiKeyBox.Password = provider switch
                {
                    "Google" => translationSettings.GoogleApiKey,
                    "OpenAI" => translationSettings.OpenAIApiKey,
                    "Anthropic" => translationSettings.AnthropicApiKey,
                    _ => ""
                };
            };

            // 초기값 설정
            var currentProvider = translationSettings.Provider;
            var providerItem = translationProviderCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == currentProvider)
                               ?? (ComboBoxItem)translationProviderCombo.Items[0];
            translationProviderCombo.SelectedItem = providerItem;
            translationModelBox.Text = translationSettings.Model;
            updateApiKeyBox();
            updateModelRowVisibility(currentProvider);

            translationProviderCombo.SelectionChanged += (s, e) =>
            {
                var provider = (string)((ComboBoxItem)translationProviderCombo.SelectedItem).Tag;
                translationSettings.Provider = provider;

                if (provider == "OpenAI" && (translationModelBox.Text == "gemini-2.0-flash" || translationModelBox.Text == "claude-3-5-sonnet-20240620"))
                    translationModelBox.Text = "gpt-4o";
                else if (provider == "Anthropic" && (translationModelBox.Text == "gemini-2.0-flash" || translationModelBox.Text == "gpt-4o"))
                    translationModelBox.Text = "claude-3-5-sonnet-20240620";

                if (provider != "Google")
                    translationSettings.Model = translationModelBox.Text;

                updateModelRowVisibility(provider);
                updateApiKeyBox();
            };

            translationModelBox.LostFocus += (s, e) => translationSettings.Model = translationModelBox.Text;

            googleModelCombo.SelectionChanged += (s, e) =>
            {
                if (googleModelCombo.SelectedItem is ComboBoxItem item)
                    translationSettings.Model = (string)item.Tag;
            };

            fetchGoogleModelsBtn.Click += async (s, e) =>
            {
                // API 키 우선 저장
                translationSettings.GoogleApiKey = translationApiKeyBox.Password;

                if (string.IsNullOrWhiteSpace(translationSettings.GoogleApiKey))
                {
                    googleModelStatus.Text = "API 키를 먼저 입력하세요";
                    return;
                }

                fetchGoogleModelsBtn.IsEnabled = false;
                googleModelStatus.Text = "가져오는 중...";

                try
                {
                    var savedSelection = googleModelCombo.SelectedItem is ComboBoxItem sel
                        ? (string)sel.Tag
                        : translationSettings.Model;

                    googleModelCombo.Items.Clear();

                    var gClient = new Google.GenAI.Client(apiKey: translationSettings.GoogleApiKey);
                    var pager = await gClient.Models.ListAsync();

                    var modelIds = new List<string>();
                    await foreach (var model in pager)
                    {
                        if (model.Name is not null)
                        {
                            // Name 형식: "models/gemini-2.0-flash" → prefix 제거
                            var id = model.Name.StartsWith("models/") ? model.Name[7..] : model.Name;
                            modelIds.Add(id);
                        }
                    }

                    // Gemini 모델 우선, 이후 가나다순
                    modelIds.Sort((a, b) =>
                    {
                        bool aG = a.StartsWith("gemini", StringComparison.OrdinalIgnoreCase);
                        bool bG = b.StartsWith("gemini", StringComparison.OrdinalIgnoreCase);
                        if (aG != bG) return aG ? -1 : 1;
                        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                    });

                    foreach (var id in modelIds)
                        googleModelCombo.Items.Add(new ComboBoxItem { Content = id, Tag = id });

                    // 이전 선택 복원
                    var toSelect = googleModelCombo.Items.OfType<ComboBoxItem>()
                        .FirstOrDefault(i => (string)i.Tag == savedSelection);
                    if (toSelect is not null)
                        googleModelCombo.SelectedItem = toSelect;
                    else if (googleModelCombo.Items.Count > 0)
                        googleModelCombo.SelectedIndex = 0;

                    googleModelStatus.Text = $"{modelIds.Count}개";
                }
                catch (Exception ex)
                {
                    googleModelStatus.Text = "오류: " + ex.Message;
                }
                finally
                {
                    fetchGoogleModelsBtn.IsEnabled = true;
                }
            };

            translationApiKeyBox.LostFocus += (s, e) =>
            {
                var provider = (string)((ComboBoxItem)translationProviderCombo.SelectedItem).Tag;
                switch (provider)
                {
                    case "Google": translationSettings.GoogleApiKey = translationApiKeyBox.Password; break;
                    case "OpenAI": translationSettings.OpenAIApiKey = translationApiKeyBox.Password; break;
                    case "Anthropic": translationSettings.AnthropicApiKey = translationApiKeyBox.Password; break;
                }
            };

            stack.Children.Add(Row("공급자:", translationProviderCombo));
            stack.Children.Add(textModelRow);
            stack.Children.Add(googleModelRow);
            stack.Children.Add(Row("API 키:", translationApiKeyBox));

            // Tag section header (separate grouping from OCR settings)
            stack.Children.Add(new TextBlock { Text = "태그 표시", FontSize = 20, Margin = new Thickness(0, 24, 0, 0), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

            _tagFontSlider = new Slider { Minimum = 8, Maximum = 32, Width = 220, Value = _tagSettings.TagFontSize };
            _tagFontSlider.ValueChanged += TagFontSlider_ValueChanged;
            _tagFontValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            UpdateTagFontValue();
            var fontRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            fontRow.Children.Add(new TextBlock { Text = "태그 폰트 크기:", VerticalAlignment = VerticalAlignment.Center });
            fontRow.Children.Add(_tagFontSlider);
            fontRow.Children.Add(_tagFontValue);
            stack.Children.Add(fontRow);

            // Thumbnail section header
            stack.Children.Add(new TextBlock { Text = "썸네일 설정", FontSize = 20, Margin = new Thickness(0,24,0,0), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var thumbRow1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            _thumbWidthSlider = new Slider { Minimum = 64, Maximum = 512, Width = 220, Value = _thumbSettings.DecodeWidth };
            _thumbWidthSlider.ValueChanged += ThumbWidthSlider_ValueChanged;
            _thumbWidthValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            UpdateThumbWidthValue();
            thumbRow1.Children.Add(new TextBlock { Text = "디코드 폭(px):", VerticalAlignment = VerticalAlignment.Center });
            thumbRow1.Children.Add(_thumbWidthSlider);
            thumbRow1.Children.Add(_thumbWidthValue);
            stack.Children.Add(thumbRow1);

            stack.Children.Add(new TextBlock { Text = "이미지 캐시", FontSize = 20, Margin = new Thickness(0,24,0,0), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            _cacheSummary = new TextBlock { Text = string.Empty, Margin = new Thickness(0,0,0,8) };
            stack.Children.Add(_cacheSummary);

            var limitsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            _cacheMaxCountBox = new NumberBox { Header = "최대 이미지 수", Width = 140, Minimum = 100, Maximum = 50000, Value = ImageCacheService.Instance.MaxMemoryImageCount };
            _cacheMaxBytesBox = new NumberBox { Header = "최대 용량(GB)", Width = 140, Minimum = 1, Maximum = 32, Value = Math.Round(ImageCacheService.Instance.MaxMemoryImageBytes / 1024d / 1024d / 1024d) };
            var applyBtn = new Button { Content = "적용" }; applyBtn.Click += ApplyCacheLimit_Click;
            limitsPanel.Children.Add(_cacheMaxCountBox);
            limitsPanel.Children.Add(_cacheMaxBytesBox);
            limitsPanel.Children.Add(applyBtn);
            stack.Children.Add(limitsPanel);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var refreshBtn = new Button { Content = "새로고침" }; refreshBtn.Click += (s,e)=> RefreshCacheView();
            var clearAllBtn = new Button { Content = "캐시 전체 삭제" }; clearAllBtn.Click += (s,e)=> { ImageCacheService.Instance.ClearMemoryImages(); RefreshCacheView(); };
            btnRow.Children.Add(refreshBtn);
            btnRow.Children.Add(clearAllBtn);
            stack.Children.Add(btnRow);

            _cacheEntries = new ObservableCollection<CacheEntryView>();
            _cacheList = new ListView { Height = 280, SelectionMode = ListViewSelectionMode.Single, ItemsSource = _cacheEntries };
            _cacheList.ItemTemplate = (DataTemplate)XamlReader.Load(@"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
<Grid Margin='0,4,0,4' ColumnDefinitions='*,Auto,Auto'>
<TextBlock VerticalAlignment='Center' FontFamily='Consolas' Text='{Binding GalleryId}'/>
<TextBlock VerticalAlignment='Center' Margin='12,0,12,0' Text='{Binding Count}' Grid.Column='1'/>
<Button Content='삭제' Padding='12,4,12,4' Grid.Column='2'/>
</Grid></DataTemplate>");
            _cacheList.ContainerContentChanging += CacheList_ContainerContentChanging;
            stack.Children.Add(_cacheList);

            Content = new ScrollViewer { Content = stack };
        }

        private void RefreshLibraryPaths()
        {
            _libraryPaths.Clear();
            var paths = _libraryService.GetLibraryPaths();
            foreach (var path in paths)
                _libraryPaths.Add(path);
        }

        /// <summary>
        /// Add a new library folder using Windows App SDK 1.8+ FolderPicker.
        /// Uses WindowId approach (no InitializeWithWindow interop needed).
        /// </summary>
        private async void AddLibraryFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var app = Application.Current as App;
                var window = app?.MainWindow;
                if (window == null) return;
                
                // Windows App SDK 1.8+ approach: use WindowId directly
                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
                var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(windowId);
                
                // Show picker and get result as PickFolderResult (returns path, not StorageFolder)
                var folder = await picker.PickSingleFolderAsync();
                
                if (folder != null)
                {
                    await _libraryService.AddLibraryPathAsync(folder.Path);
                    RefreshLibraryPaths();
                    
                    if (MainWindow.RootViewModel?.LibraryViewModel != null)
                    {
                        await MainWindow.RootViewModel.LibraryViewModel.RefreshLibraryAsync();
                    }
                }
            }
            catch { }
        }

        private async void RemovePathBtn_Click(object sender, RoutedEventArgs e)
        {
            // Remove selected library path
            if (_libraryPathsList.SelectedItem is string path)
            {
                await _libraryService.RemoveLibraryPathAsync(path);
                RefreshLibraryPaths();
                
                if (MainWindow.RootViewModel?.LibraryViewModel != null)
                {
                    await MainWindow.RootViewModel.LibraryViewModel.RefreshLibraryAsync();
                }
            }
        }

        private void LibraryPathsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.ItemContainer is ListViewItem lvi && args.Item is string path)
            {
                if (args.InRecycleQueue) return;
                if (args.Phase == 0)
                {
                    lvi.Loaded += (s,e)=>
                    {
                        var buttons = FindDescendants<Button>(lvi).ToList();
                        if (buttons.Count >= 3)
                        {
                            buttons[0].Click -= LibraryPathUp_Click;
                            buttons[0].Click += LibraryPathUp_Click;
                            
                            buttons[1].Click -= LibraryPathDown_Click;
                            buttons[1].Click += LibraryPathDown_Click;
                            
                            buttons[2].Click -= LibraryPathRemove_Click;
                            buttons[2].Click += LibraryPathRemove_Click;
                        }
                    };
                }
            }
        }

        private async void LibraryPathUp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.DataContext is string path)
            {
                var paths = _libraryService.GetLibraryPaths();
                int index = paths.IndexOf(path);
                if (index > 0)
                {
                    await _libraryService.MoveLibraryPathAsync(index, index - 1);
                    RefreshLibraryPaths();
                    if (MainWindow.RootViewModel?.LibraryViewModel != null)
                        await MainWindow.RootViewModel.LibraryViewModel.RefreshLibraryAsync();
                }
            }
        }

        private async void LibraryPathDown_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.DataContext is string path)
            {
                var paths = _libraryService.GetLibraryPaths();
                int index = paths.IndexOf(path);
                if (index >= 0 && index < paths.Count - 1)
                {
                    await _libraryService.MoveLibraryPathAsync(index, index + 1);
                    RefreshLibraryPaths();
                    if (MainWindow.RootViewModel?.LibraryViewModel != null)
                        await MainWindow.RootViewModel.LibraryViewModel.RefreshLibraryAsync();
                }
            }
        }

        private async void LibraryPathRemove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.DataContext is string path)
            {
                await _libraryService.RemoveLibraryPathAsync(path);
                RefreshLibraryPaths();
                if (MainWindow.RootViewModel?.LibraryViewModel != null)
                    await MainWindow.RootViewModel.LibraryViewModel.RefreshLibraryAsync();
            }
        }

        private void ThumbWidthSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            _thumbSettings.DecodeWidth = (int)Math.Round(e.NewValue);
            UpdateThumbWidthValue();
        }

        private void UpdateThumbWidthValue() => _thumbWidthValue.Text = _thumbSettings.DecodeWidth.ToString();

        private void CacheList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.ItemContainer is ListViewItem lvi && args.Item is CacheEntryView view)
            {
                if (args.InRecycleQueue) return;
                if (args.Phase == 0)
                {
                    lvi.Loaded += (s,e)=>
                    {
                        var btn = FindDescendant<Button>(lvi);
                        if (btn != null)
                        {
                            btn.Click -= CacheItemDelete_Click;
                            btn.Click += CacheItemDelete_Click;
                        }
                    };
                }
            }
        }

        private void CacheItemDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.DataContext is CacheEntryView v)
            {
                ImageCacheService.Instance.ClearGalleryMemory(v.GalleryId);
                RefreshCacheView();
            }
        }

        private static T? FindDescendant<T>(DependencyObject root) where T: DependencyObject
        {
            if (root == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t) return t;
                var r = FindDescendant<T>(child);
                if (r != null) return r;
            }
            return null;
        }

        private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T: DependencyObject
        {
            if (root == null) yield break;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t) yield return t;
                foreach (var desc in FindDescendants<T>(child))
                    yield return desc;
            }
        }

        private void ApplyCacheLimit_Click(object sender, RoutedEventArgs e)
        {
            int newCount = (int)_cacheMaxCountBox.Value;
            long newBytes = (long)(_cacheMaxBytesBox.Value * 1024 * 1024 * 1024); // GB -> bytes
            ImageCacheService.Instance.SetMemoryLimits(newCount, newBytes);
            RefreshCacheView();
        }

        private void RefreshCacheView()
        {
            _cacheEntries.Clear();
            var per = ImageCacheService.Instance.GetPerGalleryCounts().OrderByDescending(k=>k.Value).ToList();
            foreach (var kv in per) _cacheEntries.Add(new CacheEntryView { GalleryId = kv.Key, Count = kv.Value });
            var (cnt, bytes) = ImageCacheService.Instance.GetMemoryUsage();
            _cacheSummary.Text = $"합계: {cnt} images, {(bytes/1024d/1024d):F1} MB";
        }

        private void UpdateTagFontValue() => _tagFontValue.Text = Math.Round(_tagFontSlider.Value).ToString();

        private static UIElement Row(string label, UIElement inner)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            p.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            p.Children.Add(inner);
            return p;
        }

        private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Load library paths
            RefreshLibraryPaths();
            
            // Language
            int langIndex = 0; // default auto
            if (string.Equals(_ocr.CurrentLanguage, "ja", StringComparison.OrdinalIgnoreCase)) langIndex = 1;
            else if (string.Equals(_ocr.CurrentLanguage, "ko", StringComparison.OrdinalIgnoreCase)) langIndex = 2;
            else if (string.Equals(_ocr.CurrentLanguage, "en", StringComparison.OrdinalIgnoreCase)) langIndex = 3;
            _langCombo.SelectedIndex = langIndex;

            _groupCombo.SelectedIndex = (int)_ocr.GroupingMode;
            _writingCombo.SelectedIndex = (int)_ocr.TextWritingMode;
            _ocrBackendCombo.SelectedIndex = (int)_ocr.Backend;
            _ollamaEndpointBox.Text = _ocr.OllamaEndpoint;
            UpdateOllamaSettingsVisibility();
            RefreshCacheView();
        }

        private void OcrBackendCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_ocrBackendCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                _ocr.SetBackend(tag == "ollama" ? OcrService.OcrBackend.Ollama : OcrService.OcrBackend.WindowsBuiltIn);
                UpdateOllamaSettingsVisibility();
            }
        }

        private void OllamaEndpointBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var text = _ollamaEndpointBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
                _ocr.SetOllamaEndpoint(text);
        }

        private void UpdateOllamaSettingsVisibility()
        {
            _ollamaSettingsPanel.Visibility = _ocr.Backend == OcrService.OcrBackend.Ollama
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void LangCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_langCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                _ocr.SetLanguage(tag);
        }

        private void GroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_groupCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag && Enum.TryParse<OcrService.OcrGrouping>(tag, out var g))
                _ocr.SetGrouping(g);
        }

        private void WritingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_writingCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag && Enum.TryParse<OcrService.WritingMode>(tag, out var m))
                _ocr.SetWritingMode(m);
        }

        private void TagFontSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            _tagSettings.TagFontSize = e.NewValue;
            UpdateTagFontValue();
        }
    }
}
