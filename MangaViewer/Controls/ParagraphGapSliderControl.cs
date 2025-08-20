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
        private bool _suppress;

        public ParagraphGapSliderControl()
        {
            Orientation = Orientation.Horizontal;
            Spacing = 8;
            Children.Add(new TextBlock { Text = "문단 간격", VerticalAlignment = VerticalAlignment.Center });
            _verticalToggle = new ToggleSwitch { Header = "방향", IsOn = false, OffContent = "가로", OnContent = "세로", MinWidth = 84 };
            _verticalToggle.Toggled += (_,__) => Refresh();
            _slider = new Slider { Minimum = 0.1, Maximum = 3.0, StepFrequency = 0.05, Width = 180 };
            _slider.ValueChanged += Slider_ValueChanged;
            _valueText = new TextBlock { Width = 48, VerticalAlignment = VerticalAlignment.Center };
            Children.Add(_verticalToggle);
            Children.Add(_slider);
            Children.Add(_valueText);
            Refresh();
        }

        private void Refresh()
        {
            _isVerticalTarget = _verticalToggle.IsOn;
            double val = _isVerticalTarget ? _ocr.ParagraphGapFactorVertical : _ocr.ParagraphGapFactorHorizontal;
            _suppress = true; // avoid triggering SetParagraphGapFactor again
            _slider.Value = val;
            _suppress = false;
            _valueText.Text = val.ToString("0.00");
        }

        private void Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) return;
            if (e.NewValue <= 0) return;
            if (_isVerticalTarget) _ocr.SetParagraphGapFactorVertical(e.NewValue); else _ocr.SetParagraphGapFactorHorizontal(e.NewValue);
            _valueText.Text = e.NewValue.ToString("0.00");
        }
    }
}
