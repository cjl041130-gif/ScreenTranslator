using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ScreenTranslator;

public sealed class DocumentPreviewVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not bool modeEnabled || values[1] is not bool hasTranslation)
            return Visibility.Collapsed;

        var visible = string.Equals(parameter as string, "original", StringComparison.Ordinal)
            ? modeEnabled || !hasTranslation
            : modeEnabled && hasTranslation;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
