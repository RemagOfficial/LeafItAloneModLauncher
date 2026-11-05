using System.Windows.Controls;

namespace LeafItAlone.Launcher.Views;

/// <summary>
/// The Dashboard page (L9): the safe-base lifecycle, mod-engine runtime status and the
/// Apply &amp; Launch action, re-skinned as cards. Pure presentation - all state and
/// commands live on the inherited <c>MainViewModel</c> DataContext.
/// </summary>
public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();
}
