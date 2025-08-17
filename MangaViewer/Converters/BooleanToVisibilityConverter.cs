using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace MangaViewer.Converters
{
    /// <summary>
    /// bool -> Visibility 변환. parameter="Reversed" 시 반전.
    /// </summary>
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool v = value is bool b && b;
            bool reversed = parameter is string s && s.Equals("Reversed", StringComparison.OrdinalIgnoreCase);
            if (reversed) v = !v;
            return v ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotImplementedException();
    }
}
