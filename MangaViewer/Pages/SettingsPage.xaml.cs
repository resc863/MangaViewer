using MangaViewer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Linq;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media; // VisualTreeHelper

namespace MangaViewer.Pages
{
    public sealed partial class SettingsPage : Page
    {
        private readonly OcrService _ocr = OcrService.Instance;
        private readonly TagSettingsService _tagSettings = TagSettingsService.Instance;
        private ComboBox _langCombo = null!;
        private ComboBox _groupCombo = null!;
        private ComboBox _writingCombo = null!;
        private Slider _tagFontSlider = null!;
        private TextBlock _tagFontValue = null!;

        private ListView _cacheList = null!;
        private TextBlock _cacheSummary = null!;
        private NumberBox _cacheMaxCountBox = null!;
        private NumberBox _cacheMaxBytesBox = null!;
        private ObservableCollection<CacheEntryView> _cacheEntries = new();

        private sealed class CacheEntryView { public string GalleryId { get; set; } = string.Empty; public int Count { get; set; } }

        public SettingsPage()
        {
            BuildUi();
            Loaded += SettingsPage_Loaded;
        }

        private void BuildUi()
        {
            var stack = new StackPanel { Spacing = 18, Padding = new Thickness(24) };
            stack.Children.Add(new TextBlock { Text = "OCR 설정", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

            _langCombo = new ComboBox { Width = 160 };
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
            stack.Children.Add(Row("쓰기 방향:", _writingCombo));

            stack.Children.Add(new Controls.ParagraphGapSliderControl());

            _tagFontSlider = new Slider { Minimum = 8, Maximum = 32, Width = 220, Value = _tagSettings.TagFontSize };
            _tagFontSlider.ValueChanged += TagFontSlider_ValueChanged;
            _tagFontValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            UpdateTagFontValue();
            var fontRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            fontRow.Children.Add(new TextBlock { Text = "태그 폰트 크기:", VerticalAlignment = VerticalAlignment.Center });
            fontRow.Children.Add(_tagFontSlider);
            fontRow.Children.Add(_tagFontValue);
            stack.Children.Add(fontRow);

            stack.Children.Add(new TextBlock { Text = "이미지 캐시", FontSize = 20, Margin = new Thickness(0,24,0,0), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            _cacheSummary = new TextBlock { Text = string.Empty, Margin = new Thickness(0,0,0,8) };
            stack.Children.Add(_cacheSummary);

            var limitsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            _cacheMaxCountBox = new NumberBox { Header = "최대 이미지 수", Width = 140, Minimum = 100, Maximum = 50000, Value = ImageCacheService.Instance.MaxMemoryImageCount };
            _cacheMaxBytesBox = new NumberBox { Header = "최대 바이트(GB)", Width = 140, Minimum = 1, Maximum = 32, Value = Math.Round(ImageCacheService.Instance.MaxMemoryImageBytes / 1024d / 1024d / 1024d) };
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
            _cacheSummary.Text = $"전체: {cnt} images, {(bytes/1024d/1024d):F1} MB";
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
            _langCombo.SelectedIndex = _ocr.CurrentLanguage switch { "ko" => 1, "en" => 2, _ => 0 };
            _groupCombo.SelectedIndex = (int)_ocr.GroupingMode;
            _writingCombo.SelectedIndex = (int)_ocr.TextWritingMode;
            RefreshCacheView();
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
