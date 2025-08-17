using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace MangaViewer.Converters
{
    /// <summary>
    /// bool -> Brush ��ȯ (true=Green, false=Red, null/��Ÿ=Gray)
    /// </summary>
    public class BooleanToBrushConverter : IValueConverter
    {
        // ���� Brush ���� (SolidColorBrush �Һ� ����)
        private static readonly SolidColorBrush GreenBrush = new(Microsoft.UI.Colors.Green);
        private static readonly SolidColorBrush RedBrush = new(Microsoft.UI.Colors.Red);
        private static readonly SolidColorBrush GrayBrush = new(Microsoft.UI.Colors.Gray);

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b) return b ? GreenBrush : RedBrush;
            return GrayBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotImplementedException();
    }
}
