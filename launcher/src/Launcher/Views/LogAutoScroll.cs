using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace LeafItAlone.Launcher.Views;

/// <summary>
/// Attached behaviour that keeps a <see cref="ListBox"/> scrolled to its newest line,
/// used on the shared output log so streaming subprocess output follows itself without
/// a manual scroll. It only follows when the viewer is <em>already</em> near the bottom:
/// if the user scrolled up to read history, new lines do not yank the view down.
///
/// <para>
/// Kept as a declarative attached property rather than code-behind so the window stays
/// DataContext-only. The collection instance (<c>LogLines</c>) lives for the VM's whole
/// lifetime, so we subscribe once (on first load) and never need to detach.
/// </para>
/// </summary>
public static class LogAutoScroll
{
    /// <summary>How close to the bottom (px) still counts as "following" the log.</summary>
    private const double NearBottomPx = 48;

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(LogAutoScroll),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool value) => o.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox lb || !(bool)e.NewValue) return;

        if (lb.IsLoaded) { Attach(lb); return; }

        RoutedEventHandler? onLoaded = null;
        onLoaded = (_, _) => { lb.Loaded -= onLoaded; Attach(lb); };
        lb.Loaded += onLoaded;
    }

    private static void Attach(ListBox lb)
    {
        if (lb.ItemsSource is not INotifyCollectionChanged inc) return;

        inc.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Add) return;

            // Defer past layout so ScrollableHeight/VerticalOffset reflect the new line.
            lb.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                var sv = FindScrollViewer(lb);
                if (sv is null) return;
                double distanceFromBottom = sv.ScrollableHeight - sv.VerticalOffset;
                if (distanceFromBottom <= NearBottomPx)
                    sv.ScrollToEnd();
            }));
        };
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }
}
