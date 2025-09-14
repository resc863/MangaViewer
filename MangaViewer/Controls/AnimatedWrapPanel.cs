using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System.Collections.Generic;
using System.Numerics;
using Windows.Foundation;

namespace MangaViewer.Controls
{
    // Wrap panel that cooperates with implicit Offset animations on container visuals
    public sealed class AnimatedWrapPanel : Panel
    {
        public double HorizontalSpacing
        {
            get => (double)GetValue(HorizontalSpacingProperty);
            set => SetValue(HorizontalSpacingProperty, value);
        }
        public static readonly DependencyProperty HorizontalSpacingProperty =
            DependencyProperty.Register(nameof(HorizontalSpacing), typeof(double), typeof(AnimatedWrapPanel), new PropertyMetadata(8d, OnLayoutPropertyChanged));

        public double VerticalSpacing
        {
            get => (double)GetValue(VerticalSpacingProperty);
            set => SetValue(VerticalSpacingProperty, value);
        }
        public static readonly DependencyProperty VerticalSpacingProperty =
            DependencyProperty.Register(nameof(VerticalSpacing), typeof(double), typeof(AnimatedWrapPanel), new PropertyMetadata(8d, OnLayoutPropertyChanged));

        private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AnimatedWrapPanel p)
            {
                p.InvalidateMeasure();
                p.InvalidateArrange();
            }
        }

        // Track last arranged positions to compute deltas
        private readonly Dictionary<UIElement, Point> _lastPositions = new();

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0 ? 900 : availableSize.Width;
            double x = 0, y = 0, lineH = 0;

            foreach (var child in Children)
            {
                child.Measure(new Size(width, availableSize.Height));
                var sz = child.DesiredSize;

                if (x > 0 && x + sz.Width > width)
                {
                    x = 0;
                    y += lineH + VerticalSpacing;
                    lineH = 0;
                }

                x += sz.Width + HorizontalSpacing;
                if (sz.Height > lineH) lineH = sz.Height;
            }
            return new Size(width, y + lineH);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double width = finalSize.Width <= 0 ? 900 : finalSize.Width;
            double x = 0, y = 0, lineH = 0;

            foreach (var child in Children)
            {
                var desired = child.DesiredSize;
                if (x > 0 && x + desired.Width > width)
                {
                    x = 0;
                    y += lineH + VerticalSpacing;
                    lineH = 0;
                }

                var newPos = new Point(x, y);
                child.Arrange(new Rect(newPos, desired));

                var visual = ElementCompositionPreview.GetElementVisual(child);
                EnsureImplicitOffsetAnimations(visual);

                if (_lastPositions.TryGetValue(child, out var oldPos))
                {
                    var dx = (float)(oldPos.X - newPos.X);
                    var dy = (float)(oldPos.Y - newPos.Y);
                    if (dx != 0 || dy != 0)
                    {
                        // Jump to previous screen location (relative to new slot)
                        visual.Offset = new Vector3(dx, dy, 0f);
                        // Then let implicit animation drive back to zero
                        visual.Offset = Vector3.Zero;
                    }
                }
                else
                {
                    visual.Offset = Vector3.Zero;
                }

                _lastPositions[child] = newPos;

                x += desired.Width + HorizontalSpacing;
                if (desired.Height > lineH) lineH = desired.Height;
            }

            // Clean entries for removed children
            if (_lastPositions.Count != Children.Count)
            {
                var toRemove = new List<UIElement>();
                foreach (var kv in _lastPositions)
                {
                    if (!Children.Contains(kv.Key)) toRemove.Add(kv.Key);
                }
                foreach (var el in toRemove) _lastPositions.Remove(el);
            }

            return new Size(width, y + lineH);
        }

        private static void EnsureImplicitOffsetAnimations(Visual visual)
        {
            if (visual.ImplicitAnimations is not null && visual.ImplicitAnimations.ContainsKey("Offset"))
                return;

            var comp = visual.Compositor;
            var offsetAnim = comp.CreateVector3KeyFrameAnimation();
            offsetAnim.Duration = System.TimeSpan.FromMilliseconds(160);
            offsetAnim.Target = "Offset";
            var ease = comp.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.0f), new Vector2(0.0f, 1.0f));
            offsetAnim.InsertExpressionKeyFrame(1f, "this.FinalValue", ease);

            var coll = comp.CreateImplicitAnimationCollection();
            coll["Offset"] = offsetAnim;
            visual.ImplicitAnimations = coll;
        }
    }
}
