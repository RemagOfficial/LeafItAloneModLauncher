namespace LeafItAlone.Launcher.ViewModels;

/// <summary>
/// Which page the left nav rail shows (L9). Exactly one is active at a time; the
/// <see cref="MainViewModel"/> exposes <c>Show*</c> booleans derived from this so the
/// three <c>Views/*View</c> UserControls toggle their <c>Visibility</c> without a
/// template selector, keeping the single window VM as the shared DataContext.
/// </summary>
public enum Section
{
    Dashboard,
    Mods,
    Settings,
}
