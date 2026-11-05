using System.Windows.Controls;

namespace LeafItAlone.Launcher.Views;

/// <summary>
/// The Mods page (L9): the mod list and the schema-driven config editor, moved out of
/// the old TabControl and re-skinned. The editor markup is unchanged in behaviour - the
/// control per field is still chosen by the manifest's declared <c>type</c> and gated by
/// <c>when</c>; only the styling (and the decoupled slider/box, from L2) is refreshed.
/// Apply/Revert still resolve to the window's <c>MainViewModel</c> via RelativeSource.
/// </summary>
public partial class ModsView : UserControl
{
    public ModsView() => InitializeComponent();
}
