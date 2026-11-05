using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeafItAlone.Launcher.Models;

namespace LeafItAlone.Launcher.ViewModels;

/// <summary>
/// One editable config field, generated straight from a
/// <see cref="ConfigField"/> in the manifest. Exposes the handful of typed value
/// properties the per-type data templates bind to (<see cref="ValueText"/> and
/// <see cref="Num"/> for numbers, <see cref="Bool"/> for booleans,
/// <see cref="Str"/> for enum/text), plus <see cref="IsVisible"/> for
/// <c>when</c>-gating. The owning <see cref="ConfigEditorViewModel"/> re-evaluates
/// visibility and the dirty/pending state whenever a value changes.
/// </summary>
public sealed class ConfigFieldEditor : ObservableObject
{
    private readonly ConfigEditorViewModel _owner;

    private double _num;
    private bool _bool;
    private string _str = "";
    private bool _isVisible = true;

    public ConfigFieldEditor(ConfigField field, ConfigEditorViewModel owner)
    {
        Field = field;
        _owner = owner;
    }

    public ConfigField Field { get; }

    public string Key => Field.Key;
    public string Label => Field.Label;
    public string Type => Field.Type;
    public string? Help => Field.Help;
    public IReadOnlyList<string> Options => Field.Options;

    // Range defaults keep the slider usable when a manifest omits min/max.
    public double Min => Field.Min ?? 0;
    public double Max => Field.Max ?? 100;
    public double Step => Field.Step ?? (Field.Type == FieldTypes.Int ? 1 : 0.1);
    public bool IsInt => Field.Type == FieldTypes.Int;

    /// <summary>Number editors: <see cref="Num"/> drives the slider, <see cref="ValueText"/> the box.</summary>
    public double Num
    {
        get => _num;
        set
        {
            var clamped = Clamp(value);
            if (IsInt) clamped = Math.Round(clamped, MidpointRounding.AwayFromZero);
            if (Set(ref _num, clamped))
            {
                OnPropertyChanged(nameof(ValueText));
                _owner.OnFieldChanged(this);
            }
        }
    }

    public string ValueText
    {
        get => _num.ToString(IsInt ? "0" : "0.###", CultureInfo.CurrentCulture);
        set
        {
            if (double.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out var parsed)
                || double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed))
            {
                Num = parsed;   // routes through clamp/round + change notification
            }
            else
            {
                OnPropertyChanged(nameof(ValueText));   // revert the box to the last good value
            }
        }
    }

    public bool Bool
    {
        get => _bool;
        set { if (Set(ref _bool, value)) _owner.OnFieldChanged(this); }
    }

    /// <summary>Shared string value for both enum (ComboBox.SelectedItem) and text (TextBox).</summary>
    public string Str
    {
        get => _str;
        set { if (Set(ref _str, value ?? "")) _owner.OnFieldChanged(this); }
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set => Set(ref _isVisible, value);
    }

    private double Clamp(double v)
    {
        if (Field.Min.HasValue && v < Field.Min.Value) return Field.Min.Value;
        if (Field.Max.HasValue && v > Field.Max.Value) return Field.Max.Value;
        return v;
    }

    /// <summary>Populate the editor from a live value (or the manifest default when absent).</summary>
    public void SetValue(JsonNode? node) => Apply(node ?? Field.Default);

    private void Apply(JsonNode? node)
    {
        switch (Type)
        {
            case FieldTypes.Int:
            case FieldTypes.Float:
                // Clamp on load too: a pre-existing live value outside the manifest
                // range (e.g. an old >4-digit size) would otherwise render the slider
                // past its stops; it snaps into range and any save brings it legal.
                _num = Clamp(ReadNumber(node));
                if (IsInt) _num = Math.Round(_num, MidpointRounding.AwayFromZero);
                OnPropertyChanged(nameof(Num));
                OnPropertyChanged(nameof(ValueText));
                break;
            case FieldTypes.Bool:
                _bool = ReadBool(node);
                OnPropertyChanged(nameof(Bool));
                break;
            default: // enum | text
                _str = ReadString(node);
                if (Type == FieldTypes.Enum && Options.Count > 0 && !Options.Contains(_str))
                    _str = Options[0];
                OnPropertyChanged(nameof(Str));
                break;
        }
    }

    /// <summary>Serialize the editor's current value back to a JSON node for the given type.</summary>
    public JsonNode ToNode() => Type switch
    {
        FieldTypes.Int => JsonValue.Create((int)Math.Round(_num, MidpointRounding.AwayFromZero))!,
        FieldTypes.Float => JsonValue.Create(_num)!,
        FieldTypes.Bool => JsonValue.Create(_bool)!,
        _ => JsonValue.Create(_str)!,
    };

    /// <summary>Re-evaluate <c>when</c> against the owner's current values.</summary>
    public void RefreshVisibility(Func<string, string?> currentOf)
    {
        if (Field.When is null) { IsVisible = true; return; }
        var actual = currentOf(Field.When.Key);
        var expected = Field.When.EqualsValue?.ToString().Trim('"');
        IsVisible = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static double ReadNumber(JsonNode? node)
    {
        if (node is null) return 0;
        try
        {
            var el = node.GetValue<JsonElement>();
            if (el.TryGetDouble(out var d)) return d;
        }
        catch (InvalidOperationException) { /* stored as string */ }
        return double.TryParse(node.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var p) ? p : 0;
    }

    private static bool ReadBool(JsonNode? node)
    {
        if (node is null) return false;
        try { return node.GetValue<JsonElement>().GetBoolean(); }
        catch { return bool.TryParse(node.ToString(), out var b) && b; }
    }

    private static string ReadString(JsonNode? node)
    {
        if (node is null) return "";
        try { return node.GetValue<JsonElement>().ValueKind == JsonValueKind.String ? node.GetValue<string>() : node.ToString(); }
        catch { return node.ToString(); }
    }
}
