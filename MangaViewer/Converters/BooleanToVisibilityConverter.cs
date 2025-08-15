using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace MangaViewer.Converters
{
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool boolValue = value is bool b && b;
            bool isReversed = parameter is string s && s.Equals("Reversed", StringComparison.OrdinalIgnoreCase);

            if (isReversed)
            {
                boolValue = !boolValue;
            }

            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
