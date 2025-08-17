using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace MangaViewer.Converters
{
    /// <summary>
    /// bool -> Brush 변환 (true=Green, false=Red, null/기타=Gray)
    /// </summary>
    public class BooleanToBrushConverter : IValueConverter
    {
        // 동일 Brush 재사용 (SolidColorBrush 불변 가정)
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
