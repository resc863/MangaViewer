using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using Windows.Foundation;

namespace MangaViewer.Controls
{
    /// <summary>
    /// Lightweight wrap panel (no virtualization). Set ForceWidth from parent to guarantee correct wrapping.
    /// </summary>
    public sealed class TagWrapPanel : Panel
    {
        public double HorizontalSpacing { get => (double)GetValue(HorizontalSpacingProperty); set => SetValue(HorizontalSpacingProperty, value); }
        public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(nameof(HorizontalSpacing), typeof(double), typeof(TagWrapPanel), new PropertyMetadata(4.0, OnLayoutChanged));

        public double VerticalSpacing { get => (double)GetValue(VerticalSpacingProperty); set => SetValue(VerticalSpacingProperty, value); }
        public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(nameof(VerticalSpacing), typeof(double), typeof(TagWrapPanel), new PropertyMetadata(4.0, OnLayoutChanged));

        public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
        public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(TagWrapPanel), new PropertyMetadata(0.0, OnLayoutChanged));

        public double MinItemHeight { get => (double)GetValue(MinItemHeightProperty); set => SetValue(MinItemHeightProperty, value); }
        public static readonly DependencyProperty MinItemHeightProperty = DependencyProperty.Register(nameof(MinItemHeight), typeof(double), typeof(TagWrapPanel), new PropertyMetadata(0.0, OnLayoutChanged));

        public double MaxItemWidth { get => (double)GetValue(MaxItemWidthProperty); set => SetValue(MaxItemWidthProperty, value); }
        public static readonly DependencyProperty MaxItemWidthProperty = DependencyProperty.Register(nameof(MaxItemWidth), typeof(double), typeof(TagWrapPanel), new PropertyMetadata(0.0, OnLayoutChanged));

        /// <summary>
        /// If > 0 this width is used for wrapping instead of availableSize.Width (ensures finite width inside ScrollViewer/StackPanel scenarios).
        /// </summary>
        public double ForceWidth { get => (double)GetValue(ForceWidthProperty); set => SetValue(ForceWidthProperty, value); }
        public static readonly DependencyProperty ForceWidthProperty = DependencyProperty.Register(nameof(ForceWidth), typeof(double), typeof(TagWrapPanel), new PropertyMetadata(0.0, OnLayoutChanged));

        public double FallbackWrapWidth { get => (double)GetValue(FallbackWrapWidthProperty); set => SetValue(FallbackWrapWidthProperty, value); }
        public static readonly DependencyProperty FallbackWrapWidthProperty = DependencyProperty.Register(nameof(FallbackWrapWidth), typeof(double), typeof(TagWrapPanel), new PropertyMetadata(600.0, OnLayoutChanged));

        private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        { if (d is TagWrapPanel p) p.InvalidateMeasure(); }

        private readonly List<Rect> _childRects = new();

        protected override Size MeasureOverride(Size availableSize)
        {
            _childRects.Clear();
            double wrapWidth = ForceWidth > 0 ? ForceWidth : (!double.IsInfinity(availableSize.Width) && availableSize.Width > 0 ? availableSize.Width : FallbackWrapWidth);
            double minW = MinItemWidth > 0 ? MinItemWidth : 0;
            double minH = MinItemHeight > 0 ? MinItemHeight : 0;
            double maxW = MaxItemWidth > 0 ? MaxItemWidth : double.PositiveInfinity;

            double x = 0, y = 0, lineH = 0;
            foreach (var child in Children)
            {
                child.Measure(new Size(wrapWidth, availableSize.Height));
                var ds = child.DesiredSize;
                double w = ds.Width;
                if (w < minW) w = minW; if (w > maxW) w = maxW; if (w > wrapWidth) w = wrapWidth; // clamp
                double h = ds.Height < minH ? (minH > 0 ? minH : ds.Height) : ds.Height;

                if (x > 0 && x + w > wrapWidth)
                {
                    // new line
                    x = 0; y += lineH + VerticalSpacing; lineH = 0;
                }
                _childRects.Add(new Rect(x, y, w, h));
                x += w + HorizontalSpacing;
                if (h > lineH) lineH = h;
            }
            return new Size(wrapWidth, y + lineH);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int i = 0;
            foreach (var child in Children)
            {
                if (i < _childRects.Count) child.Arrange(_childRects[i]);
                i++;
            }
            return finalSize;
        }
    }
}
