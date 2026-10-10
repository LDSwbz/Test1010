using System;
using System.Globalization;
using System.Windows.Data;

namespace ScanningPowerCylinder
{
    /// <summary>
    /// 将法桶 Range (1..4) 与 ComboBox SelectedIndex (0..3) 互转。
    /// </summary>
    public sealed class RangeToOneIndexConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int i) return i - 1;
            if (value is double d) return (int)d - 1;
            return 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int idx) return idx + 1;
            return 1;
        }
    }
}
