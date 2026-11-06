using System.Collections.ObjectModel;

namespace LeafItAlone.Launcher.ViewModels;

/// <summary>
/// Mod-sources (2a R4): one row in the Settings -> "Mod sources" list. Wraps a
/// <see cref="Services.ModSource"/> for data-binding but keeps the underlying
/// registry object as the single source of truth (Save still writes through the
/// registry so an edit that fails validation doesn't half-persist).
/// </summary>
public sealed class SourceItem : ObservableObject
{
    public Services.ModSource Source { get; }
    public SourceItem(Services.ModSource source) => Source = source;

    public string Key => Source.Key;
    public string Url => Source.Url;
    public string RefName => string.IsNullOrWhiteSpace(Source.RefName) ? "(default branch)" : Source.RefName;
    public bool Trusted => Source.Trusted;
    public bool Enabled => Source.Enabled;
    public string LastFetched => string.IsNullOrEmpty(Source.LastFetchedUtc)
        ? "never"
        : Source.LastFetchedUtc;
    public string LastShortSha => string.IsNullOrEmpty(Source.LastSha)
        ? "-"
        : Source.LastSha[..System.Math.Min(7, Source.LastSha.Length)];

    public void Refresh()
    {
        OnPropertyChanged(nameof(Url));
        OnPropertyChanged(nameof(RefName));
        OnPropertyChanged(nameof(Trusted));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(LastFetched));
        OnPropertyChanged(nameof(LastShortSha));
    }
}

/// <summary>
/// One row in the Mods page's "Browse online" tab: a mod listed on a source repo,
/// with a badge reflecting whether it is installed / has an update available / is
/// not installed. Buttons drive <see cref="MainViewModel"/> install/update commands.
/// </summary>
public sealed class BrowseItem : ObservableObject
{
    public string SourceKey { get; init; } = "";
    public string SourceUrl { get; init; } = "";
    public Services.RemoteModEntry Entry { get; init; } = null!;
    public string SourceShortSha { get; init; } = "";

    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string Version => Entry.Version;
    public string Description => Entry.Description;
    public string Homepage => Entry.Homepage;
    public string GameVersion => Entry.GameVersion;

    /// <summary>True when the mod's id exists in the launcher's built-in set (install will be refused).</summary>
    public bool CollidesWithBuiltIn { get; init; }

    /// <summary>True when the same id is installed from this source at the same SHA.</summary>
    public bool InstalledAtSameSha { get; init; }

    /// <summary>True when the mod id is installed but at an older SHA (or from a different source).</summary>
    public bool UpdateAvailable { get; init; }

    /// <summary>True when the mod's manifest failed to parse; row shows an error but disables install.</summary>
    public bool HasManifestError => Entry.ManifestError is not null;

    public string StatusBadge
    {
        get
        {
            if (HasManifestError) return "manifest error";
            if (CollidesWithBuiltIn) return "built-in shadow";
            if (InstalledAtSameSha) return "installed";
            if (UpdateAvailable) return "update available";
            return "not installed";
        }
    }
}
