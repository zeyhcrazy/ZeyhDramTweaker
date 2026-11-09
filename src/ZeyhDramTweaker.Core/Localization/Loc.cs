using System.ComponentModel;
using System.Globalization;
using ZeyhDramTweaker.Core.Model;

namespace ZeyhDramTweaker.Core.Localization;

/// <summary>pt-BR / en strings. WPF binds to the indexer (<c>{Binding [key], Source={x:Static loc:Loc.Instance}}</c>);
/// switching <see cref="Language"/> raises "Item[]" so every bound label refreshes live.</summary>
public sealed partial class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;

    string _lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt" ? "pt" : "en";
    public string Language
    {
        get => _lang;
        set
        {
            if (value == _lang || !Tables.ContainsKey(value)) return;
            _lang = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        }
    }

    public string this[string key] =>
        Tables[_lang].TryGetValue(key, out var s) ? s : Tables["en"].TryGetValue(key, out var e) ? e : key;

    public string Format(string key, params object[] args)
    {
        var resolved = args.Select(a => a is string s && Tables["en"].ContainsKey(s) ? this[s] : a).ToArray();
        try { return string.Format(CultureInfo.InvariantCulture, this[key], resolved); }
        catch (FormatException) { return this[key]; }
    }

    public string Notice(Notice n) => Format(n.Key, n.Args);

    /// <summary>Translates BIOS option words the engines emit (Enabled, Disabled, Auto...).</summary>
    public string Value(string? v) => v == null ? "—" : Tables[_lang].TryGetValue("val." + v, out var s) ? s : Display(v);

    /// <summary>One spelling for termination values: the 1.7.3 tables and the BIOS read-back say "(48)RZQ/5" / "OFF",
    /// the DDR5 engine "RZQ/5 (48)" / "Off". Shown everywhere as "RZQ/5 (48)" and "Off".</summary>
    public static string Display(string v)
    {
        if (v == "OFF") return "Off";
        // voltages: "1.150" -> "1.15", keep real third decimals ("1.125")
        if (v.Length == 5 && v[1] == '.' && v[4] == '0' && char.IsDigit(v[0]) && char.IsDigit(v[2]) && char.IsDigit(v[3])) return v[..4];
        var m = System.Text.RegularExpressions.Regex.Match(v, @"^\((\d+(?:\.\d+)?)\)\s*RZQ/(\d)$");
        return m.Success ? $"RZQ/{m.Groups[2].Value} ({m.Groups[1].Value})" : v;
    }

    public string Profile(Profile p) => this["prof." + p];

    static readonly Dictionary<string, Dictionary<string, string>> Tables = new()
    {
        ["pt"] = Pt(),
        ["en"] = En(),
    };
}
