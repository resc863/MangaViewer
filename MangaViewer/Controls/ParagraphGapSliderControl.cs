using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using MangaViewer.Services;

namespace MangaViewer.Controls
{
    public sealed class ParagraphGapSliderControl : StackPanel
    {
        private readonly Slider _slider;
        private readonly TextBlock _valueText;
        private readonly ToggleSwitch _verticalToggle;
        private readonly OcrService _ocr = OcrService.Instance;
        private bool _isVerticalTarget;

        public ParagraphGapSliderControl()
        {
            Orientation = Orientation.Horizontal;
            Spacing = 8;
            Children.Add(new TextBlock { Text = "¹®´Ü Gap", VerticalAlignment = VerticalAlignment.Center });
            _verticalToggle = new ToggleSwitch { Header = "Vertical", IsOn = false, OffContent = "H", OnContent = "V", MinWidth = 60 };
            _verticalToggle.Toggled += (_,__) => Refresh();
            _slider = new Slider { Minimum = 0.1, Maximum = 1.5, StepFrequency = 0.05, Width = 160 };
            _slider.ValueChanged += Slider_ValueChanged;
            _valueText = new TextBlock { Width = 40, VerticalAlignment = VerticalAlignment.Center };
            Children.Add(_verticalToggle);
            Children.Add(_slider);
            Children.Add(_valueText);
            Refresh();
        }

        private void Refresh()
        {
            _isVerticalTarget = _verticalToggle.IsOn;
            double val = _isVerticalTarget ? _ocr.ParagraphGapFactorVertical : _ocr.ParagraphGapFactorHorizontal;
            _slider.Value = val;
            _valueText.Text = val.ToString("0.00");
        }

        private void Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (e.NewValue <= 0) return;
            if (_isVerticalTarget) _ocr.SetParagraphGapFactorVertical(e.NewValue); else _ocr.SetParagraphGapFactorHorizontal(e.NewValue);
            _valueText.Text = e.NewValue.ToString("0.00");
        }
    }
}
