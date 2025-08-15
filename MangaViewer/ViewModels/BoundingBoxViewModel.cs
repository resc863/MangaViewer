using MangaViewer.Helpers;
using System.Windows.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace MangaViewer.ViewModels
{
    public class BoundingBoxViewModel : BaseViewModel
    {
        private string _text;
        public string Text
        {
            get => _text;
            set => SetProperty(ref _text, value);
        }

        private Rect _originalBoundingBox;

        private double _x;
        public double X
        {
            get => _x;
            set => SetProperty(ref _x, value);
        }

        private double _y;
        public double Y
        {
            get => _y;
            set => SetProperty(ref _y, value);
        }

        private double _width;
        public double Width
        {
            get => _width;
            set => SetProperty(ref _width, value);
        }

        private double _height;
        public double Height
        {
            get => _height;
            set => SetProperty(ref _height, value);
        }

        public ICommand CopyCommand { get; }

        public BoundingBoxViewModel(string text, Rect boundingBox)
        { 
            _text = text;
            _originalBoundingBox = boundingBox;
            CopyCommand = new RelayCommand(CopyTextToClipboard);
        }

        private void CopyTextToClipboard(object obj)
        { 
            var dataPackage = new DataPackage();
            dataPackage.SetText(Text);
            Clipboard.SetContent(dataPackage);
        }

        public void UpdatePosition(double scaleX, double scaleY)
        {
            X = _originalBoundingBox.X * scaleX;
            Y = _originalBoundingBox.Y * scaleY;
            Width = _originalBoundingBox.Width * scaleX;
            Height = _originalBoundingBox.Height * scaleY;
        }
    }
}
