using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ZeyhDramTweaker.Core.Engines;
using ZeyhDramTweaker.Core.Localization;
using ZeyhDramTweaker.Core.Model;

namespace ZeyhDramTweaker.ViewModels;

public sealed record Metric(string Label, string Value, string? Sub = null);

/// <summary>A stat with a small vertical level bar (PC Manager style); Level is 0..1 of the bar.</summary>
public sealed record Gauge(string Label, string Value, string? Sub, double Level)
{
    public double BarHeight => Math.Clamp(Level, 0.06, 1) * 46;
}

public sealed class TabViewModel(string header, List<SectionViewModel> sections, List<NoticeViewModel>? notes = null)
{
    public string Header { get; } = header;
    public List<SectionViewModel> Sections { get; } = sections;
    public List<NoticeViewModel> Notes { get; } = notes ?? new();
}

public sealed class NoticeViewModel(Notice n)
{
    static Brush B(string k) => (Brush)Application.Current.Resources[k];
    public NoticeLevel Level { get; } = n.Level;
    public string Text { get; } = Loc.Instance.Notice(n);
    public string Glyph { get; } = n.Level switch { NoticeLevel.Error => "", NoticeLevel.Warning => "", _ => "" };
    public Brush Brush { get; } = B(n.Level switch { NoticeLevel.Error => "ErrorBrush", NoticeLevel.Warning => "WarnBrush", _ => "InfoBrush" });
    public Brush Tint { get; } = B(n.Level switch { NoticeLevel.Error => "ErrorTintBrush", NoticeLevel.Warning => "WarnTintBrush", _ => "InfoTintBrush" });
}

public sealed class ResultViewModel
{
    static Loc L => Loc.Instance;

    public CalcResult Model { get; }
    public bool Ok => Model.Ok;
    public List<Metric> Metrics { get; }
    public List<Gauge> Gauges { get; } = new();
    public string Speed { get; }
    public string PresetLabel { get; }
    public string Kit { get; }
    public string Details { get; }
    public List<TabViewModel> Tabs { get; }
    public List<NoticeViewModel> Alerts { get; }      // errors + warnings, shown above the tabs
    public List<NoticeViewModel> Notes { get; }       // everything, in the Notes tab
    public string NotesHeader { get; }

    // one compact bar above the tabs instead of repeating every warning that the Notes tab already lists
    public bool HasAlerts => Ok && Alerts.Count > 0;
    public bool ManyAlerts => Alerts.Count > 1;
    public NoticeViewModel? TopAlert => Alerts.FirstOrDefault();
    public string AlertCount => L.Format("alert.count", Alerts.Count);

    // no result (Manual without ns, combination 1.7.3 does not cover): an empty state replaces the hero and the tabs
    public bool IsEmpty => !Ok;
    public bool NeedsValues => !Ok && Model.Notices.Any(n => n.Key == "err.entervalues");
    public string EmptyTitle => L[NeedsValues ? "empty.manual.title" : "empty.error.title"];
    public string EmptyBody => NeedsValues ? L["empty.manual.body"] : string.Join("\n\n", Alerts.Where(a => a.Level == NoticeLevel.Error).Select(a => a.Text));
    public string? EmptyHint => NeedsValues ? null : L["empty.error.hint"];
    /// <summary>Warnings that still matter in the empty state (the error itself is already the body).</summary>
    public List<NoticeViewModel> EmptyExtras => NeedsValues ? new() : Alerts.Where(a => a.Level != NoticeLevel.Error).ToList();

    public ResultViewModel(CalcResult r)
    {
        Model = r;
        var i = r.Input;
        var plat = Catalog.Get(i.Platform);
        bool ddr5 = plat.Mem == MemGen.DDR5;
        double tck = 2000.0 / i.MTs;
        string? P(string row) => r.Value(Ddr4Layout.Primary, row);
        string? V(string row, int col) => r.Value(Ddr4Layout.Voltages, row, col);

        Metrics = new() { new(L["m.speed"], $"{(ddr5 ? "DDR5" : "DDR4")}-{i.MTs}", L["ui.preset." + i.Preset]) };
        Speed = $"{(ddr5 ? "DDR5" : "DDR4")}-{i.MTs}";
        PresetLabel = L["ui.preset." + i.Preset];
        Kit = r.Ok ? $"{P("tCL")}-{P("tRCDRD")}-{P("tRP")}-{P("tRAS")}" : "—";
        Details = "";
        if (r.Ok)
        {
            var parts = new List<string> { $"tRC {P("tRC")}" };
            if (int.TryParse(P("tRFC"), out var rf)) parts.Add(string.Create(CultureInfo.InvariantCulture, $"tRFC {rf} ({rf * tck:0} ns)"));
            parts.Add($"GDM {L.Value(r.Value(Ddr4Layout.Misc, "Gear Down Mode"))}");
            if (r.Value(Ddr4Layout.Misc, "Command Rate") is { } cr) parts.Add(cr);
            Details = string.Join("     ", parts.Select(x => x.Replace(' ', (char)160)));   // a narrow hero wraps between items, never inside "GDM Desativado"

            double D(string? v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
            string vRow = ddr5 ? "VDD (MEM)" : "DRAM (VDIMM)", sRow = ddr5 ? "VSOC" : "SOC (VSOC)";
            double vRec = D(V(vRow, 1)), vMax = D(V(vRow, 2)), soc = D(V(sRow, 1));
            string Vt(string row, int col) => L.Value(V(row, col));
            Gauges.Add(new(L["g.vdimm"], $"{Vt(vRow, 1)} V", vMax > vRec + 0.004 ? L.Format("g.upto", Vt(vRow, 2)) : null, (vRec - 1.10) / (1.60 - 1.10)));
            Gauges.Add(new("VSOC", $"{Vt(sRow, 1)} V", L.Format("g.upto", Vt(sRow, 2)), (soc - 0.90) / (1.30 - 0.90)));
            var fclkText = r.Value(Ddr4Layout.Misc, "FCLK");
            if (!string.IsNullOrEmpty(fclkText) && fclkText != "N/A")
            {
                double fclk = D(fclkText.Split(' ', '-')[0]);
                Gauges.Add(new("FCLK", fclkText.Replace(" MHz", ""), "MHz", fclk / 2400));
            }
        }
        if (r.Ok)
        {
            Metrics.Add(new(L["m.primaries"], $"{P("tCL")}-{P("tRCDRD")}-{P("tRP")}-{P("tRAS")}", $"tRC {P("tRC")}"));
            if (int.TryParse(P("tRFC"), out var rfc))
                Metrics.Add(new("tRFC", rfc.ToString(), string.Create(CultureInfo.InvariantCulture, $"{rfc * tck:0} ns")));
            Metrics.Add(new(ddr5 ? "VDD / VDDQ" : "VDIMM", $"{L.Value(V(ddr5 ? "VDD (MEM)" : "DRAM (VDIMM)", 1))} V"));
            Metrics.Add(new("VSOC", $"{L.Value(V(ddr5 ? "VSOC" : "SOC (VSOC)", 1))} V"));
            var fclk = r.Value(Ddr4Layout.Misc, "FCLK");
            if (!string.IsNullOrEmpty(fclk) && fclk != "N/A") Metrics.Add(new("FCLK", fclk.EndsWith(" MHz") ? fclk : fclk + " MHz"));
            Metrics.Add(new("GDM", L.Value(r.Value(Ddr4Layout.Misc, "Gear Down Mode")), ddr5 ? (r.Value(Ddr4Layout.Misc, "UCLK DIV1 MODE")?.Contains("1:1") == true ? "UCLK 1:1" : "UCLK 2:1") : r.Value(Ddr4Layout.Misc, "Command Rate")));
        }

        bool anyCurrent = r.Sections.Any(s => s.Rows.Any(x => x.Current != null));
        string currentHeader = r.CurrentMTs is { } cm && cm != i.MTs ? $"{L["col.current"]} ({cm})" : L["col.current"];
        SectionViewModel? S(string key) => r.Section(key) is { } s ? new SectionViewModel(s, anyCurrent, key is Ddr4Layout.Primary or Ddr4Layout.Secondary or Ddr4Layout.Tertiary ? tck : null, currentHeader) : null;
        List<SectionViewModel> Of(params string[] keys) => keys.Select(S).Where(s => s != null).ToList()!;
        Tabs = new List<TabViewModel>
        {
            new(L["tab.timings"], Of(Ddr4Layout.Primary, Ddr4Layout.Secondary, Ddr4Layout.Tertiary)),
            new(L["tab.controller"], Of(Ddr4Layout.Misc)),
            new(L["tab.voltages"], Of(Ddr4Layout.Voltages, "sec.reference", Ddr4Layout.MainVoltages)),
            new(L["tab.signal"], Of(Ddr4Layout.Termination, Ddr4Layout.CadBus, Ddr4Layout.CadTimings)),
            new(L["tab.advanced"], Of(Ddr4Layout.Pmu, Ddr4Layout.Interleave)),
        }.Where(t => t.Sections.Count > 0).ToList();

        var ordered = r.Notices.OrderBy(n => n.Level switch { NoticeLevel.Error => 0, NoticeLevel.Warning => 1, _ => 2 }).Select(n => new NoticeViewModel(n)).ToList();
        Alerts = ordered.Where(n => n.Level != NoticeLevel.Info).ToList();
        Notes = ordered;
        NotesHeader = L.Format("tab.notes", ordered.Count);
        if (r.Ok && ordered.Count > 0) Tabs.Add(new TabViewModel(NotesHeader, new(), ordered));
    }
}

public sealed class SectionViewModel
{
    static Loc L => Loc.Instance;

    public string Key { get; }
    public string Title { get; }
    public string Source { get; }
    public string FirstHeader { get; }
    public bool IsNew { get; }
    public string?[] Headers { get; }   // value columns (up to 4), ns, current
    public bool ShowNs { get; }
    public bool ShowCurrent { get; }
    public List<RowViewModel> Rows { get; }

    public SectionViewModel(ResultSection s, bool anyCurrent, double? tck, string currentHeader)
    {
        Key = s.TitleKey;
        Title = L[s.TitleKey];
        Source = L["prov." + s.Provenance];
        FirstHeader = L[s.TitleKey == "sec.reference" ? "col.kit" : "col.param"];
        IsNew = s.Provenance is Provenance.Extended or Provenance.New;
        ShowNs = tck != null;
        ShowCurrent = anyCurrent && s.Rows.Any(r => r.Current != null);
        Headers = Enumerable.Range(0, 4).Select(c => c < s.ColumnKeys.Length ? L[s.ColumnKeys[c]] : null)
            .Append(ShowNs ? "ns" : null).Append(ShowCurrent ? currentHeader : null).ToArray();
        int recCol = Array.IndexOf(s.ColumnKeys, "col.rec") is var ix and >= 0 ? ix : 0;
        // timings compare as tighter/looser; everything else (voltages, ohms) against the recommendation or the min..max range
        int minCol = Array.IndexOf(s.ColumnKeys, "col.min"), maxCol = Array.IndexOf(s.ColumnKeys, "col.max");
        bool timing = s.TitleKey is Ddr4Layout.Primary or Ddr4Layout.Secondary or Ddr4Layout.Tertiary;
        Rows = s.Rows.Select(r => new RowViewModel(r, s.ColumnKeys.Length, recCol, ShowCurrent, tck, timing, minCol, maxCol)).ToList();
    }
}

public sealed class RowViewModel
{
    static Loc L => Loc.Instance;

    public string Name { get; }
    public string? Hint { get; }
    public string? V0 { get; }
    public string? V1 { get; }
    public string? V2 { get; }
    public string? V3 { get; }
    public string? Ns { get; }
    public string? Current { get; }
    public string DiffGlyph { get; } = "";
    public Brush DiffBrush { get; } = Brushes.Transparent;
    public string? DiffTip { get; }

    public RowViewModel(ResultRow r, int cols, int recCol, bool showCurrent, double? tck, bool timing = true, int minCol = -1, int maxCol = -1)
    {
        Name = r.Name;
        Hint = r.HintKey?.Split('|') is { } h ? (h.Length > 1 ? L.Format(h[0], h[1..]) : L[h[0]]) : null;
        string? Cell(int c) => c < cols ? L.Value(r.Values.ElementAtOrDefault(c)) : null;
        V0 = Cell(0); V1 = Cell(1); V2 = Cell(2); V3 = Cell(3);
        if (tck is { } t)
            Ns = int.TryParse(r.Values.ElementAtOrDefault(0), out var cyc) && !Name.StartsWith("tREFI") ? (cyc * t).ToString(cyc * t >= 100 ? "0" : "0.00", CultureInfo.InvariantCulture) : "";
        if (!showCurrent) return;
        Current = r.Current is { } cur ? L.Value(cur) : "";
        if (r.Current is null or "—") return;   // "—" = the board does not report it

        string rec = r.Values.ElementAtOrDefault(recCol) ?? "";
        var res = Application.Current.Resources;
        if (double.TryParse(r.Current, NumberStyles.Float, CultureInfo.InvariantCulture, out var c) &&
            double.TryParse(rec, NumberStyles.Float, CultureInfo.InvariantCulture, out var want))
        {
            double D(int col) => double.TryParse(r.Values.ElementAtOrDefault(col), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : double.NaN;
            double lo = minCol >= 0 ? D(minCol) : double.NaN, hi = maxCol >= 0 ? D(maxCol) : double.NaN;
            if (Math.Abs(c - want) < 1e-6) { DiffGlyph = "="; DiffBrush = (Brush)res["MutedBrush"]; DiffTip = L["ui.diff.same"]; }
            else if (timing)
            {
                if (c < want) { DiffGlyph = "▼"; DiffBrush = (Brush)res["TighterBrush"]; DiffTip = L["ui.diff.tighter"]; }
                else { DiffGlyph = "▲"; DiffBrush = (Brush)res["LooserBrush"]; DiffTip = L["ui.diff.looser"]; }
            }
            // voltages: inside the min..max band is fine; ohms / drive strengths: just above or below the recommendation
            else if (!double.IsNaN(lo) && !double.IsNaN(hi) && c >= lo - 1e-6 && c <= hi + 1e-6)
            { DiffGlyph = "✓"; DiffBrush = (Brush)res["SuccessBrush"]; DiffTip = L["ui.diff.inrange"]; }
            else if (c < want) { DiffGlyph = "▼"; DiffBrush = (Brush)res[double.IsNaN(lo) ? "InfoBrush" : "WarnBrush"]; DiffTip = L[double.IsNaN(lo) ? "ui.diff.below" : "ui.diff.underrange"]; }
            else { DiffGlyph = "▲"; DiffBrush = (Brush)res[double.IsNaN(hi) ? "InfoBrush" : "WarnBrush"]; DiffTip = L[double.IsNaN(hi) ? "ui.diff.above" : "ui.diff.overrange"]; }
        }
        else if (rec.Length > 0 && !rec.StartsWith("Auto"))
        {
            bool same = string.Equals(Loc.Display(rec), Loc.Display(r.Current), StringComparison.OrdinalIgnoreCase);
            DiffGlyph = same ? "=" : "≠";
            DiffBrush = (Brush)res[same ? "MutedBrush" : "LooserBrush"];
            DiffTip = same ? L["ui.diff.same"] : L["ui.diff.other"];
        }
    }
}
