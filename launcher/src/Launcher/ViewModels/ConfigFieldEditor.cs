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

    // Authoritative value for numeric fields. It MAY sit outside the manifest's
    // [Min,Max] - the textbox is allowed to set that on purpose (see ValueText) -
    // while the slider only ever sees a clamped view of it (see Num).
    private double _value;
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

    /// <summary>
    /// Slider-bound VIEW of the value: always clamped into the manifest's safe
    /// [Min,Max] (and rounded for ints) so the thumb can never pass its stops. The
    /// authoritative value is <see cref="_value"/>, which <see cref="ValueText"/> may
    /// set outside that range on purpose - the slider then just pins to the nearest bound.
    /// Dragging the slider writes a clamped value back into <see cref="_value"/>.
    /// </summary>
    public double Num
    {
        get => CoerceForSlider(_value);
        set => Commit(value, fromSlider: true);
    }

    /// <summary>
    /// Text-box view: shows the real (possibly out-of-range) value and lets the user
    /// commit any number EXCEPT zero - a safe slider band plus free entry beyond it.
    /// Non-numeric, non-finite, or values that round to 0 are rejected by snapping the
    /// box back to the last good value (the slider is never affected past its stops).
    /// </summary>
    public string ValueText
    {
        get => _value.ToString(IsInt ? "0" : "0.###", CultureInfo.CurrentCulture);
        set
        {
            if (!TryParseNumber(value, out var parsed) || !double.IsFinite(parsed))
            {
                OnPropertyChanged(nameof(ValueText));          // not a number: revert
                return;
            }
            if (IsInt) parsed = Math.Round(parsed, MidpointRounding.AwayFromZero);
            if (parsed == 0)
            {
                OnPropertyChanged(nameof(ValueText));          // zero is not allowed: revert
                return;
            }
            Commit(parsed, fromSlider: false);                 // out-of-range is intentional
        }
    }

    /// <summary>Shared write path. Slider input is clamped to the safe band; box input is
    /// stored as given (out-of-range allowed) after int rounding. Refreshes BOTH bindings so
    /// dragging the box pins the slider to the edge and dragging the slider rewrites the box.</summary>
    private void Commit(double v, bool fromSlider)
    {
        if (fromSlider) v = Clamp(v);
        if (IsInt) v = Math.Round(v, MidpointRounding.AwayFromZero);
        if (!Set(ref _value, v)) return;
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(Num));
        _owner.OnFieldChanged(this);
    }

    private double CoerceForSlider(double v)
    {
        var s = Clamp(v);
        if (IsInt) s = Math.Round(s, MidpointRounding.AwayFromZero);
        return s;
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

    private static bool TryParseNumber(string s, out double v)
        => double.TryParse(s, NumberStyles.Number, CultureInfo.CurrentCulture, out v)
        || double.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out v);

    /// <summary>Populate the editor from a live value (or the manifest default when absent).</summary>
    public void SetValue(JsonNode? node) => Apply(node ?? Field.Default);

    private void Apply(JsonNode? node)
    {
        switch (Type)
        {
            case FieldTypes.Int:
            case FieldTypes.Float:
                // Keep the stored value AS-IS (out-of-range allowed). The slider clamps
                // for display via Num's getter, so a value past Max just pins the thumb to
                // the end while the box (and the saved config) still hold the real number.
                _value = ReadNumber(node);
                if (IsInt) _value = Math.Round(_value, MidpointRounding.AwayFromZero);
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
        FieldTypes.Int => JsonValue.Create((int)Math.Round(_value, MidpointRounding.AwayFromZero))!,
        FieldTypes.Float => JsonValue.Create(_value)!,
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
