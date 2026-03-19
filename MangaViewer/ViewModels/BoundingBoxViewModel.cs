using MangaViewer.Helpers;
using Windows.Foundation;
using System.Diagnostics;

namespace MangaViewer.ViewModels
{
    /// <summary>
    /// OCR 결과 등의 BoundingBox 표현.
    /// 원본 이미지 픽셀 좌표(Rect)와 이미지 전체 크기 기반으로 정규화/스케일링 지원.
    /// </summary>
    public class BoundingBoxViewModel : BaseViewModel
    {
        private string _text = string.Empty;
        private string _translatedText = string.Empty;
        public string Text
        {
            get => _text;
            set => SetProperty(ref _text, value);
        }

        public string TranslatedText
        {
            get => _translatedText;
            set => SetProperty(ref _translatedText, value);
        }

        /// <summary>
        /// 원본 이미지에서의 BoundingBox (픽셀 단위)
        /// </summary>
        public Rect OriginalBoundingBox { get; }

        public int ImagePixelWidth { get; }
        public int ImagePixelHeight { get; }

        /// <summary>
        /// 0~1 정규화된 사각형
        /// </summary>
        public Rect NormalizedRect { get; }

        private Rect _displayRect;
        /// <summary>
        /// 현재 표시 컨테이너(Canvas)에서의 위치/크기 (Uniform 스케일 고려)
        /// </summary>
        public Rect DisplayRect
        {
            get => _displayRect;
            private set
            {
                if (SetProperty(ref _displayRect, value))
                {
                    OnPropertyChanged(nameof(X));
                    OnPropertyChanged(nameof(Y));
                    OnPropertyChanged(nameof(W));
                    OnPropertyChanged(nameof(H));
                }
            }
        }

        public double X => _displayRect.X;
        public double Y => _displayRect.Y;
        public double W => _displayRect.Width;
        public double H => _displayRect.Height;

        // Original pixel coordinate convenience (used when applying a global RenderTransform)
        public double OriginalX => OriginalBoundingBox.X;
        public double OriginalY => OriginalBoundingBox.Y;
        public double OriginalW => OriginalBoundingBox.Width;
        public double OriginalH => OriginalBoundingBox.Height;

        public BoundingBoxViewModel(string text, Rect boundingBox, int imagePixelWidth, int imagePixelHeight)
        {
            _text = text;
            OriginalBoundingBox = boundingBox;
            ImagePixelWidth = imagePixelWidth;
            ImagePixelHeight = imagePixelHeight;
            if (imagePixelWidth > 0 && imagePixelHeight > 0)
            {
                NormalizedRect = new Rect(
                    boundingBox.X / imagePixelWidth,
                    boundingBox.Y / imagePixelHeight,
                    boundingBox.Width / imagePixelWidth,
                    boundingBox.Height / imagePixelHeight);
            }
            else
            {
                NormalizedRect = new Rect();
            }
        }

        /// <summary>
        /// 컨테이너(이미지 표시 영역) 크기 변경 시 호출하여 DisplayRect 갱신.
        /// </summary>
        public void UpdateLayout(double containerWidth, double containerHeight)
        {
            if (ImagePixelWidth <= 0 || ImagePixelHeight <= 0 || 
                containerWidth <= 0 || containerHeight <= 0 ||
                double.IsNaN(containerWidth) || double.IsNaN(containerHeight) ||
                double.IsInfinity(containerWidth) || double.IsInfinity(containerHeight))
            {
                DisplayRect = new Rect();
                return;
            }
            
            // Uniform 스케일 적용
            double scale = System.Math.Min(containerWidth / ImagePixelWidth, containerHeight / ImagePixelHeight);
            
            // Prevent invalid scale values
            if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
            {
                DisplayRect = new Rect();
                return;
            }
            
            double displayImageWidth = ImagePixelWidth * scale;
            double displayImageHeight = ImagePixelHeight * scale;
            double offsetX = (containerWidth - displayImageWidth) / 2.0;
            double offsetY = (containerHeight - displayImageHeight) / 2.0;

            double x = offsetX + NormalizedRect.X * displayImageWidth;
            double y = offsetY + NormalizedRect.Y * displayImageHeight;
            double w = NormalizedRect.Width * displayImageWidth;
            double h = NormalizedRect.Height * displayImageHeight;
            
            DisplayRect = new Rect(x, y, w, h);
            Debug.WriteLine($"[OCRBox]Legacy '{Text}' CW={containerWidth:F1} CH={containerHeight:F1} -> Rect=({x:F1},{y:F1},{w:F1},{h:F1})");
        }

        /// <summary>
        /// Overlay 캔버스가 이미 표시된 이미지 크기와 일치할 때 사용 (오프셋 없음)
        /// </summary>
        public void UpdateLayoutExact(double displayImageWidth, double displayImageHeight)
        {
            if (displayImageWidth <= 0 || displayImageHeight <= 0 ||
                double.IsNaN(displayImageWidth) || double.IsNaN(displayImageHeight) ||
                double.IsInfinity(displayImageWidth) || double.IsInfinity(displayImageHeight))
            {
                DisplayRect = new Rect();
                return;
            }
            
            double x = NormalizedRect.X * displayImageWidth;
            double y = NormalizedRect.Y * displayImageHeight;
            double w = NormalizedRect.Width * displayImageWidth;
            double h = NormalizedRect.Height * displayImageHeight;
            DisplayRect = new Rect(x, y, w, h);
        }

        /// <summary>
        /// Wrapper의 레터박싱으로 인한 오프셋을 고려하여 Layout 업데이트
        /// </summary>
        public void UpdateLayoutExactWithOffset(double displayImageWidth, double displayImageHeight, double offsetX, double offsetY)
        {
            if (displayImageWidth <= 0 || displayImageHeight <= 0 ||
                double.IsNaN(displayImageWidth) || double.IsNaN(displayImageHeight) ||
                double.IsInfinity(displayImageWidth) || double.IsInfinity(displayImageHeight) ||
                double.IsNaN(offsetX) || double.IsNaN(offsetY) ||
                double.IsInfinity(offsetX) || double.IsInfinity(offsetY))
            {
                DisplayRect = new Rect();
                return;
            }
            
            double x = offsetX + NormalizedRect.X * displayImageWidth;
            double y = offsetY + NormalizedRect.Y * displayImageHeight;
            double w = NormalizedRect.Width * displayImageWidth;
            double h = NormalizedRect.Height * displayImageHeight;
            DisplayRect = new Rect(x, y, w, h);
        }
    }
}
