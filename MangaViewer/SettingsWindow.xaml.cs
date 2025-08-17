using System;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MangaViewer.Services;
using MangaViewer.Controls;

namespace MangaViewer
{
    // Fallback simple dialog style settings UI (ContentDialog) to avoid XAML code-behind InitializeComponent issues for separate Window.
    public sealed class SettingsWindow : ContentDialog
    {
        private readonly OcrService _ocr = OcrService.Instance;
        private ComboBox _langCombo = null!;
        private ComboBox _groupCombo = null!;
        private ComboBox _writingCombo = null!;
        private ParagraphGapSliderControl _gapControl = null!;

        public SettingsWindow()
        {
            Title = "����";
            PrimaryButtonText = "�ݱ�";
            PrimaryButtonClick += (_, __) => Hide();
            BuildContent();
        }

        private void BuildContent()
        {
            var stack = new StackPanel { Spacing = 12 };
            stack.Children.Add(new TextBlock { Text = "OCR ����", FontSize = 18, FontWeight = FontWeights.SemiBold });

            _langCombo = new ComboBox { Width = 140 };
            _langCombo.Items.Add(new ComboBoxItem { Content = "�Ϻ���", Tag = "ja" });
            _langCombo.Items.Add(new ComboBoxItem { Content = "�ѱ���", Tag = "ko" });
            _langCombo.Items.Add(new ComboBoxItem { Content = "����", Tag = "en" });
            _langCombo.SelectedIndex = _ocr.CurrentLanguage switch { "ko" => 1, "en" => 2, _ => 0 };
            _langCombo.SelectionChanged += LangCombo_SelectionChanged;
            stack.Children.Add(WrapRow("���:", _langCombo));

            _groupCombo = new ComboBox { Width = 140 };
            _groupCombo.Items.Add(new ComboBoxItem { Content = "�ܾ�", Tag = OcrService.OcrGrouping.Word.ToString() });
            _groupCombo.Items.Add(new ComboBoxItem { Content = "����", Tag = OcrService.OcrGrouping.Line.ToString() });
            _groupCombo.Items.Add(new ComboBoxItem { Content = "����", Tag = OcrService.OcrGrouping.Paragraph.ToString() });
            _groupCombo.SelectedIndex = (int)_ocr.GroupingMode;
            _groupCombo.SelectionChanged += GroupCombo_SelectionChanged;
            stack.Children.Add(WrapRow("�׷�:", _groupCombo));

            _writingCombo = new ComboBox { Width = 160 };
            _writingCombo.Items.Add(new ComboBoxItem { Content = "�ڵ�", Tag = OcrService.WritingMode.Auto.ToString() });
            _writingCombo.Items.Add(new ComboBoxItem { Content = "����", Tag = OcrService.WritingMode.Horizontal.ToString() });
            _writingCombo.Items.Add(new ComboBoxItem { Content = "����", Tag = OcrService.WritingMode.Vertical.ToString() });
            _writingCombo.SelectedIndex = (int)_ocr.TextWritingMode;
            _writingCombo.SelectionChanged += WritingCombo_SelectionChanged;
            stack.Children.Add(WrapRow("�������:", _writingCombo));

            _gapControl = new ParagraphGapSliderControl();
            stack.Children.Add(_gapControl);

            Content = new ScrollViewer { Content = stack };
        }

        private static UIElement WrapRow(string label, UIElement control)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(control);
            return panel;
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

        public async Task ShowFor(FrameworkElement owner)
        {
            XamlRoot = owner.XamlRoot;
            await this.ShowAsync();
        }
    }
}
