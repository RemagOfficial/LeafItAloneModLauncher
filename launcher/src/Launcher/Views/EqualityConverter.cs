using System;
using System.Globalization;
using System.Windows.Data;

namespace LeafItAlone.Launcher.Views;

/// <summary>
/// One-way "does the bound value equal the parameter?" test used by the L9 Settings
/// segmented controls: each theme/accent option binds its <c>IsChecked</c> through this
/// and fires a Command on click, so the selection reflects the VM string without the VM
/// needing a boolean per option. <see cref="ConvertBack"/> is intentionally unsupported -
/// the change path is the button command, not the checked state.
/// </summary>
public sealed class EqualityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var a = value?.ToString();
        var b = parameter?.ToString();
        if (a is null || b is null) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
