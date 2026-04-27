using MangaViewer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Media; // VisualTreeHelper
using MangaViewer.Services.Thumbnails; // moved thumbnail services
using MangaViewer.Helpers;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using Windows.Globalization;

namespace MangaViewer.Pages
{
    public sealed partial class SettingsPage : Page
    {
        private static string L(string key, string fallback) => LocalizationHelper.GetString(key, fallback);

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
        private ComboBox _ocrOllamaModelCombo = null!;
        private TextBlock _ocrOllamaModelStatus = null!;
        private ComboBox _ocrThinkingLevelCombo = null!;
        private ToggleSwitch _ocrStructuredOutputToggle = null!;
        private NumberBox _ocrOllamaTemperatureBox = null!;
        private NumberBox _llamaServerMaxConcurrentBox = null!;
        private ToggleSwitch _llamaServerSlotEraseToggle = null!;
        private NumberBox _hybridTextParallelBox = null!;
        private ToggleSwitch _hybridOnnxFallbackToggle = null!;
        private Button _docLayoutModelDownloadButton = null!;
        private TextBlock _docLayoutModelStatusText = null!;
        private ComboBox _onnxEpModeCombo = null!;
        private TextBox _onnxEpManualListBox = null!;
        private Button _onnxEpRegisterNowButton = null!;
        private TextBlock _onnxEpStatusText = null!;
        private TextBlock _onnxEpCompatibleListText = null!;
        private ToggleSwitch _onnxTrtCudaGraphToggle = null!;
        private TextBox _onnxTrtRuntimeCachePathBox = null!;
        private ToggleSwitch _onnxUseEpContextToggle = null!;
        private ToggleSwitch _onnxAutoCompileEpContextToggle = null!;
        private Button _onnxCompileEpContextNowButton = null!;
        private readonly Dictionary<string, bool> _ocrModelThinkingSupport = new(StringComparer.OrdinalIgnoreCase);
        private ToggleSwitch _ocrAdjacentPrefetchToggle = null!;
        private NumberBox _ocrAdjacentPrefetchCountBox = null!;
        private ToggleSwitch _translationAdjacentPrefetchToggle = null!;
        private NumberBox _translationAdjacentPrefetchCountBox = null!;
        private Slider _translationOverlayFontSlider = null!;
        private TextBlock _translationOverlayFontValue = null!;
        private Slider _translationOverlayBoxScaleHorizontalSlider = null!;
        private TextBlock _translationOverlayBoxScaleHorizontalValue = null!;
        private Slider _translationOverlayBoxScaleVerticalSlider = null!;
        private TextBlock _translationOverlayBoxScaleVerticalValue = null!;
        private Slider _tagFontSlider = null!;
        private TextBlock _tagFontValue = null!;

        private Slider _thumbWidthSlider = null!;
        private TextBlock _thumbWidthValue = null!;
        private ComboBox _appLanguageCombo = null!;
        private bool _isInitializingSettingsUi;

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
            if (_ocrAdjacentPrefetchToggle.IsOn != _ocr.PrefetchAdjacentPagesEnabled) _ocrAdjacentPrefetchToggle.IsOn = _ocr.PrefetchAdjacentPagesEnabled;
            if (Math.Abs(_ocrAdjacentPrefetchCountBox.Value - _ocr.PrefetchAdjacentPageCount) > 0.001) _ocrAdjacentPrefetchCountBox.Value = _ocr.PrefetchAdjacentPageCount;
            _ocrAdjacentPrefetchCountBox.IsEnabled = _ocrAdjacentPrefetchToggle.IsOn;
            if (_ocrStructuredOutputToggle.IsOn != _ocr.OllamaStructuredOutputEnabled)
                _ocrStructuredOutputToggle.IsOn = _ocr.OllamaStructuredOutputEnabled;
            if (Math.Abs(_ocrOllamaTemperatureBox.Value - _ocr.OllamaTemperature) > 0.0001)
                _ocrOllamaTemperatureBox.Value = _ocr.OllamaTemperature;
            if (Math.Abs(_llamaServerMaxConcurrentBox.Value - _ocr.LlamaServerMaxConcurrentRequests) > 0.001)
                _llamaServerMaxConcurrentBox.Value = _ocr.LlamaServerMaxConcurrentRequests;
            if (_llamaServerSlotEraseToggle.IsOn != _ocr.LlamaServerSlotEraseEnabled)
                _llamaServerSlotEraseToggle.IsOn = _ocr.LlamaServerSlotEraseEnabled;
            if (Math.Abs(_hybridTextParallelBox.Value - _ocr.HybridTextExtractionParallelism) > 0.001)
                _hybridTextParallelBox.Value = _ocr.HybridTextExtractionParallelism;
            if (_hybridOnnxFallbackToggle.IsOn != _ocr.HybridOnnxFallbackEnabled)
                _hybridOnnxFallbackToggle.IsOn = _ocr.HybridOnnxFallbackEnabled;
            if (_onnxEpModeCombo.SelectedIndex != (int)_ocr.OnnxExecutionProviderMode)
                _onnxEpModeCombo.SelectedIndex = (int)_ocr.OnnxExecutionProviderMode;
            if (!string.Equals(_onnxEpManualListBox.Text, _ocr.OnnxExecutionProviderManualList, StringComparison.Ordinal))
                _onnxEpManualListBox.Text = _ocr.OnnxExecutionProviderManualList;
            _onnxEpManualListBox.IsEnabled = _ocr.OnnxExecutionProviderMode == OcrService.OnnxEpRegistrationMode.Manual;
            _onnxEpStatusText.Text = _ocr.OnnxExecutionProviderStatus;
            if (_onnxTrtCudaGraphToggle.IsOn != _ocr.OnnxTrtRtxEnableCudaGraph)
                _onnxTrtCudaGraphToggle.IsOn = _ocr.OnnxTrtRtxEnableCudaGraph;
            if (!string.Equals(_onnxTrtRuntimeCachePathBox.Text, _ocr.OnnxTrtRtxRuntimeCachePath, StringComparison.Ordinal))
                _onnxTrtRuntimeCachePathBox.Text = _ocr.OnnxTrtRtxRuntimeCachePath;
            if (_onnxUseEpContextToggle.IsOn != _ocr.OnnxUseEpContextModel)
                _onnxUseEpContextToggle.IsOn = _ocr.OnnxUseEpContextModel;
            if (_onnxAutoCompileEpContextToggle.IsOn != _ocr.OnnxAutoCompileEpContextModel)
                _onnxAutoCompileEpContextToggle.IsOn = _ocr.OnnxAutoCompileEpContextModel;

            var thinkingItem = _ocrThinkingLevelCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, _ocr.OllamaThinkingLevel, StringComparison.OrdinalIgnoreCase));
            if (thinkingItem != null && !ReferenceEquals(_ocrThinkingLevelCombo.SelectedItem, thinkingItem))
                _ocrThinkingLevelCombo.SelectedItem = thinkingItem;

            if (!string.IsNullOrWhiteSpace(_ocr.OllamaModel))
            {
                var selected = _ocrOllamaModelCombo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => string.Equals(i.Tag as string, _ocr.OllamaModel, StringComparison.OrdinalIgnoreCase));
                if (selected == null)
                {
                    selected = new ComboBoxItem { Content = _ocr.OllamaModel, Tag = _ocr.OllamaModel };
                    _ocrOllamaModelCombo.Items.Add(selected);
                }
                if (!ReferenceEquals(_ocrOllamaModelCombo.SelectedItem, selected))
                    _ocrOllamaModelCombo.SelectedItem = selected;
            }

            UpdateOcrThinkingComboAvailability();
            UpdateOllamaSettingsVisibility();
            UpdateOcrGroupingAvailability();
        }

        private void BuildUi()
        {
            var stack = new StackPanel { Spacing = 18, Padding = new Thickness(24) };

            BuildGeneralSettingsSection(stack);
            BuildLibrarySettingsSection(stack);
            BuildOcrSettingsSection(stack);
            BuildTranslationSettingsSection(stack);
            BuildTagSettingsSection(stack);
            BuildThumbnailSettingsSection(stack);
            BuildCacheSettingsSection(stack);

            Content = new ScrollViewer { Content = stack };
        }

        private void BuildGeneralSettingsSection(StackPanel stack)
        {
            stack.Children.Add(new TextBlock
            {
                Text = LocalizationHelper.GetString("Settings.General.Header", "앱 설정"),
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });

            _appLanguageCombo = new ComboBox { Width = 180 };
            _appLanguageCombo.Items.Add(new ComboBoxItem { Content = LocalizationHelper.GetString("Settings.AppLanguage.Auto", "시스템 기본값"), Tag = "auto" });
            _appLanguageCombo.Items.Add(new ComboBoxItem { Content = LocalizationHelper.GetString("Settings.AppLanguage.Korean", "한국어"), Tag = "ko-KR" });
            _appLanguageCombo.Items.Add(new ComboBoxItem { Content = LocalizationHelper.GetString("Settings.AppLanguage.English", "English"), Tag = "en-US" });
            _appLanguageCombo.Items.Add(new ComboBoxItem { Content = LocalizationHelper.GetString("Settings.AppLanguage.Japanese", "日本語"), Tag = "ja-JP" });
            _appLanguageCombo.SelectionChanged += AppLanguageCombo_SelectionChanged;
            stack.Children.Add(Row(LocalizationHelper.GetString("Settings.AppLanguage.Label", "UI 언어:"), _appLanguageCombo));
        }

        private void BuildLibrarySettingsSection(StackPanel stack)
        {
            stack.Children.Add(new TextBlock { Text = L("Settings.Library.Header", "만화 라이브러리"), FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

            var libraryBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var addLibBtn = new Button { Content = L("Settings.Library.AddFolder", "라이브러리 폴더 추가") };
            addLibBtn.Click += AddLibraryFolder_Click;
            libraryBtnRow.Children.Add(addLibBtn);
            stack.Children.Add(libraryBtnRow);

            _libraryPaths = new ObservableCollection<string>();
            _libraryPathsList = new ListView { Height = 160, SelectionMode = ListViewSelectionMode.Single, ItemsSource = _libraryPaths };
            _libraryPathsList.ItemTemplate = (DataTemplate)Resources["LibraryPathItemTemplate"];
            _libraryPathsList.ContainerContentChanging += LibraryPathsList_ContainerContentChanging;
            stack.Children.Add(_libraryPathsList);
        }

        private void BuildOcrSettingsSection(StackPanel stack)
        {
            stack.Children.Add(new TextBlock { Text = L("Settings.Ocr.Header", "OCR 설정"), FontSize = 20, Margin = new Thickness(0, 24, 0, 0), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

            _ocrBackendCombo = new ComboBox { Width = 220 };
            _ocrBackendCombo.Items.Add(new ComboBoxItem { Content = "Hybrid (DocLayout + glm-ocr)", Tag = "hybrid" });
            _ocrBackendCombo.Items.Add(new ComboBoxItem { Content = "VLM (Full image)", Tag = "vlm" });
            _ocrBackendCombo.SelectionChanged += OcrBackendCombo_SelectionChanged;
            stack.Children.Add(Row(L("Settings.Ocr.Engine", "OCR 엔진:"), _ocrBackendCombo));

            _ollamaEndpointBox = new TextBox { Width = 260, PlaceholderText = "http://localhost:11434" };
            _ollamaEndpointBox.LostFocus += OllamaEndpointBox_LostFocus;
            _ollamaSettingsPanel = new StackPanel { Spacing = 8 };
            _ollamaSettingsPanel.Children.Add(Row(L("Settings.Ocr.OllamaEndpoint", "Ollama 주소:"), _ollamaEndpointBox));

            _ocrOllamaModelCombo = new ComboBox { Width = 260, PlaceholderText = "VLM model" };
            _ocrOllamaModelCombo.SelectionChanged += OcrOllamaModelCombo_SelectionChanged;
            var fetchOcrOllamaModelsBtn = new Button { Content = L("Settings.Ocr.LoadModels", "모델 불러오기"), Margin = new Thickness(8, 0, 0, 0) };
            _ocrOllamaModelStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Opacity = 0.6, FontSize = 12 };
            var ocrModelInner = new StackPanel { Orientation = Orientation.Horizontal };
            ocrModelInner.Children.Add(_ocrOllamaModelCombo);
            ocrModelInner.Children.Add(fetchOcrOllamaModelsBtn);
            ocrModelInner.Children.Add(_ocrOllamaModelStatus);
            _ollamaSettingsPanel.Children.Add(Row(L("Settings.Ocr.Model", "OCR 모델:"), ocrModelInner));

            _ocrThinkingLevelCombo = new ComboBox { Width = 180 };
            _ocrThinkingLevelCombo.Items.Add(new ComboBoxItem { Content = L("Settings.Common.Off", "꺼짐"), Tag = "Off" });
            _ocrThinkingLevelCombo.Items.Add(new ComboBoxItem { Content = L("Settings.Common.On", "켜짐"), Tag = "On" });
            _ocrThinkingLevelCombo.SelectionChanged += OcrThinkingLevelCombo_SelectionChanged;
            _ollamaSettingsPanel.Children.Add(Row("Thinking:", _ocrThinkingLevelCombo));

            _ocrStructuredOutputToggle = new ToggleSwitch { OnContent = L("Settings.Ocr.Output.Json", "JSON(박스 포함)"), OffContent = L("Settings.Ocr.Output.Text", "일반 텍스트") };
            _ocrStructuredOutputToggle.Toggled += OcrStructuredOutputToggle_Toggled;
            _ollamaSettingsPanel.Children.Add(Row(L("Settings.Ocr.OutputFormat", "출력 형식:"), _ocrStructuredOutputToggle));

            _ocrOllamaTemperatureBox = new NumberBox
            {
                Width = 140,
                Minimum = 0,
                Maximum = 2,
                SmallChange = 0.1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
            };
            _ocrOllamaTemperatureBox.ValueChanged += OcrOllamaTemperatureBox_ValueChanged;
            _ollamaSettingsPanel.Children.Add(Row("Temperature:", _ocrOllamaTemperatureBox));

            _llamaServerMaxConcurrentBox = new NumberBox
            {
                Width = 140,
                Minimum = 1,
                Maximum = 32,
                SmallChange = 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
            };
            _llamaServerMaxConcurrentBox.ValueChanged += (s, e) =>
            {
                if (_isInitializingSettingsUi || double.IsNaN(_llamaServerMaxConcurrentBox.Value))
                    return;

                int count = (int)Math.Clamp(Math.Round(_llamaServerMaxConcurrentBox.Value), 1, 32);
                if (Math.Abs(_llamaServerMaxConcurrentBox.Value - count) > 0.001)
                    _llamaServerMaxConcurrentBox.Value = count;

                _ocr.SetLlamaServerMaxConcurrentRequests(count);
            };
            _ollamaSettingsPanel.Children.Add(Row(L("Settings.Ocr.LlamaServerParallel", "llama-server request parallelism:"), _llamaServerMaxConcurrentBox));

            _llamaServerSlotEraseToggle = new ToggleSwitch
            {
                OnContent = L("Settings.Common.Enabled", "Enabled"),
                OffContent = L("Settings.Common.Disabled", "Disabled")
            };
            _llamaServerSlotEraseToggle.Toggled += (s, e) =>
            {
                if (_isInitializingSettingsUi)
                    return;

                _ocr.SetLlamaServerSlotEraseEnabled(_llamaServerSlotEraseToggle.IsOn);
            };
            _ollamaSettingsPanel.Children.Add(Row(L("Settings.Ocr.LlamaServerSlotErase", "Erase llama-server slot on cancel/failure:"), _llamaServerSlotEraseToggle));

            _hybridTextParallelBox = new NumberBox
            {
                Width = 140,
                Minimum = 1,
                Maximum = 8,
                SmallChange = 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
            };
            _hybridTextParallelBox.ValueChanged += HybridTextParallelBox_ValueChanged;
            _ollamaSettingsPanel.Children.Add(Row(L("Settings.Ocr.HybridParallel", "Hybrid text parallel:"), _hybridTextParallelBox));

            _hybridOnnxFallbackToggle = new ToggleSwitch
            {
                OnContent = L("Settings.Common.Enabled", "Enabled"),
                OffContent = L("Settings.Common.Disabled", "Disabled")
            };
            _hybridOnnxFallbackToggle.Toggled += HybridOnnxFallbackToggle_Toggled;
            _ollamaSettingsPanel.Children.Add(Row(L("Settings.Ocr.HybridFallback", "Hybrid fallback to VLM:"), _hybridOnnxFallbackToggle));

            _docLayoutModelDownloadButton = new Button { Content = L("Settings.Ocr.DocLayout.Download", "Download PP-DocLayoutV3 model") };
            _docLayoutModelDownloadButton.Click += DocLayoutModelDownloadButton_Click;
            _docLayoutModelStatusText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Opacity = 0.7,
                FontSize = 12
            };
            var docLayoutRow = new StackPanel { Orientation = Orientation.Horizontal };
            docLayoutRow.Children.Add(_docLayoutModelDownloadButton);
            docLayoutRow.Children.Add(_docLayoutModelStatusText);
            _ollamaSettingsPanel.Children.Add(Row(L("Settings.Ocr.DocLayout.Model", "DocLayout model:"), docLayoutRow));

            _onnxEpModeCombo = new ComboBox { Width = 180 };
            _onnxEpModeCombo.Items.Add(new ComboBoxItem { Content = L("Settings.Common.Auto", "Auto"), Tag = "auto" });
            _onnxEpModeCombo.Items.Add(new ComboBoxItem { Content = L("Settings.Common.Manual", "Manual"), Tag = "manual" });
            _onnxEpModeCombo.SelectionChanged += OnnxEpModeCombo_SelectionChanged;
            _ollamaSettingsPanel.Children.Add(Row(L("Settings.Ocr.OnnxEpMode", "ONNX EP mode:"), _onnxEpModeCombo));

            _onnxEpManualListBox = new TextBox { Width = 300, PlaceholderText = "QNN, DML ... (optional)" };
            _onnxEpManualListBox.LostFocus += OnnxEpManualListBox_LostFocus;
            _ollamaSettingsPanel.Children.Add(Row(L("Settings.Ocr.ManualEpList", "Manual EP list:"), _onnxEpManualListBox));

            _onnxEpRegisterNowButton = new Button { Content = L("Settings.Ocr.EpRegisterNow", "EP register now") };
            var onnxEpRefreshCompatibleButton = new Button { Content = L("Settings.Ocr.GetCompatibleEps", "Get compatible EPs"), Margin = new Thickness(8, 0, 0, 0) };
            onnxEpRefreshCompatibleButton.Click += OnnxEpRefreshCompatibleButton_Click;
            _onnxEpRegisterNowButton.Click += OnnxEpRegisterNowButton_Click;
            _onnxEpStatusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Opacity = 0.7, FontSize = 12 };
            var epRow = new StackPanel { Orientation = Orientation.Horizontal };
            epRow.Children.Add(_onnxEpRegisterNowButton);
            epRow.Children.Add(onnxEpRefreshCompatibleButton);
            epRow.Children.Add(_onnxEpStatusText);
            _ollamaSettingsPanel.Children.Add(Row(L("Settings.Ocr.ExecutionProvider", "Execution Provider:"), epRow));

            _onnxEpCompatibleListText = new TextBlock
            {
                Text = string.Empty,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 560,
                Opacity = 0.8,
                FontSize = 12
            };
            _ollamaSettingsPanel.Children.Add(Row(L("Settings.Ocr.CompatibleEps", "Compatible EPs:"), _onnxEpCompatibleListText));

            _onnxTrtCudaGraphToggle = new ToggleSwitch { OnContent = L("Settings.Common.Enabled", "Enabled"), OffContent = L("Settings.Common.Disabled", "Disabled") };
            _onnxTrtCudaGraphToggle.Toggled += (s, e) =>
            {
                _ocr.SetOnnxTrtRtxEnableCudaGraph(_onnxTrtCudaGraphToggle.IsOn);
                _onnxEpStatusText.Text = L("Settings.Ocr.Status.CudaGraphUpdated", "TensorRT RTX CUDA graph option updated");
            };
            _ollamaSettingsPanel.Children.Add(Row(L("Settings.Ocr.TensorRtCudaGraph", "TensorRT RTX CUDA graph:"), _onnxTrtCudaGraphToggle));

            _onnxTrtRuntimeCachePathBox = new TextBox { Width = 420, PlaceholderText = "Runtime cache directory" };
            _onnxTrtRuntimeCachePathBox.LostFocus += (s, e) =>
            {
                _ocr.SetOnnxTrtRtxRuntimeCachePath(_onnxTrtRuntimeCachePathBox.Text);
            };
            _ollamaSettingsPanel.Children.Add(Row(L("Settings.Ocr.TensorRtRuntimeCache", "TensorRT RTX runtime cache:"), _onnxTrtRuntimeCachePathBox));

            _onnxUseEpContextToggle = new ToggleSwitch { OnContent = L("Settings.Ocr.UseCompiledModel", "Use compiled model"), OffContent = L("Settings.Ocr.UseSourceOnnx", "Use source ONNX") };
            _onnxUseEpContextToggle.Toggled += (s, e) =>
            {
                _ocr.SetOnnxUseEpContextModel(_onnxUseEpContextToggle.IsOn);
                _onnxEpStatusText.Text = _ocr.OnnxUseEpContextModel
                    ? L("Settings.Ocr.Status.EpContextEnabled", "EP context loading enabled")
                    : L("Settings.Ocr.Status.EpContextDisabled", "EP context loading disabled");
            };
            _ollamaSettingsPanel.Children.Add(Row(L("Settings.Ocr.EpContextLoad", "EP context load:"), _onnxUseEpContextToggle));

            _onnxAutoCompileEpContextToggle = new ToggleSwitch { OnContent = L("Settings.Ocr.AutoCompile", "Auto compile"), OffContent = L("Settings.Ocr.ManualCompile", "Manual compile") };
            _onnxAutoCompileEpContextToggle.Toggled += (s, e) =>
            {
                _ocr.SetOnnxAutoCompileEpContextModel(_onnxAutoCompileEpContextToggle.IsOn);
                _onnxEpStatusText.Text = _ocr.OnnxAutoCompileEpContextModel
                    ? L("Settings.Ocr.Status.EpAutoCompileEnabled", "EP context auto compile enabled")
                    : L("Settings.Ocr.Status.EpAutoCompileDisabled", "EP context auto compile disabled");
            };
            _ollamaSettingsPanel.Children.Add(Row(L("Settings.Ocr.EpContextCompileMode", "EP context compile mode:"), _onnxAutoCompileEpContextToggle));

            _onnxCompileEpContextNowButton = new Button { Content = L("Settings.Ocr.CompileEpContextNow", "Compile EP context now") };
            _onnxCompileEpContextNowButton.Click += async (s, e) =>
            {
                _onnxCompileEpContextNowButton.IsEnabled = false;
                _onnxEpStatusText.Text = L("Settings.Ocr.Status.CompilingEpContext", "Compiling EP context...");
                try
                {
                    await _ocr.CompileDocLayoutEpContextModelAsync(CancellationToken.None);
                    _onnxEpStatusText.Text = _ocr.OnnxExecutionProviderStatus;
                }
                catch (Exception ex)
                {
                    _onnxEpStatusText.Text = L("Settings.Common.ErrorPrefix", "Error: ") + ex.Message;
                }
                finally
                {
                    _onnxCompileEpContextNowButton.IsEnabled = true;
                }
            };
            _ollamaSettingsPanel.Children.Add(Row(L("Settings.Ocr.EpContextCompile", "EP context compile:"), _onnxCompileEpContextNowButton));

            if (!string.IsNullOrWhiteSpace(_ocr.OllamaModel))
            {
                _ocrOllamaModelCombo.Items.Add(new ComboBoxItem { Content = _ocr.OllamaModel, Tag = _ocr.OllamaModel });
                _ocrOllamaModelCombo.SelectedIndex = 0;
            }

            fetchOcrOllamaModelsBtn.Click += async (s, e) => await RefreshOcrOllamaModelsAsync(fetchOcrOllamaModelsBtn);
            stack.Children.Add(_ollamaSettingsPanel);

            _langCombo = new ComboBox { Width = 160 };
            _langCombo.Items.Add(new ComboBoxItem { Content = L("Settings.Common.Auto", "자동"), Tag = "auto" });
            _langCombo.Items.Add(new ComboBoxItem { Content = L("Settings.AppLanguage.Japanese", "일본어"), Tag = "ja" });
            _langCombo.Items.Add(new ComboBoxItem { Content = L("Settings.AppLanguage.Korean", "한국어"), Tag = "ko" });
            _langCombo.Items.Add(new ComboBoxItem { Content = L("Settings.AppLanguage.English", "영어"), Tag = "en" });
            _langCombo.SelectionChanged += LangCombo_SelectionChanged;
            stack.Children.Add(Row(L("Settings.Ocr.Language", "언어:"), _langCombo));

            _groupCombo = new ComboBox { Width = 160 };
            _groupCombo.Items.Add(new ComboBoxItem { Content = L("Settings.Ocr.Group.Word", "단어"), Tag = OcrService.OcrGrouping.Word.ToString() });
            _groupCombo.Items.Add(new ComboBoxItem { Content = L("Settings.Ocr.Group.Line", "줄"), Tag = OcrService.OcrGrouping.Line.ToString() });
            _groupCombo.Items.Add(new ComboBoxItem { Content = L("Settings.Ocr.Group.Paragraph", "문단"), Tag = OcrService.OcrGrouping.Paragraph.ToString() });
            _groupCombo.SelectionChanged += GroupCombo_SelectionChanged;
            stack.Children.Add(Row(L("Settings.Ocr.Group.Label", "그룹:"), _groupCombo));

            _writingCombo = new ComboBox { Width = 160 };
            _writingCombo.Items.Add(new ComboBoxItem { Content = L("Settings.Common.Auto", "자동"), Tag = OcrService.WritingMode.Auto.ToString() });
            _writingCombo.Items.Add(new ComboBoxItem { Content = L("Settings.Ocr.Writing.Horizontal", "가로"), Tag = OcrService.WritingMode.Horizontal.ToString() });
            _writingCombo.Items.Add(new ComboBoxItem { Content = L("Settings.Ocr.Writing.Vertical", "세로"), Tag = OcrService.WritingMode.Vertical.ToString() });
            _writingCombo.SelectionChanged += WritingCombo_SelectionChanged;
            stack.Children.Add(Row(L("Settings.Ocr.Writing.Label", "텍스트 방향:"), _writingCombo));

            _ocrAdjacentPrefetchToggle = new ToggleSwitch { OnContent = L("Settings.Common.Use", "사용"), OffContent = L("Settings.Common.NotUse", "사용 안 함") };
            _ocrAdjacentPrefetchToggle.Toggled += (s, e) =>
            {
                _ocr.SetPrefetchAdjacentPagesEnabled(_ocrAdjacentPrefetchToggle.IsOn);
                _ocrAdjacentPrefetchCountBox.IsEnabled = _ocrAdjacentPrefetchToggle.IsOn;
            };
            stack.Children.Add(Row(L("Settings.Ocr.AdjacentCache", "인접 페이지 OCR 캐시:"), _ocrAdjacentPrefetchToggle));

            _ocrAdjacentPrefetchCountBox = new NumberBox
            {
                Width = 100,
                Minimum = 0,
                Maximum = 10,
                SmallChange = 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
            };
            _ocrAdjacentPrefetchCountBox.ValueChanged += (s, e) =>
            {
                int count = (int)Math.Clamp(Math.Round(_ocrAdjacentPrefetchCountBox.Value), 0, 10);
                _ocr.SetPrefetchAdjacentPageCount(count);
            };
            stack.Children.Add(Row(L("Settings.Ocr.AdjacentCount", "OCR 인접 페이지 수:"), _ocrAdjacentPrefetchCountBox));

            stack.Children.Add(new Controls.ParagraphGapSliderControl());
        }

        private void BuildTranslationSettingsSection(StackPanel stack)
        {
            var translationSettings = TranslationSettingsService.Instance;

            stack.Children.Add(new TextBlock { Text = L("Settings.Translation.Header", "번역 설정"), FontSize = 20, Margin = new Thickness(0, 24, 0, 0), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

            var translationProviderCombo = CreateTranslationProviderCombo();
            var translationApiKeyBox = new PasswordBox { Width = 260 };
            var apiKeyRow = Row(L("Settings.Translation.ApiKey", "API 키:"), translationApiKeyBox);
            var translationTargetLanguageBox = new TextBox
            {
                Width = 220,
                PlaceholderText = "Korean"
            };
            var translationTargetLanguageRow = Row(L("Settings.Translation.TargetLanguage", "타겟 언어:"), translationTargetLanguageBox);

            var translationModelBox = new TextBox { Width = 260 };

            var googleModelCombo = new ComboBox { Width = 200, PlaceholderText = L("Settings.Translation.SelectModel", "모델을 선택하세요") };
            var fetchGoogleModelsBtn = new Button { Content = L("Settings.Translation.FetchModelList", "목록 가져오기"), Margin = new Thickness(8, 0, 0, 0) };
            var googleModelStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Opacity = 0.6, FontSize = 12 };
            var googleModelInner = new StackPanel { Orientation = Orientation.Horizontal };
            googleModelInner.Children.Add(googleModelCombo);
            googleModelInner.Children.Add(fetchGoogleModelsBtn);
            googleModelInner.Children.Add(googleModelStatus);

            var textModelRow = Row(L("Settings.Translation.Model", "모델:"), translationModelBox);
            var googleModelRow = Row(L("Settings.Translation.Model", "모델:"), googleModelInner);
            var ollamaEndpointBox = new TextBox { Width = 260, PlaceholderText = "http://localhost:11434" };
            var ollamaEndpointRow = Row("Ollama / llama-server URL:", ollamaEndpointBox);

            var ollamaModelCombo = new ComboBox { Width = 200, PlaceholderText = L("Settings.Translation.SelectModel", "모델을 선택하세요") };
            var fetchOllamaModelsBtn = new Button { Content = L("Settings.Translation.FetchModelList", "목록 가져오기"), Margin = new Thickness(8, 0, 0, 0) };
            var ollamaModelStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Opacity = 0.6, FontSize = 12 };
            var ollamaModelInner = new StackPanel { Orientation = Orientation.Horizontal };
            ollamaModelInner.Children.Add(ollamaModelCombo);
            ollamaModelInner.Children.Add(fetchOllamaModelsBtn);
            ollamaModelInner.Children.Add(ollamaModelStatus);
            var ollamaModelRow = Row(L("Settings.Translation.Model", "모델:"), ollamaModelInner);

            AddSavedTranslationModels(translationSettings, googleModelCombo, ollamaModelCombo);

            Action<TranslationProviderKind> updateModelRowVisibility = (provider) =>
            {
                textModelRow.Visibility = provider is TranslationProviderKind.Google or TranslationProviderKind.Ollama ? Visibility.Collapsed : Visibility.Visible;
                googleModelRow.Visibility = provider == TranslationProviderKind.Google ? Visibility.Visible : Visibility.Collapsed;
                ollamaEndpointRow.Visibility = provider == TranslationProviderKind.Ollama ? Visibility.Visible : Visibility.Collapsed;
                ollamaModelRow.Visibility = provider == TranslationProviderKind.Ollama ? Visibility.Visible : Visibility.Collapsed;
            };

            Action updateApiKeyBox = () =>
            {
                var provider = GetSelectedTranslationProvider(translationProviderCombo);
                translationApiKeyBox.Password = translationSettings.GetApiKeyForProvider(provider);
                apiKeyRow.Visibility = provider == TranslationProviderKind.Ollama ? Visibility.Collapsed : Visibility.Visible;
            };

            var systemPromptBox = new TextBox
            {
                Width = 360,
                Height = 80,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                PlaceholderText = L("Settings.Translation.SystemPromptPlaceholder", "시스템 프롬프트")
            };

            Action updateSystemPromptBox = () =>
            {
                var provider = GetSelectedTranslationProvider(translationProviderCombo);
                systemPromptBox.Text = translationSettings.GetSystemPromptForProvider(provider);
            };

            systemPromptBox.LostFocus += (s, e) =>
            {
                var provider = GetSelectedTranslationProvider(translationProviderCombo);
                translationSettings.SetSystemPromptForProvider(provider, systemPromptBox.Text);
            };

            TranslationProviderKind currentProvider = translationSettings.ProviderKind;
            SelectTranslationProvider(translationProviderCombo, currentProvider);
            translationModelBox.Text = translationSettings.GetModelForProvider(currentProvider);
            ollamaEndpointBox.Text = translationSettings.OllamaEndpoint;
            translationTargetLanguageBox.Text = translationSettings.TargetLanguage;
            updateApiKeyBox();
            updateModelRowVisibility(currentProvider);
            updateSystemPromptBox();

            var thinkingLevelCombo = new ComboBox { Width = 160 };

            translationProviderCombo.SelectionChanged += (s, e) =>
            {
                var provider = GetSelectedTranslationProvider(translationProviderCombo);
                translationSettings.ProviderKind = provider;
                translationModelBox.Text = translationSettings.GetModelForProvider(provider);
                updateModelRowVisibility(provider);
                updateApiKeyBox();
                updateSystemPromptBox();
                PopulateTranslationThinkingLevels(provider, thinkingLevelCombo, translationSettings);
            };

            translationModelBox.LostFocus += (s, e) =>
            {
                var provider = GetSelectedTranslationProvider(translationProviderCombo);
                if (provider is TranslationProviderKind.OpenAI or TranslationProviderKind.Anthropic)
                    translationSettings.SetModelForProvider(provider, translationModelBox.Text);
            };

            googleModelCombo.SelectionChanged += (s, e) =>
            {
                if (googleModelCombo.SelectedItem is ComboBoxItem item)
                    translationSettings.GoogleModel = (string)item.Tag;
            };

            ollamaModelCombo.SelectionChanged += (s, e) =>
            {
                if (ollamaModelCombo.SelectedItem is ComboBoxItem item)
                    translationSettings.OllamaModel = (string)item.Tag;
            };

            ollamaEndpointBox.LostFocus += (s, e) =>
            {
                var endpoint = ollamaEndpointBox.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(endpoint))
                    translationSettings.OllamaEndpoint = endpoint.TrimEnd('/');
            };

            fetchGoogleModelsBtn.Click += async (s, e) =>
            {
                translationSettings.GoogleApiKey = translationApiKeyBox.Password;

                if (string.IsNullOrWhiteSpace(translationSettings.GoogleApiKey))
                {
                    googleModelStatus.Text = L("Settings.Translation.EnterApiKeyFirst", "API 키를 먼저 입력하세요");
                    return;
                }

                fetchGoogleModelsBtn.IsEnabled = false;
                googleModelStatus.Text = L("Settings.Common.Loading", "가져오는 중...");

                try
                {
                    var savedSelection = googleModelCombo.SelectedItem is ComboBoxItem sel
                        ? (string)sel.Tag
                        : translationSettings.GoogleModel;

                    googleModelCombo.Items.Clear();

                    var gClient = new Google.GenAI.Client(apiKey: translationSettings.GoogleApiKey);
                    var pager = await gClient.Models.ListAsync();

                    var modelIds = new List<string>();
                    await foreach (var model in pager)
                    {
                        if (model.Name is not null)
                        {
                            var id = model.Name.StartsWith("models/") ? model.Name[7..] : model.Name;
                            modelIds.Add(id);
                        }
                    }

                    modelIds.Sort((a, b) =>
                    {
                        bool aG = a.StartsWith("gemini", StringComparison.OrdinalIgnoreCase);
                        bool bG = b.StartsWith("gemini", StringComparison.OrdinalIgnoreCase);
                        if (aG != bG) return aG ? -1 : 1;
                        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                    });

                    foreach (var id in modelIds)
                        googleModelCombo.Items.Add(new ComboBoxItem { Content = id, Tag = id });

                    RestoreTranslationModelSelection(googleModelCombo, savedSelection);
                    googleModelStatus.Text = string.Format(L("Settings.Common.CountUnit", "{0}개"), modelIds.Count);
                }
                catch (Exception ex)
                {
                    googleModelStatus.Text = L("Settings.Common.ErrorPrefix", "Error: ") + ex.Message;
                }
                finally
                {
                    fetchGoogleModelsBtn.IsEnabled = true;
                }
            };

            fetchOllamaModelsBtn.Click += async (s, e) =>
            {
                fetchOllamaModelsBtn.IsEnabled = false;
                ollamaModelStatus.Text = L("Settings.Common.Loading", "가져오는 중...");

                try
                {
                    var endpoint = string.IsNullOrWhiteSpace(ollamaEndpointBox.Text)
                        ? "http://localhost:11434"
                        : ollamaEndpointBox.Text.Trim().TrimEnd('/');
                    translationSettings.OllamaEndpoint = endpoint;

                    var savedSelection = ollamaModelCombo.SelectedItem is ComboBoxItem sel
                        ? (string)sel.Tag
                        : translationSettings.OllamaModel;

                    var modelIds = await GetOllamaModelIdsAsync(endpoint);

                    ollamaModelCombo.Items.Clear();
                    foreach (var id in modelIds)
                        ollamaModelCombo.Items.Add(new ComboBoxItem { Content = id, Tag = id });

                    RestoreTranslationModelSelection(ollamaModelCombo, savedSelection);
                    ollamaModelStatus.Text = string.Format(L("Settings.Common.CountUnit", "{0}개"), modelIds.Count);
                }
                catch (Exception ex)
                {
                    ollamaModelStatus.Text = L("Settings.Common.ErrorPrefix", "Error: ") + ex.Message;
                }
                finally
                {
                    fetchOllamaModelsBtn.IsEnabled = true;
                }
            };

            translationApiKeyBox.LostFocus += (s, e) =>
            {
                var provider = GetSelectedTranslationProvider(translationProviderCombo);
                translationSettings.SetApiKeyForProvider(provider, translationApiKeyBox.Password);
            };

            translationTargetLanguageBox.LostFocus += (s, e) =>
            {
                translationSettings.TargetLanguage = translationTargetLanguageBox.Text;
                if (string.IsNullOrWhiteSpace(translationTargetLanguageBox.Text))
                    translationTargetLanguageBox.Text = translationSettings.TargetLanguage;
            };

            stack.Children.Add(Row(L("Settings.Translation.Provider", "공급자:"), translationProviderCombo));
            stack.Children.Add(textModelRow);
            stack.Children.Add(googleModelRow);
            stack.Children.Add(ollamaEndpointRow);
            stack.Children.Add(ollamaModelRow);
            stack.Children.Add(translationTargetLanguageRow);
            stack.Children.Add(Row(L("Settings.Translation.SystemPrompt", "시스템 프롬프트:"), systemPromptBox));
            stack.Children.Add(apiKeyRow);

            _translationAdjacentPrefetchToggle = new ToggleSwitch { OnContent = L("Settings.Common.Use", "사용"), OffContent = L("Settings.Common.NotUse", "사용 안 함") };
            _translationAdjacentPrefetchToggle.Toggled += (s, e) =>
            {
                translationSettings.PrefetchAdjacentPagesEnabled = _translationAdjacentPrefetchToggle.IsOn;
                _translationAdjacentPrefetchCountBox.IsEnabled = _translationAdjacentPrefetchToggle.IsOn;
            };
            stack.Children.Add(Row(L("Settings.Translation.AdjacentCache", "인접 페이지 번역 캐시:"), _translationAdjacentPrefetchToggle));

            _translationAdjacentPrefetchCountBox = new NumberBox
            {
                Width = 100,
                Minimum = 0,
                Maximum = 10,
                SmallChange = 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
            };
            _translationAdjacentPrefetchCountBox.ValueChanged += (s, e) =>
            {
                int count = (int)Math.Clamp(Math.Round(_translationAdjacentPrefetchCountBox.Value), 0, 10);
                translationSettings.PrefetchAdjacentPageCount = count;
            };
            stack.Children.Add(Row(L("Settings.Translation.AdjacentCount", "번역 인접 페이지 수:"), _translationAdjacentPrefetchCountBox));

            _translationOverlayFontSlider = new Slider
            {
                Minimum = 8,
                Maximum = 28,
                Width = 220,
                Value = translationSettings.OverlayFontSize
            };
            _translationOverlayFontSlider.ValueChanged += TranslationOverlayFontSlider_ValueChanged;
            _translationOverlayFontValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            UpdateTranslationOverlayFontValue();
            stack.Children.Add(CreateTranslationSliderRow(L("Settings.Translation.OverlayFontSize", "번역 바운딩 박스 글자 크기:"), _translationOverlayFontSlider, _translationOverlayFontValue));

            _translationOverlayBoxScaleHorizontalSlider = new Slider
            {
                Minimum = 0.6,
                Maximum = 2.2,
                Width = 220,
                StepFrequency = 0.05,
                Value = translationSettings.OverlayBoxScaleHorizontal
            };
            _translationOverlayBoxScaleHorizontalSlider.ValueChanged += TranslationOverlayBoxScaleHorizontalSlider_ValueChanged;
            _translationOverlayBoxScaleHorizontalValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            UpdateTranslationOverlayBoxScaleHorizontalValue();
            stack.Children.Add(CreateTranslationSliderRow(L("Settings.Translation.OverlayBoxHorizontal", "번역 바운딩 박스 가로 크기:"), _translationOverlayBoxScaleHorizontalSlider, _translationOverlayBoxScaleHorizontalValue));

            _translationOverlayBoxScaleVerticalSlider = new Slider
            {
                Minimum = 0.6,
                Maximum = 2.2,
                Width = 220,
                StepFrequency = 0.05,
                Value = translationSettings.OverlayBoxScaleVertical
            };
            _translationOverlayBoxScaleVerticalSlider.ValueChanged += TranslationOverlayBoxScaleVerticalSlider_ValueChanged;
            _translationOverlayBoxScaleVerticalValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            UpdateTranslationOverlayBoxScaleVerticalValue();
            stack.Children.Add(CreateTranslationSliderRow(L("Settings.Translation.OverlayBoxVertical", "번역 바운딩 박스 세로 크기:"), _translationOverlayBoxScaleVerticalSlider, _translationOverlayBoxScaleVerticalValue));

            PopulateTranslationThinkingLevels(currentProvider, thinkingLevelCombo, translationSettings);
            thinkingLevelCombo.SelectionChanged += (s, e) =>
            {
                if (thinkingLevelCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                    translationSettings.ThinkingLevel = tag;
            };
            stack.Children.Add(Row("Thinking:", thinkingLevelCombo));
        }

        private static ComboBox CreateTranslationProviderCombo()
        {
            var combo = new ComboBox { Width = 160 };
            combo.Items.Add(new ComboBoxItem { Content = TranslationProviders.Google, Tag = TranslationProviderKind.Google });
            combo.Items.Add(new ComboBoxItem { Content = TranslationProviders.OpenAI, Tag = TranslationProviderKind.OpenAI });
            combo.Items.Add(new ComboBoxItem { Content = TranslationProviders.Anthropic, Tag = TranslationProviderKind.Anthropic });
            combo.Items.Add(new ComboBoxItem { Content = TranslationProviders.Ollama, Tag = TranslationProviderKind.Ollama });
            return combo;
        }

        private static TranslationProviderKind GetSelectedTranslationProvider(ComboBox combo)
            => (TranslationProviderKind)((ComboBoxItem)combo.SelectedItem).Tag;

        private static void SelectTranslationProvider(ComboBox combo, TranslationProviderKind provider)
        {
            combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, provider))
                ?? (ComboBoxItem)combo.Items[0];
        }

        private static void AddSavedTranslationModels(TranslationSettingsService settings, ComboBox googleModelCombo, ComboBox ollamaModelCombo)
        {
            if (settings.ProviderKind == TranslationProviderKind.Google && !string.IsNullOrEmpty(settings.GoogleModel))
            {
                var savedId = settings.GoogleModel;
                googleModelCombo.Items.Add(new ComboBoxItem { Content = savedId, Tag = savedId });
                googleModelCombo.SelectedIndex = 0;
            }

            if (settings.ProviderKind == TranslationProviderKind.Ollama && !string.IsNullOrEmpty(settings.OllamaModel))
            {
                var savedId = settings.OllamaModel;
                ollamaModelCombo.Items.Add(new ComboBoxItem { Content = savedId, Tag = savedId });
                ollamaModelCombo.SelectedIndex = 0;
            }
        }

        private static void RestoreTranslationModelSelection(ComboBox combo, string savedSelection)
        {
            var toSelect = combo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (string)i.Tag == savedSelection);
            if (toSelect is not null)
                combo.SelectedItem = toSelect;
            else if (combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }

        private static void PopulateTranslationThinkingLevels(TranslationProviderKind provider, ComboBox combo, TranslationSettingsService translationSettings)
        {
            combo.Items.Clear();
            if (provider == TranslationProviderKind.Ollama)
            {
                combo.Items.Add(new ComboBoxItem { Content = L("Settings.Common.Off", "꺼짐"), Tag = "Off" });
                combo.Items.Add(new ComboBoxItem { Content = L("Settings.Common.On", "켜짐"), Tag = "On" });

                string normalized = ThinkingLevelHelper.NormalizeOllama(translationSettings.ThinkingLevel);
                var selected = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == normalized)
                    ?? (ComboBoxItem)combo.Items[0];
                combo.SelectedItem = selected;

                if (!string.Equals(translationSettings.ThinkingLevel, normalized, StringComparison.OrdinalIgnoreCase))
                    translationSettings.ThinkingLevel = normalized;
                return;
            }

            combo.Items.Add(new ComboBoxItem { Content = L("Settings.Common.Off", "꺼짐"), Tag = "Off" });
            combo.Items.Add(new ComboBoxItem { Content = L("Settings.Thinking.Minimal", "최소"), Tag = "Minimal" });
            combo.Items.Add(new ComboBoxItem { Content = L("Settings.Thinking.Low", "낮음"), Tag = "Low" });
            combo.Items.Add(new ComboBoxItem { Content = L("Settings.Thinking.Medium", "보통"), Tag = "Medium" });
            combo.Items.Add(new ComboBoxItem { Content = L("Settings.Thinking.High", "높음"), Tag = "High" });

            var saved = translationSettings.ThinkingLevel;
            var selectedNonOllama = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == saved)
                ?? (ComboBoxItem)combo.Items[0];
            combo.SelectedItem = selectedNonOllama;
        }

        private static UIElement CreateTranslationSliderRow(string label, Slider slider, TextBlock valueText)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(slider);
            row.Children.Add(valueText);
            return row;
        }

        private void BuildTagSettingsSection(StackPanel stack)
        {
            stack.Children.Add(new TextBlock { Text = L("Settings.Tag.Header", "태그 표시"), FontSize = 20, Margin = new Thickness(0, 24, 0, 0), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

            _tagFontSlider = new Slider { Minimum = 8, Maximum = 32, Width = 220, Value = _tagSettings.TagFontSize };
            _tagFontSlider.ValueChanged += TagFontSlider_ValueChanged;
            _tagFontValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            UpdateTagFontValue();
            var fontRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            fontRow.Children.Add(new TextBlock { Text = L("Settings.Tag.FontSize", "태그 폰트 크기:"), VerticalAlignment = VerticalAlignment.Center });
            fontRow.Children.Add(_tagFontSlider);
            fontRow.Children.Add(_tagFontValue);
            stack.Children.Add(fontRow);
        }

        private void BuildThumbnailSettingsSection(StackPanel stack)
        {
            stack.Children.Add(new TextBlock { Text = L("Settings.Thumbnail.Header", "썸네일 설정"), FontSize = 20, Margin = new Thickness(0, 24, 0, 0), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var thumbRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            _thumbWidthSlider = new Slider { Minimum = 64, Maximum = 512, Width = 220, Value = _thumbSettings.DecodeWidth };
            _thumbWidthSlider.ValueChanged += ThumbWidthSlider_ValueChanged;
            _thumbWidthValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            UpdateThumbWidthValue();
            thumbRow.Children.Add(new TextBlock { Text = L("Settings.Thumbnail.DecodeWidth", "디코드 폭(px):"), VerticalAlignment = VerticalAlignment.Center });
            thumbRow.Children.Add(_thumbWidthSlider);
            thumbRow.Children.Add(_thumbWidthValue);
            stack.Children.Add(thumbRow);
        }

        private void BuildCacheSettingsSection(StackPanel stack)
        {
            stack.Children.Add(new TextBlock { Text = L("Settings.Cache.Header", "이미지 캐시"), FontSize = 20, Margin = new Thickness(0, 24, 0, 0), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            _cacheSummary = new TextBlock { Text = string.Empty, Margin = new Thickness(0, 0, 0, 8) };
            stack.Children.Add(_cacheSummary);

            var limitsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            _cacheMaxCountBox = new NumberBox { Header = L("Settings.Cache.MaxImageCount", "최대 이미지 수"), Width = 140, Minimum = 100, Maximum = 50000, Value = ImageCacheService.Instance.MaxMemoryImageCount };
            _cacheMaxBytesBox = new NumberBox { Header = L("Settings.Cache.MaxSizeGb", "최대 용량(GB)"), Width = 140, Minimum = 1, Maximum = 32, Value = Math.Round(ImageCacheService.Instance.MaxMemoryImageBytes / 1024d / 1024d / 1024d) };
            var applyBtn = new Button { Content = L("Settings.Common.Apply", "적용") };
            applyBtn.Click += ApplyCacheLimit_Click;
            limitsPanel.Children.Add(_cacheMaxCountBox);
            limitsPanel.Children.Add(_cacheMaxBytesBox);
            limitsPanel.Children.Add(applyBtn);
            stack.Children.Add(limitsPanel);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var refreshBtn = new Button { Content = L("Settings.Common.Refresh", "새로고침") };
            refreshBtn.Click += (s, e) => RefreshCacheView();
            var clearAllBtn = new Button { Content = L("Settings.Cache.ClearAll", "캐시 전체 삭제") };
            clearAllBtn.Click += (s, e) => { ImageCacheService.Instance.ClearMemoryImages(); RefreshCacheView(); };
            btnRow.Children.Add(refreshBtn);
            btnRow.Children.Add(clearAllBtn);
            stack.Children.Add(btnRow);

            _cacheEntries = new ObservableCollection<CacheEntryView>();
            _cacheList = new ListView { Height = 280, SelectionMode = ListViewSelectionMode.Single, ItemsSource = _cacheEntries };
            _cacheList.ItemTemplate = (DataTemplate)Resources["CacheEntryItemTemplate"];
            _cacheList.ContainerContentChanging += CacheList_ContainerContentChanging;
            stack.Children.Add(_cacheList);
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
            _cacheSummary.Text = string.Format(L("Settings.Cache.Summary", "합계: {0} images, {1:F1} MB"), cnt, (bytes / 1024d / 1024d));
        }

        private void UpdateTagFontValue() => _tagFontValue.Text = Math.Round(_tagFontSlider.Value).ToString();

        private sealed class OllamaModelInfo
        {
            public string Name { get; init; } = string.Empty;
            public bool SupportsThinking { get; init; }
        }

        private static async Task<List<OllamaModelInfo>> GetOllamaOcrModelInfosAsync(string endpoint)
        {
            using var http = new HttpClient();
            endpoint = LlmEndpointCompatibility.NormalizeEndpoint(endpoint);
            var flavor = await LlmEndpointCompatibility.DetectApiFlavorAsync(http, endpoint, CancellationToken.None).ConfigureAwait(false);
            var modelIds = await LlmEndpointCompatibility.GetModelIdsAsync(http, endpoint, CancellationToken.None).ConfigureAwait(false);

            Debug.WriteLine($"[SettingsPage] OCR model discovery start: endpoint={endpoint}, flavor={flavor}, modelCount={modelIds.Count}");
            Debug.WriteLine($"[SettingsPage] OCR model ids: [{string.Join(", ", modelIds)}]");

            var result = new List<OllamaModelInfo>();
            foreach (var id in modelIds)
            {
                var capabilities = await GetOllamaModelCapabilitiesAsync(http, endpoint, id, flavor).ConfigureAwait(false);
                Debug.WriteLine($"[SettingsPage] OCR model candidate: id={id}, vision={capabilities.Vision}, tools={capabilities.Tools}, thinking={capabilities.Thinking}");
                if (!capabilities.Vision || (flavor == LlmApiFlavor.Ollama && !capabilities.Tools))
                {
                    string filterReason = !capabilities.Vision ? "Vision=false" : "Tools=false";
                    Debug.WriteLine($"[SettingsPage] OCR model filtered out: id={id}, reason={filterReason}");
                    continue;
                }

                result.Add(new OllamaModelInfo
                {
                    Name = id,
                    SupportsThinking = capabilities.Thinking
                });
            }

            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            Debug.WriteLine($"[SettingsPage] OCR model discovery result: count={result.Count}, ids=[{string.Join(", ", result.Select(m => m.Name))}]");
            return result;
        }

        private static async Task<(bool Vision, bool Tools, bool Thinking)> GetOllamaModelCapabilitiesAsync(HttpClient http, string endpoint, string model)
        {
            return await GetOllamaModelCapabilitiesAsync(http, endpoint, model, await LlmEndpointCompatibility.DetectApiFlavorAsync(http, endpoint, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }

        private static async Task<(bool Vision, bool Tools, bool Thinking)> GetOllamaModelCapabilitiesAsync(HttpClient http, string endpoint, string model, LlmApiFlavor flavor)
        {
            endpoint = LlmEndpointCompatibility.NormalizeEndpoint(endpoint);
            if (flavor == LlmApiFlavor.OpenAiCompatible)
            {
                var capabilities = await LlmEndpointCompatibility.GetOpenAiCompatibleModelCapabilitiesAsync(http, endpoint, model, CancellationToken.None).ConfigureAwait(false);
                return (capabilities.Vision, true, capabilities.Thinking);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/api/show")
            {
                Content = new StringContent(LlmEndpointCompatibility.BuildModelRequestJson(model), System.Text.Encoding.UTF8, "application/json")
            };

            using var response = await http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            bool vision = HasOllamaCapability(doc.RootElement, "vision");
            bool tools = HasOllamaCapability(doc.RootElement, "tools")
                         || HasOllamaCapability(doc.RootElement, "tool")
                         || HasOllamaCapability(doc.RootElement, "tool_calling")
                         || HasOllamaCapability(doc.RootElement, "tool-calling");
            bool thinking = HasOllamaCapability(doc.RootElement, "thinking")
                            || HasOllamaCapability(doc.RootElement, "think");
            return (vision, tools, thinking);
        }

        private static bool HasOllamaCapability(JsonElement root, string capability)
        {
            static bool ContainsCap(JsonElement caps, string cap)
            {
                if (caps.ValueKind != JsonValueKind.Array) return false;
                foreach (var item in caps.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String
                        && string.Equals(item.GetString(), cap, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            if (root.TryGetProperty("capabilities", out var caps) && ContainsCap(caps, capability))
                return true;

            if (root.TryGetProperty("details", out var details)
                && details.ValueKind == JsonValueKind.Object
                && details.TryGetProperty("capabilities", out var nested)
                && ContainsCap(nested, capability))
                return true;

            return false;
        }

        private static async Task<List<string>> GetOllamaModelIdsAsync(string endpoint)
        {
            using var http = new HttpClient();
            return await LlmEndpointCompatibility.GetModelIdsAsync(http, endpoint, CancellationToken.None).ConfigureAwait(false);
        }

        private static UIElement Row(string label, UIElement inner)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            p.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            p.Children.Add(inner);
            return p;
        }

        private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            _isInitializingSettingsUi = true;
            var appLanguage = SettingsProvider.Get("AppLanguage", "auto");
            var appLanguageItem = _appLanguageCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, appLanguage, StringComparison.OrdinalIgnoreCase))
                ?? _appLanguageCombo.Items.OfType<ComboBoxItem>().FirstOrDefault();
            if (appLanguageItem != null)
                _appLanguageCombo.SelectedItem = appLanguageItem;

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
            _ocrStructuredOutputToggle.IsOn = _ocr.OllamaStructuredOutputEnabled;
            _ocrOllamaTemperatureBox.Value = _ocr.OllamaTemperature;
            _llamaServerMaxConcurrentBox.Value = _ocr.LlamaServerMaxConcurrentRequests;
            _llamaServerSlotEraseToggle.IsOn = _ocr.LlamaServerSlotEraseEnabled;
            _hybridTextParallelBox.Value = _ocr.HybridTextExtractionParallelism;
            _hybridOnnxFallbackToggle.IsOn = _ocr.HybridOnnxFallbackEnabled;
            _onnxEpModeCombo.SelectedIndex = (int)_ocr.OnnxExecutionProviderMode;
            _onnxEpManualListBox.Text = _ocr.OnnxExecutionProviderManualList;
            _onnxEpManualListBox.IsEnabled = _ocr.OnnxExecutionProviderMode == OcrService.OnnxEpRegistrationMode.Manual;
            _onnxEpStatusText.Text = _ocr.OnnxExecutionProviderStatus;
            _onnxEpCompatibleListText.Text = string.Empty;
            _onnxTrtCudaGraphToggle.IsOn = _ocr.OnnxTrtRtxEnableCudaGraph;
            _onnxTrtRuntimeCachePathBox.Text = _ocr.OnnxTrtRtxRuntimeCachePath;
            _onnxUseEpContextToggle.IsOn = _ocr.OnnxUseEpContextModel;
            _onnxAutoCompileEpContextToggle.IsOn = _ocr.OnnxAutoCompileEpContextModel;
            UpdateDocLayoutModelStatus();

            var thinkingItem = _ocrThinkingLevelCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, _ocr.OllamaThinkingLevel, StringComparison.OrdinalIgnoreCase))
                ?? _ocrThinkingLevelCombo.Items.OfType<ComboBoxItem>().FirstOrDefault();
            if (thinkingItem != null)
                _ocrThinkingLevelCombo.SelectedItem = thinkingItem;

            if (!string.IsNullOrWhiteSpace(_ocr.OllamaModel))
            {
                var selected = _ocrOllamaModelCombo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => string.Equals(i.Tag as string, _ocr.OllamaModel, StringComparison.OrdinalIgnoreCase));
                if (selected == null)
                {
                    selected = new ComboBoxItem { Content = _ocr.OllamaModel, Tag = _ocr.OllamaModel };
                    _ocrOllamaModelCombo.Items.Add(selected);
                }
                _ocrOllamaModelCombo.SelectedItem = selected;
            }

            _ocrAdjacentPrefetchToggle.IsOn = _ocr.PrefetchAdjacentPagesEnabled;
            _ocrAdjacentPrefetchCountBox.Value = _ocr.PrefetchAdjacentPageCount;
            _ocrAdjacentPrefetchCountBox.IsEnabled = _ocrAdjacentPrefetchToggle.IsOn;

            var translationSettings = TranslationSettingsService.Instance;
            _translationAdjacentPrefetchToggle.IsOn = translationSettings.PrefetchAdjacentPagesEnabled;
            _translationAdjacentPrefetchCountBox.Value = translationSettings.PrefetchAdjacentPageCount;
            _translationAdjacentPrefetchCountBox.IsEnabled = _translationAdjacentPrefetchToggle.IsOn;
            _translationOverlayFontSlider.Value = translationSettings.OverlayFontSize;
            UpdateTranslationOverlayFontValue();
            _translationOverlayBoxScaleHorizontalSlider.Value = translationSettings.OverlayBoxScaleHorizontal;
            UpdateTranslationOverlayBoxScaleHorizontalValue();
            _translationOverlayBoxScaleVerticalSlider.Value = translationSettings.OverlayBoxScaleVertical;
            UpdateTranslationOverlayBoxScaleVerticalValue();
            UpdateOcrThinkingComboAvailability();
            UpdateOllamaSettingsVisibility();
            UpdateOcrGroupingAvailability();
            RefreshCacheView();
            await RefreshLlamaServerParallelismBoundsAsync().ConfigureAwait(true);
            _isInitializingSettingsUi = false;
        }

        private void OcrBackendCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_ocrBackendCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                _ocr.SetBackend(tag == "vlm" ? OcrService.OcrBackend.Vlm : OcrService.OcrBackend.Hybrid);
                UpdateOllamaSettingsVisibility();
                UpdateOcrGroupingAvailability();
            }
        }

        private void OnnxEpRefreshCompatibleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
                return;

            button.IsEnabled = false;
            _onnxEpStatusText.Text = L("Settings.Common.Enumerating", "Enumerating...");
            try
            {
                var providers = _ocr.GetCompatibleOnnxExecutionProviders();
                _onnxEpCompatibleListText.Text = providers.Count == 0
                    ? L("Settings.Common.None", "(none)")
                    : string.Join(", ", providers.Select(p => $"{p.Name} ({p.ReadyState})"));
                _onnxEpStatusText.Text = _ocr.OnnxExecutionProviderStatus;
            }
            catch (Exception ex)
            {
                _onnxEpStatusText.Text = L("Settings.Common.FailedPrefix", "Failed: ") + ex.Message;
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        private async void OllamaEndpointBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var text = _ollamaEndpointBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
                _ocr.SetOllamaEndpoint(text);

            await RefreshLlamaServerParallelismBoundsAsync().ConfigureAwait(true);
        }

        private async void OcrOllamaModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_ocrOllamaModelCombo.SelectedItem is ComboBoxItem item && item.Tag is string model)
                _ocr.SetOllamaModel(model);

            UpdateOcrThinkingComboAvailability();
            await RefreshLlamaServerParallelismBoundsAsync().ConfigureAwait(true);
        }

        private void OcrThinkingLevelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_ocrThinkingLevelCombo.SelectedItem is ComboBoxItem item && item.Tag is string level)
                _ocr.SetOllamaThinkingLevel(level);
        }

        private void OcrStructuredOutputToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _ocr.SetOllamaStructuredOutputEnabled(_ocrStructuredOutputToggle.IsOn);
            UpdateOcrGroupingAvailability();
        }

        private void OcrOllamaTemperatureBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (double.IsNaN(sender.Value)) return;
            _ocr.SetOllamaTemperature(sender.Value);
        }

        private void HybridTextParallelBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (double.IsNaN(sender.Value)) return;
            int value = (int)Math.Clamp(Math.Round(sender.Value), 1, 8);
            if (Math.Abs(sender.Value - value) > 0.001)
                sender.Value = value;
            _ocr.SetHybridTextExtractionParallelism(value);
        }

        private void HybridOnnxFallbackToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _ocr.SetHybridOnnxFallbackEnabled(_hybridOnnxFallbackToggle.IsOn);
        }

        private async void DocLayoutModelDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            _docLayoutModelDownloadButton.IsEnabled = false;
            _docLayoutModelStatusText.Text = L("Settings.Common.Downloading", "Downloading...");

            try
            {
                await _ocr.DownloadDocLayoutModelAsync(CancellationToken.None);
                UpdateDocLayoutModelStatus();
            }
            catch (Exception ex)
            {
                _docLayoutModelStatusText.Text = L("Settings.Common.DownloadFailedPrefix", "Download failed: ") + ex.Message;
            }
            finally
            {
                _docLayoutModelDownloadButton.IsEnabled = true;
            }
        }

        private void UpdateDocLayoutModelStatus()
        {
            bool installed = _ocr.IsDocLayoutModelInstalled();
            bool epContextInstalled = _ocr.IsDocLayoutEpContextModelInstalled();
            _docLayoutModelStatusText.Text = installed
                ? (epContextInstalled
                    ? L("Settings.Ocr.DocLayout.InstalledWithEp", "Installed + EP context ready")
                    : L("Settings.Ocr.DocLayout.Installed", "Installed"))
                : L("Settings.Ocr.DocLayout.NotInstalled", "Not installed (required for Hybrid OCR).");
        }

        private void OnnxEpModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_onnxEpModeCombo.SelectedIndex < 0) return;
            var mode = _onnxEpModeCombo.SelectedIndex == (int)OcrService.OnnxEpRegistrationMode.Manual
                ? OcrService.OnnxEpRegistrationMode.Manual
                : OcrService.OnnxEpRegistrationMode.Auto;
            _ocr.SetOnnxExecutionProviderMode(mode);
            _onnxEpManualListBox.IsEnabled = mode == OcrService.OnnxEpRegistrationMode.Manual;
            _onnxEpStatusText.Text = _ocr.OnnxExecutionProviderStatus;
        }

        private void OnnxEpManualListBox_LostFocus(object sender, RoutedEventArgs e)
        {
            _ocr.SetOnnxExecutionProviderManualList(_onnxEpManualListBox.Text);
        }

        private async void OnnxEpRegisterNowButton_Click(object sender, RoutedEventArgs e)
        {
            _onnxEpRegisterNowButton.IsEnabled = false;
            _onnxEpStatusText.Text = L("Settings.Common.Registering", "Registering...");
            try
            {
                await _ocr.EnsureOnnxExecutionProvidersReadyAsync(CancellationToken.None, force: true);
                _onnxEpStatusText.Text = _ocr.OnnxExecutionProviderStatus;
            }
            catch (Exception ex)
            {
                _onnxEpStatusText.Text = L("Settings.Common.FailedPrefix", "Failed: ") + ex.Message;
            }
            finally
            {
                _onnxEpRegisterNowButton.IsEnabled = true;
            }
        }

        private void UpdateOcrThinkingComboAvailability()
        {
            if (_ocrOllamaModelCombo.SelectedItem is ComboBoxItem item
                && item.Tag is string model
                && _ocrModelThinkingSupport.TryGetValue(model, out bool supportsThinking))
            {
                _ocrThinkingLevelCombo.IsEnabled = supportsThinking;
                _ocrOllamaModelStatus.Text = supportsThinking
                    ? L("Settings.Ocr.Thinking.Supported", "선택 모델: Thinking 지원")
                    : L("Settings.Ocr.Thinking.NotSupported", "선택 모델: Thinking 미지원");
                return;
            }

            _ocrThinkingLevelCombo.IsEnabled = true;
        }

        private async Task RefreshOcrOllamaModelsAsync(Button triggerButton)
        {
            triggerButton.IsEnabled = false;
            _ocrOllamaModelStatus.Text = L("Settings.Ocr.LoadingModels", "모델 조회 중...");

            try
            {
                var endpoint = string.IsNullOrWhiteSpace(_ollamaEndpointBox.Text)
                    ? "http://localhost:11434"
                    : _ollamaEndpointBox.Text.Trim().TrimEnd('/');
                _ocr.SetOllamaEndpoint(endpoint);

                var current = _ocrOllamaModelCombo.SelectedItem is ComboBoxItem selected
                    ? (selected.Tag as string)
                    : _ocr.OllamaModel;

                var models = await GetOllamaOcrModelInfosAsync(endpoint);

                _ocrModelThinkingSupport.Clear();
                _ocrOllamaModelCombo.Items.Clear();
                foreach (var model in models)
                {
                    _ocrModelThinkingSupport[model.Name] = model.SupportsThinking;
                    _ocrOllamaModelCombo.Items.Add(new ComboBoxItem { Content = model.Name, Tag = model.Name });
                }

                var toSelect = _ocrOllamaModelCombo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => string.Equals(i.Tag as string, current, StringComparison.OrdinalIgnoreCase));
                if (toSelect != null)
                    _ocrOllamaModelCombo.SelectedItem = toSelect;
                else if (_ocrOllamaModelCombo.Items.Count > 0)
                    _ocrOllamaModelCombo.SelectedIndex = 0;

                _ocrOllamaModelStatus.Text = string.Format(L("Settings.Ocr.ModelsCountVisionTool", "{0}개 (Vision+Tool)"), models.Count);
                UpdateOcrThinkingComboAvailability();
                await RefreshLlamaServerParallelismBoundsAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _ocrOllamaModelStatus.Text = L("Settings.Common.FailedPrefix", "Failed: ") + ex.Message;
            }
            finally
            {
                triggerButton.IsEnabled = true;
            }
        }

        private async Task RefreshLlamaServerParallelismBoundsAsync()
        {
            double previousMaximum = _llamaServerMaxConcurrentBox.Maximum;

            await _ocr.RefreshLlamaServerSlotLimitAsync().ConfigureAwait(true);

            int maximum = _ocr.LlamaServerMaxConcurrentRequestsUpperBound;
            _llamaServerMaxConcurrentBox.Maximum = maximum;

            double desiredValue = Math.Clamp(_ocr.LlamaServerMaxConcurrentRequests, 1, maximum);
            if (Math.Abs(_llamaServerMaxConcurrentBox.Value - desiredValue) > 0.001 || Math.Abs(previousMaximum - maximum) > 0.001)
                _llamaServerMaxConcurrentBox.Value = desiredValue;
        }

        private void UpdateOllamaSettingsVisibility()
        {
            _ollamaSettingsPanel.Visibility = Visibility.Visible;

            bool isVlm = _ocr.Backend == OcrService.OcrBackend.Vlm;
            bool isHybrid = _ocr.Backend == OcrService.OcrBackend.Hybrid;
            _ocrOllamaModelCombo.IsEnabled = isVlm || isHybrid;
            _ocrStructuredOutputToggle.IsEnabled = isVlm;
            _ocrOllamaTemperatureBox.IsEnabled = isVlm;
            _hybridTextParallelBox.IsEnabled = isHybrid;
            _hybridOnnxFallbackToggle.IsEnabled = isHybrid;
        }

        private void UpdateOcrGroupingAvailability()
        {
            _groupCombo.IsEnabled = false;
            _writingCombo.IsEnabled = false;
            _langCombo.IsEnabled = false;
        }

        private async void AppLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingSettingsUi) return;
            if (!IsLoaded) return;
            if (_appLanguageCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;

            SettingsProvider.Set("AppLanguage", tag);

            if (string.Equals(tag, "auto", StringComparison.OrdinalIgnoreCase))
                ApplicationLanguages.PrimaryLanguageOverride = string.Empty;
            else
                ApplicationLanguages.PrimaryLanguageOverride = tag;

            var cultureName = string.IsNullOrWhiteSpace(ApplicationLanguages.PrimaryLanguageOverride)
                ? CultureInfo.CurrentUICulture.Name
                : ApplicationLanguages.PrimaryLanguageOverride;

            try
            {
                var culture = new CultureInfo(cultureName);
                CultureInfo.DefaultThreadCurrentCulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
            }
            catch
            {
            }

            if (MainWindow.RootViewModel != null)
                MainWindow.RootViewModel.RefreshLocalizedTexts();

            if (Application.Current is App app && app.MainWindow != null)
                app.MainWindow.Title = LocalizationHelper.GetString("FooterBrandText.Text", "Manga Viewer");

            var dialog = new ContentDialog
            {
                Title = LocalizationHelper.GetString("Settings.AppLanguage.Restart.Title", "언어 변경"),
                Content = LocalizationHelper.GetString("Settings.AppLanguage.Restart.Message", "일부 UI는 앱을 다시 시작하면 완전히 적용됩니다."),
                PrimaryButtonText = L("Settings.Common.Ok", "OK"),
                XamlRoot = this.XamlRoot
            };

            try
            {
                await dialog.ShowAsync();
            }
            catch
            {
            }
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

        private void TranslationOverlayFontSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            TranslationSettingsService.Instance.OverlayFontSize = e.NewValue;
            UpdateTranslationOverlayFontValue();
        }

        private void TranslationOverlayBoxScaleHorizontalSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            TranslationSettingsService.Instance.OverlayBoxScaleHorizontal = e.NewValue;
            UpdateTranslationOverlayBoxScaleHorizontalValue();
        }

        private void TranslationOverlayBoxScaleVerticalSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            TranslationSettingsService.Instance.OverlayBoxScaleVertical = e.NewValue;
            UpdateTranslationOverlayBoxScaleVerticalValue();
        }

        private void UpdateTranslationOverlayFontValue()
            => _translationOverlayFontValue.Text = Math.Round(_translationOverlayFontSlider.Value).ToString();

        private void UpdateTranslationOverlayBoxScaleHorizontalValue()
            => _translationOverlayBoxScaleHorizontalValue.Text = $"x{_translationOverlayBoxScaleHorizontalSlider.Value:F2}";

        private void UpdateTranslationOverlayBoxScaleVerticalValue()
            => _translationOverlayBoxScaleVerticalValue.Text = $"x{_translationOverlayBoxScaleVerticalSlider.Value:F2}";
    }
}
