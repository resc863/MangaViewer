using MangaViewer.Helpers;
using Windows.Foundation;
using System.Diagnostics;

namespace MangaViewer.ViewModels
{
    /// <summary>
    /// OCR ��� ���� BoundingBox ǥ��.
    /// ���� �̹��� �ȼ� ��ǥ(Rect)�� �̹��� ��ü ũ�� ������� ����ȭ/�����ϸ� ����.
    /// </summary>
    public class BoundingBoxViewModel : BaseViewModel
    {
        private string _text = string.Empty;
        public string Text
        {
            get => _text;
            set => SetProperty(ref _text, value);
        }

        /// <summary>
        /// ���� �̹��������� BoundingBox (�ȼ� ����)
        /// </summary>
        public Rect OriginalBoundingBox { get; }

        public int ImagePixelWidth { get; }
        public int ImagePixelHeight { get; }

        /// <summary>
        /// 0~1 ����ȭ�� �簢��
        /// </summary>
        public Rect NormalizedRect { get; }

        private Rect _displayRect;
        /// <summary>
        /// ���� ǥ�� �����̳�(Canvas)������ ��ġ/ũ�� (Uniform ������ ���)
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
        /// �����̳�(�̹��� ǥ�� ����) ũ�� ���� �� ȣ���Ͽ� DisplayRect ����.
        /// </summary>
        public void UpdateLayout(double containerWidth, double containerHeight)
        {
            if (ImagePixelWidth <= 0 || ImagePixelHeight <= 0 || containerWidth <= 0 || containerHeight <= 0)
            {
                DisplayRect = new Rect();
                return;
            }
            // Uniform ������ ����
            double scale = System.Math.Min(containerWidth / ImagePixelWidth, containerHeight / ImagePixelHeight);
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
        /// Overlay ĵ������ �̹� ǥ�õ� �̹��� ũ��� ��ġ�� �� ��� (������ ����)
        /// </summary>
        public void UpdateLayoutExact(double displayImageWidth, double displayImageHeight)
        {
            if (displayImageWidth <= 0 || displayImageHeight <= 0)
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
        /// Wrapper�� ���͹ڽ����� ���� �������� ����Ͽ� Layout ������Ʈ
        /// </summary>
        public void UpdateLayoutExactWithOffset(double displayImageWidth, double displayImageHeight, double offsetX, double offsetY)
        {
            if (displayImageWidth <= 0 || displayImageHeight <= 0)
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
