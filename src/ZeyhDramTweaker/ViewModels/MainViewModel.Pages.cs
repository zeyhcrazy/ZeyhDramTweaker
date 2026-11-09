using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using ZeyhDramTweaker.Core.Localization;
using ZeyhDramTweaker.Core.Model;
using ZeyhDramTweaker.Core.Hardware;
using ZeyhDramTweaker.Core.Test;

namespace ZeyhDramTweaker.ViewModels;

public sealed record NavItem(string Glyph, string Label);

public sealed class Tm5SessionViewModel(Tm5Session s, bool showSource = true)
{
    static Loc L => Loc.Instance;
    static Brush B(string k) => (Brush)Application.Current.Resources[k];
    public string When { get; } = s.Started == default ? "" : s.Started.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
    public string Config { get; } = s.Config;
    public string Duration { get; } = s.Duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
    public string Size { get; } = s.Size;
    public string Result { get; } = s.Outcome switch
    {
        Tm5Outcome.Passed => L["tm5.passed"],
        Tm5Outcome.Errors => L.Format("tm5.errors", s.Errors.Count),
        Tm5Outcome.Stopped => L["tm5.stopped"],
        Tm5Outcome.Interrupted => L["tm5.interrupted"],
        _ => L["tm5.running"],
    };
    public string Glyph { get; } = s.Outcome switch { Tm5Outcome.Passed => "", Tm5Outcome.Errors => "", Tm5Outcome.Running => "", _ => "" };
    public Brush Brush { get; } = B(s.Outcome switch { Tm5Outcome.Passed => "SuccessBrush", Tm5Outcome.Errors => "ErrorBrush", Tm5Outcome.Running => "InfoBrush", _ => "MutedBrush" });
    public Brush Tint { get; } = B(s.Outcome switch { Tm5Outcome.Passed => "SuccessTintBrush", Tm5Outcome.Errors => "ErrorTintBrush", Tm5Outcome.Running => "InfoTintBrush", _ => "HeaderBandBrush" });
    public string ErrorText { get; } = string.Join("\n", s.Errors.Take(6));
    /// <summary>Which TestMem5 wrote it, when not the app's own copy ("…\PROGRAMAS\TestMem5").</summary>
    public string? Source { get; } = showSource ? (s.Source.Length > 0 ? Tm5Service.ShortName(s.Source) : Loc.Instance["tm5.src.app"]) : null;
    public bool HasErrors { get; } = s.Errors.Count > 0;
}

/// <summary>One saved latency measurement and how it compares with the one before it.</summary>
public sealed class LatencyRowViewModel(LatencyResult r, LatencyResult? older)
{
    const double Noise = 0.6;   // back-to-back runs on the same settings land within ~0.5 ns
    static Brush B(string k) => (Brush)Application.Current.Resources[k];
    static string N(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    public string When { get; } = r.When.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
    public string Value { get; } = N(r.Ns) + " ns";
    public string Setup { get; } = r.Setup;
    public string? FullRandom { get; } = r.FullRandomNs > 0 ? Loc.Instance.Format("lat.full", N(r.FullRandomNs) + " ns") : null;
    public string? Delta { get; } = older == null ? null
        : Math.Abs(r.Ns - older.Ns) < Noise ? Loc.Instance["lat.same"]
        : Loc.Instance.Format(r.Ns < older.Ns ? "lat.faster" : "lat.slower", N(Math.Abs(r.Ns - older.Ns)));
    public Brush DeltaBrush { get; } = B(older == null || Math.Abs(r.Ns - older.Ns) < Noise ? "MutedBrush" : r.Ns < older.Ns ? "SuccessBrush" : "WarnBrush");
}

public sealed partial class MainViewModel
{
    // ------------------------------------------------------------------ memory latency (Test page)
    public RelayCommand MeasureLatencyCommand { get; private set; } = null!;
    bool _measuring;
    public bool IsMeasuring { get => _measuring; private set { if (Set(ref _measuring, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
    public ObservableCollection<LatencyRowViewModel> LatencyHistory { get; } = new();
    public LatencyRowViewModel? LatencyLast => LatencyHistory.FirstOrDefault();
    public bool HasLatency => LatencyHistory.Count > 0;
    /// <summary>Everything but the latest, which the card shows big.</summary>
    public List<LatencyRowViewModel> LatencyOlder => LatencyHistory.Skip(1).ToList();
    public bool HasOlderLatency => LatencyHistory.Count > 1;
    string _latStatus = "";
    public string LatencyStatus { get => _latStatus; private set => Set(ref _latStatus, value); }

    /// <summary>"DDR4-3800  14-13-13-38" when the controller was read, else the speed Windows reports.</summary>
    string LatencySetup()
    {
        if (_current is { } c)
        {
            string T(string k) => c.Values.GetValueOrDefault(k) ?? "?";
            return $"DDR{(c.Mem == MemGen.DDR5 ? 5 : 4)}-{c.MTs}  {T("tCL")}-{T("tRCDRD")}-{T("tRP")}-{T("tRAS")}";
        }
        try { return SystemProbe.ReadDimms() is { Count: > 0 } d && d[0].ConfiguredMTs > 0 ? $"{d[0].ConfiguredMTs} MT/s" : ""; }
        catch { return ""; }
    }

    public async Task MeasureLatency()
    {
        if (LatencyTest.Tm5Running()) { LatencyStatus = L["lat.tm5"]; return; }
        IsMeasuring = true;
        LatencyStatus = L["lat.running"];
        try
        {
            string setup = LatencySetup();
            var r = await Task.Run(() => LatencyTest.Run(setup));
            LatencyTest.Save(r);
            LoadLatency();
            LatencyStatus = "";
        }
        catch (Exception ex) { LatencyStatus = L.Format("lat.error", ex.Message); }
        finally { IsMeasuring = false; }
    }

    void LoadLatency()
    {
        var list = LatencyTest.History();
        LatencyHistory.Clear();
        for (int i = 0; i < Math.Min(6, list.Count); i++) LatencyHistory.Add(new LatencyRowViewModel(list[i], list.ElementAtOrDefault(i + 1)));
        Raise(nameof(LatencyLast)); Raise(nameof(HasLatency)); Raise(nameof(LatencyOlder)); Raise(nameof(HasOlderLatency));
    }

    // ------------------------------------------------------------------ navigation
    List<NavItem> _nav = new();
    public List<NavItem> Nav { get => _nav; private set => Set(ref _nav, value); }
    int _page;
    public int SelectedPage
    {
        get => _page;
        set
        {
            if (value < 0 || !Set(ref _page, value)) return;
            Raise(nameof(IsCalcPage)); Raise(nameof(IsMemoryPage)); Raise(nameof(IsTestPage)); Raise(nameof(IsSettingsPage));
            if (value == 2) RefreshTm5();
        }
    }
    public bool IsCalcPage => _page == 0;
    public bool IsMemoryPage => _page == 1;
    public bool IsTestPage => _page == 2;
    public bool IsSettingsPage => _page == 3;
    public RelayCommand GoCalcCommand { get; private set; } = null!;
    public RelayCommand GoTestCommand { get; private set; } = null!;
    public RelayCommand GoMemoryCommand { get; private set; } = null!;

    void BuildNav()
    {
        int keep = Math.Max(0, _page);
        Nav = new()
        {
            new("", L["nav.calc"]),
            new("", L["nav.memory"]),
            new("", L["nav.test"]),
            new("", L["nav.settings"]),
        };
        _page = -1;
        SelectedPage = keep;
    }

    void InitPages()
    {
        GoCalcCommand = new RelayCommand(() => SelectedPage = 0);
        GoMemoryCommand = new RelayCommand(() => SelectedPage = 1);
        GoTestCommand = new RelayCommand(() => SelectedPage = 2);
        StartTm5Command = new RelayCommand(StartTm5);
        OpenTm5FolderCommand = new RelayCommand(() =>
        {
            Tm5Service.EnsureExtracted();
            System.Diagnostics.Process.Start("explorer.exe", Tm5Service.Root);
        });
        AddTm5FolderCommand = new RelayCommand(AddTm5Folder);
        MeasureLatencyCommand = new RelayCommand(() => _ = MeasureLatency(), _ => !IsMeasuring);
        LoadLatency();
        BuildNav();
        try
        {
            Tm5Configs = Tm5Service.Configs();
            Raise(nameof(Tm5Configs));
            PickTm5ConfigFor(SelectedPlatform != null ? Catalog.Get(SelectedPlatform.Value).Mem : MemGen.DDR4);
            SelectedTm5Config ??= Tm5Configs.FirstOrDefault();
            _watchers.Add(Tm5Service.Watch(() => Application.Current?.Dispatcher.InvokeAsync(RefreshTm5)));
        }
        catch { Tm5Configs = new(); }
        RefreshTm5();
    }

    /// <summary>Looks for other TestMem5 copies (the Zeyh pack in Downloads...) off the UI thread and adds their logs to the history.</summary>
    public async Task FindOtherTm5()
    {
        try { UseTm5Folders(await Task.Run(Tm5Service.OtherFolders)); }
        catch { }
    }

    void UseTm5Folders(List<string> folders)
    {
        foreach (var d in folders.Where(d => !_tm5Folders.Contains(d, StringComparer.OrdinalIgnoreCase)))
        {
            _tm5Folders.Add(d);
            try { _watchers.Add(Tm5Service.Watch(() => Application.Current?.Dispatcher.InvokeAsync(RefreshTm5), d)); } catch { }
        }
        Raise(nameof(Tm5SourcesText)); Raise(nameof(HasTm5Sources));
        RefreshTm5();
    }

    void AddTm5Folder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L["tm5.addfolder.title"] };
        if (dlg.ShowDialog() != true) return;
        if (!Tm5Service.AddFolder(dlg.FolderName)) { Tm5Status = L["tm5.addfolder.bad"]; return; }
        UseTm5Folders(new() { dlg.FolderName });
        Tm5Status = L.Format("tm5.addfolder.ok", Tm5Service.ShortName(dlg.FolderName));
    }

    // ------------------------------------------------------------------ current timings (Memory page)
    public bool HasCurrent => _current != null;
    public List<KeyValuePair<string, string>> CurrentTimings => _current == null ? new() : _current.Values
        .Where(kv => kv.Key is not ("tRDRDSD / DD" or "tWRWRSD / DD" or "VTT DDR max" or "VDDG CCD / IOD" or "UCLK DIV1 MODE") && kv.Value != "—")
        .OrderBy(kv => Array.IndexOf(Order, kv.Key) is var i && i >= 0 ? i : 99)
        .Select(kv => new KeyValuePair<string, string>(kv.Key, L.Value(kv.Value))).ToList();
    static readonly string[] Order = { "tCL", "tRCDRD", "tRCDWR", "tRP", "tRAS", "tRC", "tRFC", "tRFC2", "tRFCsb", "tREFI", "tRRDS", "tRRDL", "tFAW",
        "tWTRS", "tWTRL", "tWR", "tRTP", "tCWL", "tCKE", "tRDRDSCL", "tWRWRSCL", "tRDWR", "tWRRD", "tRDRDSC", "tRDRDSD", "tRDRDDD",
        "tWRWRSC", "tWRWRSD", "tWRWRDD", "Gear Down Mode", "Command Rate", "Power Down Mode", "BankGroupSwap", "BankGroupSwap Alt", "FCLK",
        "DRAM (VDIMM)", "VTT DDR min", "VPP", "SOC (VSOC)", "VSOC", "VDDG CCD", "VDDG IOD", "cLDO VDDP", "CPU VDDP", "ProcODT", "RTT_NOM", "RTT_WR", "RTT_PARK",
        "ClkDrvStren", "AddrCmdDrvStren", "CsOdtDrvStren", "CkeDrvStren", "AddrCmdSetup", "CsOdtSetup", "CkeSetup" };
    public string CurrentHeader => _current == null ? "" : $"DDR{(_current.Mem == MemGen.DDR5 ? 5 : 4)}-{_current.MTs}  {_current.Dimms}× {_current.Ranks}R";

    // ------------------------------------------------------------------ TestMem5
    readonly List<FileSystemWatcher> _watchers = new();
    readonly List<string> _tm5Folders = new();
    public RelayCommand AddTm5FolderCommand { get; private set; } = null!;
    public bool HasTm5Sources => _tm5Folders.Count > 0;
    public string Tm5SourcesText => L.Format("tm5.sources", string.Join(", ", _tm5Folders.Select(Tm5Service.ShortName)));
    public List<string> Tm5Configs { get; private set; } = new();
    string? _tm5Config;
    public string? SelectedTm5Config { get => _tm5Config; set => Set(ref _tm5Config, value); }
    public RelayCommand StartTm5Command { get; private set; } = null!;
    public RelayCommand OpenTm5FolderCommand { get; private set; } = null!;
    public ObservableCollection<Tm5SessionViewModel> Tm5History { get; } = new();
    Tm5SessionViewModel? _tm5Last;
    public Tm5SessionViewModel? Tm5Last { get => _tm5Last; private set { Set(ref _tm5Last, value); Raise(nameof(HasTm5)); } }
    public bool HasTm5 => _tm5Last != null;
    string _tm5Status = "";
    public string Tm5Status { get => _tm5Status; private set => Set(ref _tm5Status, value); }

    public void PickTm5ConfigFor(MemGen mem)
    {
        var want = Tm5Service.DefaultConfig(mem);
        if (Tm5Configs.Contains(want)) SelectedTm5Config = want;
    }

    void StartTm5()
    {
        if (SelectedTm5Config == null) return;
        try
        {
            Tm5Service.Start(SelectedTm5Config);
            Tm5Status = L.Format("tm5.started", SelectedTm5Config);
        }
        catch (System.ComponentModel.Win32Exception) { Tm5Status = L["tm5.uac"]; }
        catch (Exception ex) { Tm5Status = ex.Message; }
    }

    public void RefreshTm5()
    {
        try
        {
            var hist = Tm5Service.History(_tm5Folders);
            Tm5History.Clear();
            var shown = hist.Take(12).ToList();
            bool mixed = shown.Select(s => s.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;   // one folder: the header already names it
            foreach (var s in shown) Tm5History.Add(new Tm5SessionViewModel(s, mixed));
            Tm5Last = Tm5History.FirstOrDefault();
        }
        catch { }
    }
}
