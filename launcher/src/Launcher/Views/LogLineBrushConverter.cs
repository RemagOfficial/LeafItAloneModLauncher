using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using LeafItAlone.Launcher.Services;

namespace LeafItAlone.Launcher.Views;

/// <summary>
/// Converts a raw Output-log line (the bound item, a <c>string</c>) into a foreground
/// <see cref="Brush"/>, using <see cref="LogColor"/> to recover the severity the
/// redirected stdout lost. Kept as a converter so the log can stay a flat list of
/// strings — nothing else about the log plumbing changes.
/// </summary>
public sealed class LogLineBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush InfoBrush = Freeze(0xD4, 0xD4, 0xD4);
    private static readonly SolidColorBrush SuccessBrush = Freeze(0x7F, 0xD8, 0x58);
    private static readonly SolidColorBrush WarningBrush = Freeze(0xFF, 0xC9, 0x4D);
    private static readonly SolidColorBrush ErrorBrush = Freeze(0xFF, 0x6B, 0x6B);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        LogColor.Classify(value as string) switch
        {
            LogSeverity.Success => SuccessBrush,
            LogSeverity.Warning => WarningBrush,
            LogSeverity.Error => ErrorBrush,
            _ => InfoBrush,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
