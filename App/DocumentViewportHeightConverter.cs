using System.Globalization;
using System.Windows.Data;

namespace ScreenTranslator;

public sealed class DocumentViewportHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double height || double.IsNaN(height) || double.IsInfinity(height))
            return 360d;
        return Math.Max(300d, height - 80d);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
