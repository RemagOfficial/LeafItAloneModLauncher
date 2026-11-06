using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using LeafItAlone.Launcher.Models;
using LeafItAlone.Launcher.Services;

namespace LeafItAlone.Launcher.ViewModels;

/// <summary>
/// The schema-driven editor for one mod, generated entirely from its manifest's
/// <c>config.fields</c>. Loads the live config (preserving every key the schema
/// does not mention), maps each described field to a <see cref="ConfigFieldEditor"/>,
/// re-evaluates <c>when</c> visibility as values change, and writes back only the
/// described keys. "Pending" = the live file's sha differs from the sha recorded at
/// the last apply (set by L3); L2 surfaces it via the badge.
/// </summary>
public sealed class ConfigEditorViewModel : ObservableObject
{
    private JsonObject _root = new();
    private string _currentSha = "";
    private string _lastAppliedSha = "";

    public ConfigEditorViewModel(ModInfo mod, Action<string>? log = null)
    {
        Mod = mod;
        _log = log;
        LivePath = EnvPaths.Expand(mod.Manifest.ConfigPath);
        SaveCommand = new RelayCommand(_ => Save());
        ReloadCommand = new RelayCommand(_ => { Load(); Info = "reloaded from disk"; });
        ResetCommand = new RelayCommand(_ => ResetToDefaults());
        Fields = new ObservableCollection<ConfigFieldEditor>();
        Load();
    }

    private readonly Action<string>? _log;

    public ModInfo Mod { get; }
    public string LivePath { get; }
    public ObservableCollection<ConfigFieldEditor> Fields { get; }

    public RelayCommand SaveCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand ResetCommand { get; }

    private string _info = "";
    public string Info { get => _info; set => Set(ref _info, value); }

    /// <summary>The mod's enable toggle (L3/L4 drive apply/revert from this).</summary>
    public bool Enabled
    {
        get => _enabled;
        set { if (Set(ref _enabled, value)) RaisePending(); }
    }
    private bool _enabled;

    /// <summary>
    /// Pending = the state on disk differs from what is currently applied to the
    /// game, so an Apply/Revert would change something. Two cases (plan: "enabled
    /// state changed OR live-config sha != lastAppliedConfigSha"):
    ///  * enabled  - pending when the config's sha differs from the applied sha
    ///               (edited since the last apply, or never applied: applied is empty).
    ///  * disabled - pending when an applied sha exists (it's live in the game but
    ///               the user has switched it off, so a revert is owed).
    /// </summary>
    public bool IsPending => Enabled ? _currentSha != _lastAppliedSha : _lastAppliedSha.Length > 0;

    /// <summary>True when the mod is currently applied in the game (a revert is possible).</summary>
    public bool IsApplied => _lastAppliedSha.Length > 0;

    /// <summary>Seed from launcher state, then re-evaluate pending.</summary>
    public void ApplyState(ModState state)
    {
        _enabled = state.Enabled;
        OnPropertyChanged(nameof(Enabled));
        _lastAppliedSha = state.LastAppliedConfigSha;
        RaisePending();
    }

    public void Load()
    {
        _root = ConfigStore.Load(LivePath);
        _currentSha = ConfigStore.ComputeSha(LivePath);

        Fields.Clear();
        foreach (var field in Mod.Manifest.Config.Fields)
        {
            var editor = new ConfigFieldEditor(field, this);
            editor.SetValue(ConfigStore.GetByPath(_root, field.Key));
            Fields.Add(editor);
        }
        RefreshVisibility();
    }

    public void Save()
    {
        try
        {
            // Write ONLY the schema-described keys into the loaded document so all
            // other members (_comment, _applyHelper, future engine keys) survive.
            foreach (var editor in Fields)
                ConfigStore.SetByPath(_root, editor.Key, editor.ToNode());

            ConfigStore.Save(LivePath, _root);
            _currentSha = ConfigStore.ComputeSha(LivePath);
            Info = $"saved -> {LivePath}";
            _log?.Invoke($"{Mod.Id}: config saved to {LivePath}");
            RaisePending();
        }
        catch (Exception ex)
        {
            Info = "save failed: " + ex.Message;
            _log?.Invoke($"{Mod.Id}: SAVE FAILED - {ex.Message}");
        }
    }

    /// <summary>Called by a field when its value changes: re-gate visibility + pending.</summary>
    public void OnFieldChanged(ConfigFieldEditor changed)
    {
        RefreshVisibility();
        RaisePending();
    }

    /// <summary>
    /// Restore every schema-described field to its manifest <c>default</c> and save.
    /// Unknown top-level keys (<c>_comment</c>, <c>_applyHelper</c>, <c>seed</c>, ...)
    /// are preserved because <see cref="Save"/> only rewrites the described paths. This
    /// is the fix for "I reinstalled / updated a mod but the live config still holds
    /// values from the old version" - the on-disk config file is shared across installs
    /// and downloads never touch it, so it has to be reset explicitly.
    /// Fields with no declared default are left as-is rather than zeroed.
    /// </summary>
    public void ResetToDefaults()
    {
        try
        {
            int n = 0;
            foreach (var editor in Fields)
            {
                if (editor.Field.Default is null) continue;
                editor.SetValue(editor.Field.Default);
                n++;
            }
            RefreshVisibility();
            Save();
            Info = $"reset {n} field(s) to manifest defaults";
            _log?.Invoke($"{Mod.Id}: config reset to defaults ({n} field(s))");
        }
        catch (Exception ex)
        {
            Info = "reset failed: " + ex.Message;
            _log?.Invoke($"{Mod.Id}: RESET FAILED - {ex.Message}");
        }
    }

    private void RefreshVisibility()
    {
        foreach (var editor in Fields)
            editor.RefreshVisibility(StringValueOf);
    }

    private string? StringValueOf(string key)
    {
        var editor = Fields.FirstOrDefault(f => f.Key == key);
        if (editor is null) return null;
        return editor.Type switch
        {
            FieldTypes.Bool => editor.Bool ? "true" : "false",
            FieldTypes.Int or FieldTypes.Float => editor.Num.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => editor.Str,
        };
    }

    private void RaisePending()
    {
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsApplied));
    }

    /// <summary>Snapshot used to reconcile state.json after a successful apply (L3/L4).</summary>
    public string CurrentConfigSha => _currentSha;

    /// <summary>After L3 applies cleanly the caller records the applied sha; clear pending.</summary>
    public void MarkApplied(string appliedSha)
    {
        _lastAppliedSha = appliedSha;
        RaisePending();
    }

    /// <summary>After L3 reverts cleanly there is nothing applied; the pending badge
    /// then reflects the disabled state (an empty applied sha is not "owed").</summary>
    public void MarkReverted() => MarkApplied("");
}
