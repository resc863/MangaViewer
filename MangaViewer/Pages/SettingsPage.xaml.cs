using MangaViewer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;

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

        public SettingsPage()
        {
            BuildUi();
            Loaded += SettingsPage_Loaded;
        }

        private void BuildUi()
        {
            var stack = new StackPanel { Spacing = 16, Padding = new Thickness(24) };
            stack.Children.Add(new TextBlock { Text = "OCR 설정", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

            _langCombo = new ComboBox { Width = 160 };
            _langCombo.Items.Add(new ComboBoxItem { Content = "일본어", Tag = "ja" });
            _langCombo.Items.Add(new ComboBoxItem { Content = "한국어", Tag = "ko" });
            _langCombo.Items.Add(new ComboBoxItem { Content = "영어", Tag = "en" });
            _langCombo.SelectionChanged += LangCombo_SelectionChanged;
            stack.Children.Add(Row("언어:", _langCombo));

            _groupCombo = new ComboBox { Width = 160 };
            _groupCombo.Items.Add(new ComboBoxItem { Content = "단어", Tag = OcrService.OcrGrouping.Word.ToString() });
            _groupCombo.Items.Add(new ComboBoxItem { Content = "라인", Tag = OcrService.OcrGrouping.Line.ToString() });
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

            // Tag font size slider
            _tagFontSlider = new Slider { Minimum = 8, Maximum = 32, Width = 220, Value = _tagSettings.TagFontSize };
            _tagFontSlider.ValueChanged += TagFontSlider_ValueChanged;
            _tagFontValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            UpdateTagFontValue();
            var fontRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            fontRow.Children.Add(new TextBlock { Text = "태그 글자 크기:", VerticalAlignment = VerticalAlignment.Center });
            fontRow.Children.Add(_tagFontSlider);
            fontRow.Children.Add(_tagFontValue);
            stack.Children.Add(fontRow);

            Content = new ScrollViewer { Content = stack };
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

        private void TagFontSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            _tagSettings.TagFontSize = e.NewValue;
            UpdateTagFontValue();
        }
    }
}
