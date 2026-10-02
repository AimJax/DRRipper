using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DRRipper.UI;

namespace DRRipper.Views
{
    /// <summary>View-only converters (Ticket #006). No business logic.</summary>
    public sealed class EnumMatchConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value == null || parameter == null) return false;
                return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value is bool b && b && parameter != null)
                    return Enum.Parse(targetType, parameter.ToString()!);
            }
            catch { }
            return DependencyProperty.UnsetValue;
        }
    }

    /// <summary>Byte-count to readable size (unknown/negative renders "?").</summary>
    public sealed class BytesConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value is long l) return l < 0 ? "?" : UI.Formatting.FormatBytes(l);
                if (value is int i) return i < 0 ? "?" : UI.Formatting.FormatBytes(i);
            }
            catch { }
            return "?";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => DependencyProperty.UnsetValue;
    }
}
