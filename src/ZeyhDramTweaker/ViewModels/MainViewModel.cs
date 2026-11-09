using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using ZeyhDramTweaker.Core.Engines;
using ZeyhDramTweaker.Core.Export;
using ZeyhDramTweaker.Core.Hardware;
using ZeyhDramTweaker.Core.Import;
using ZeyhDramTweaker.Core.Localization;
using ZeyhDramTweaker.Core.Model;

namespace ZeyhDramTweaker.ViewModels;

/// <summary>One XMP/EXPO nanosecond input of the Manual profile.</summary>
public sealed class NsField(string name, Action changed) : ObservableObject
{
    public string Name { get; } = name;
    string _text = "";
    public string Text { get => _text; set { if (Set(ref _text, value)) changed(); } }
    public double Value => double.TryParse(Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

/// <summary>One kind of stick read from the SPD (identical sticks are grouped), with the profiles to apply.</summary>
public sealed class ModuleViewModel(SpdModule m, int count, string slots, string icName, RelayCommand apply)
{
    public SpdModule Model { get; } = m;
    public string Title { get; } = (count > 1 ? $"{count}× " : "") + (string.IsNullOrWhiteSpace(m.PartNumber) ? m.ModuleVendor : m.PartNumber);
    public string Chip { get; } = $"{m.DramVendor} {m.DensityGbit} Gb x{m.Width}" + (m.DieLetter is { } d ? $"  {d}-die" : "");
    public string Size { get; } = $"{m.CapacityGB} GB  {m.Ranks}R  {slots}";
    public string IcName { get; } = icName;
    public bool IcUncertain { get; } = !m.IcCertain;
    public List<ProfileViewModel> Profiles { get; } = m.Profiles.Select(p => new ProfileViewModel(p, m, apply)).ToList();
}

public sealed class ProfileViewModel(SpdProfile p, SpdModule m, RelayCommand apply)
{
    public SpdProfile Profile { get; } = p;
    public SpdModule Module { get; } = m;
    public string Name { get; } = p.Name;
    public string Speed { get; } = $"{p.MTs} MT/s";
    public string Timings { get; } = p.Label;
    public string Volt { get; } = p.Vdd is { } v ? string.Create(CultureInfo.InvariantCulture, $"{v:0.00} V") : "";
    public RelayCommand Apply { get; } = apply;
}

public sealed partial class MainViewModel : ObservableObject
{
    static Loc L => Loc.Instance;
    bool _suspend;

    public MainViewModel()
    {
        ReadPcCommand = new RelayCommand(ReadFromPc);
        ImportCommand = new RelayCommand(ImportThaiphoon);
        ElevateCommand = new RelayCommand(RelaunchElevated);
        ApplyProfileCommand = new RelayCommand(p => { if (p is ProfileViewModel pv) ApplyProfile(pv.Module, pv.Profile); });
        CopyCommand = new RelayCommand(() => { if (HasResult) { Clipboard.SetText(TextExport.Build(Result!.Model)); Status = L["ui.copied"]; } }, _ => HasResult);
        SavePngCommand = new RelayCommand(() => SavePngRequested?.Invoke(this, EventArgs.Empty), _ => HasResult);
        ShowNotesCommand = new RelayCommand(() => ShowNotesRequested?.Invoke(this, EventArgs.Empty));
        ReadFromCalcCommand = new RelayCommand(() => { SelectedPage = 1; ReadFromPc(); }, _ => !IsBusy);
        SafeCommand = new RelayCommand(() => SelectedPreset = Presets[0]);
        FastCommand = new RelayCommand(() => SelectedPreset = Presets[1]);
        foreach (var n in new[] { "tCL", "tRCDWR", "tRCDRD", "tRP", "tRAS", "tRC", "tRRDS", "tRRDL", "tFAW", "tRFC" })
            NsFields.Add(new NsField(n, Recalculate));
        Languages = new() { new("pt", "PT"), new("en", "EN") };
        _language = Languages.First(l => l.Value == L.Language);
        BuildStatic();
        BuildPlatformOptions(Platform.Zen3);
        IsAdmin = SystemProbe.IsAdministrator();
        PawnIoVersion = PawnIo.InstalledVersion?.ToString(3);
        InitPages();
    }

    public event EventHandler? SavePngRequested;
    public event EventHandler? ShowNotesRequested;
    public RelayCommand ShowNotesCommand { get; }
    public RelayCommand ReadFromCalcCommand { get; }
    /// <summary>There is something to copy, save or test (Manual without values and unsupported combinations have nothing).</summary>
    public bool HasResult => Result?.Ok == true;
    public RelayCommand ReadPcCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand ElevateCommand { get; }
    public RelayCommand ApplyProfileCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand SavePngCommand { get; }
    public RelayCommand SafeCommand { get; }
    public RelayCommand FastCommand { get; }

    // ------------------------------------------------------------------ inputs (every change recalculates)
    List<Option<Platform>> _platforms = new();
    public List<Option<Platform>> Platforms { get => _platforms; private set => Set(ref _platforms, value); }
    Option<Platform>? _platform;
    public Option<Platform>? SelectedPlatform { get => _platform; set { if (Set(ref _platform, value) && value != null) OnPlatformChanged(); } }

    List<Option<string>> _ics = new();
    public List<Option<string>> Ics { get => _ics; private set => Set(ref _ics, value); }
    Option<string>? _ic;
    public Option<string>? SelectedIc { get => _ic; set { if (Set(ref _ic, value)) Recalculate(); } }

    List<Option<Profile>> _profiles = new();
    public List<Option<Profile>> Profiles { get => _profiles; private set => Set(ref _profiles, value); }
    Option<Profile>? _profile;
    public Option<Profile>? SelectedProfile { get => _profile; set { if (Set(ref _profile, value)) { Raise(nameof(IsManual)); Recalculate(); } } }
    public bool IsManual => SelectedProfile?.Value == Profile.Manual;

    List<Option<int>> _ranks = new();
    public List<Option<int>> RankOptions { get => _ranks; private set => Set(ref _ranks, value); }
    Option<int>? _rank;
    public Option<int>? SelectedRank { get => _rank; set { if (Set(ref _rank, value)) Recalculate(); } }

    List<Option<int>> _dimms = new();
    public List<Option<int>> DimmOptions { get => _dimms; private set => Set(ref _dimms, value); }
    Option<int>? _dimm;
    public Option<int>? SelectedDimms { get => _dimm; set { if (Set(ref _dimm, value)) Recalculate(); } }

    List<Option<int>> _topologies = new();
    public List<Option<int>> Topologies { get => _topologies; private set => Set(ref _topologies, value); }
    Option<int>? _topology;
    public Option<int>? SelectedTopology { get => _topology; set { if (Set(ref _topology, value)) Recalculate(); } }

    List<int> _frequencies = new();
    public List<int> Frequencies { get => _frequencies; private set => Set(ref _frequencies, value); }
    int _mts;
    public int SelectedMts { get => _mts; set { if (Set(ref _mts, value)) Recalculate(); } }

    public List<Option<Preset>> Presets { get; private set; } = new();
    Option<Preset>? _preset;
    public Option<Preset>? SelectedPreset { get => _preset; set { if (Set(ref _preset, value)) Recalculate(); } }

    public ObservableCollection<NsField> NsFields { get; } = new();
    public ObservableCollection<ModuleViewModel> Modules { get; } = new();
    bool _hasModules;
    public bool HasModules { get => _hasModules; private set => Set(ref _hasModules, value); }

    public List<Option<string>> Languages { get; }
    Option<string> _language;
    public Option<string> SelectedLanguage
    {
        get => _language;
        set
        {
            if (!Set(ref _language, value) || value == null) return;
            L.Language = value.Value;
            var keep = Snapshot();
            BuildStatic();
            BuildPlatformOptions(keep.Platform);
            Restore(keep);
            RebuildModules();
            RaiseStatusBar();
            Raise(nameof(LastReadText)); Raise(nameof(Tm5SourcesText));
            BuildNav();
            RefreshTm5();
            LoadLatency();
            Recalculate();
        }
    }

    // ------------------------------------------------------------------ output
    ResultViewModel? _result;
    public ResultViewModel? Result { get => _result; private set { if (Set(ref _result, value)) Raise(nameof(HasResult)); } }

    string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }
    bool _needsElevation;
    public bool NeedsElevation { get => _needsElevation; set => Set(ref _needsElevation, value); }
    bool _busy;
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }

    public bool IsAdmin { get; }
    public string? PawnIoVersion { get; }
    string _cpuName = "";
    public string CpuText => string.IsNullOrEmpty(_cpuName) ? "—" : _cpuName;
    string _memText = "";
    public string MemoryText => _memText;
    public string DriverText => PawnIoVersion is { } v ? L.Format("ui.status.pawnio", v) : L["ui.status.nopawnio"];
    public string AdminText => IsAdmin ? L["ui.status.admin"] : L["ui.status.user"];
    void RaiseStatusBar() { Raise(nameof(CpuText)); Raise(nameof(MemoryText)); Raise(nameof(DriverText)); Raise(nameof(AdminText)); }

    CurrentMemory? _current;
    List<SpdModule> _spd = new();
    DateTime? _lastRead;
    public bool HasRead => _lastRead != null;
    public string LastReadText => _lastRead is { } t ? L.Format("mem.lastread", t.ToString("HH:mm", CultureInfo.InvariantCulture)) : "";
    void MarkRead() { _lastRead = DateTime.Now; Raise(nameof(HasRead)); Raise(nameof(LastReadText)); }

    // ------------------------------------------------------------------ report header (PNG): who, what, when
    string? _board;
    public IEnumerable<(string Label, string Value)> ReportFacts()
    {
        if (string.IsNullOrEmpty(_cpuName)) try { _cpuName = SystemProbe.ReadCpuId().Name; } catch { }
        _board ??= SystemProbe.ReadBoard();
        yield return (L["report.when"], DateTime.Now.ToString(L.Language == "pt" ? "dd/MM/yyyy HH:mm" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(_cpuName)) yield return (L["set.cpu"], _cpuName.Trim());
        if (!string.IsNullOrEmpty(_board)) yield return (L["report.board"], _board);
        if (!string.IsNullOrEmpty(_memText)) yield return (L["report.memory"], _memText);
        if (Result is { } r)
        {
            var i = r.Model.Input;
            var ic = Catalog.Ic(i.IcId);
            yield return (L["report.for"], L.Format("report.for.value", L[Catalog.Get(i.Platform).NameKey], ic.Name.StartsWith("ic.") ? L[ic.Name] : ic.Name, L.Profile(i.Profile), i.Ranks, i.Dimms));
        }
    }

    // ------------------------------------------------------------------ option lists
    void BuildStatic()
    {
        Presets = new() { new(Preset.Safe, "SAFE"), new(Preset.Fast, "FAST") };
        Raise(nameof(Presets));
        var keep = _preset?.Value ?? Preset.Safe;
        _preset = Presets.First(p => p.Value == keep);
        Raise(nameof(SelectedPreset));
    }

    void BuildPlatformOptions(Platform select)
    {
        _suspend = true;
        Platforms = Catalog.Platforms.Select(p => new Option<Platform>(p.Id, L[p.NameKey])).ToList();
        _platform = null;
        _suspend = false;
        SelectedPlatform = Platforms.First(p => p.Value == select);
    }

    void OnPlatformChanged()
    {
        _suspend = true;
        var plat = Catalog.Get(SelectedPlatform!.Value);
        var keepIc = SelectedIc?.Value; var keepMts = SelectedMts; var keepRank = SelectedRank?.Value; var keepDimm = SelectedDimms?.Value; var keepProf = SelectedProfile?.Value;
        Ics = Catalog.Ics.Where(i => i.Mem == plat.Mem).Select(i => new Option<string>(i.Id, i.Name.StartsWith("ic.") ? L[i.Name] : i.Name)).ToList();
        SelectedIc = Ics.FirstOrDefault(i => i.Value == keepIc) ?? Ics[0];
        Profiles = Catalog.ProfilesFor(plat.Mem).Select(p => new Option<Profile>(p, L.Profile(p))).ToList();
        SelectedProfile = Profiles.FirstOrDefault(p => p.Value == keepProf) ?? Profiles.First(p => p.Value is Profile.A0B0 or Profile.Standard);
        RankOptions = new() { new(1, L["ui.single"]), new(2, L["ui.dual"]) };
        SelectedRank = RankOptions.FirstOrDefault(r => r.Value == keepRank) ?? RankOptions[0];
        DimmOptions = (plat.Mem == MemGen.DDR5 ? new[] { 2, 4 } : new[] { 1, 2, 4 }).Select(d => new Option<int>(d, d.ToString())).ToList();
        SelectedDimms = DimmOptions.FirstOrDefault(d => d.Value == keepDimm) ?? DimmOptions.First(d => d.Value == 2);
        Topologies = Catalog.TopologiesFor(plat).Select((t, ix) => new Option<int>(ix, L[t.NameKey])).ToList();
        SelectedTopology = Topologies[0];
        Frequencies = plat.Frequencies.ToList();
        SelectedMts = Nearest(Frequencies, keepMts > 0 && (keepMts > 4800) == (plat.Mem == MemGen.DDR5) ? keepMts : (plat.Mem == MemGen.DDR5 ? 6000 : 3600));
        Raise(nameof(IsDdr4));
        PickTm5ConfigFor(plat.Mem);
        _suspend = false;
        Recalculate();
    }

    public bool IsDdr4 => SelectedPlatform != null && Catalog.Get(SelectedPlatform.Value).Mem == MemGen.DDR4;
    static int Nearest(List<int> list, int want) => list.OrderBy(f => Math.Abs(f - want)).First();

    record Snap(Platform Platform, string? Ic, Profile? Profile, int? Rank, int? Dimms, int? Topo, int Mts);
    Snap Snapshot() => new(SelectedPlatform?.Value ?? Platform.Zen3, SelectedIc?.Value, SelectedProfile?.Value, SelectedRank?.Value, SelectedDimms?.Value, SelectedTopology?.Value, SelectedMts);
    void Restore(Snap s)
    {
        _suspend = true;
        SelectedIc = Ics.FirstOrDefault(i => i.Value == s.Ic) ?? SelectedIc;
        SelectedProfile = Profiles.FirstOrDefault(p => p.Value == s.Profile) ?? SelectedProfile;
        SelectedRank = RankOptions.FirstOrDefault(r => r.Value == s.Rank) ?? SelectedRank;
        SelectedDimms = DimmOptions.FirstOrDefault(d => d.Value == s.Dimms) ?? SelectedDimms;
        SelectedTopology = Topologies.FirstOrDefault(t => t.Value == s.Topo) ?? SelectedTopology;
        if (Frequencies.Contains(s.Mts)) SelectedMts = s.Mts;
        _suspend = false;
    }

    // ------------------------------------------------------------------ calculation
    public CalcInput BuildInput()
    {
        ManualTimings? xmp = null;
        if (IsManual)
        {
            double V(string n) => NsFields.First(f => f.Name == n).Value;
            xmp = new ManualTimings
            {
                tCL = V("tCL"), tRCDWR = V("tRCDWR") > 0 ? V("tRCDWR") : V("tRCDRD"), tRCDRD = V("tRCDRD"), tRP = V("tRP"),
                tRAS = V("tRAS"), tRC = V("tRC"), tRRDS = V("tRRDS"), tRRDL = V("tRRDL"), tFAW = V("tFAW"), tRFC = V("tRFC"),
            };
        }
        return new CalcInput
        {
            Platform = SelectedPlatform!.Value, IcId = SelectedIc!.Value, Preset = SelectedPreset?.Value ?? Preset.Safe, Profile = SelectedProfile!.Value,
            Ranks = SelectedRank?.Value ?? 1, Dimms = SelectedDimms?.Value ?? 2, TopologyIndex = SelectedTopology?.Value ?? 0,
            MTs = SelectedMts, Manual = xmp,
        };
    }

    public void Recalculate()
    {
        if (_suspend || SelectedPlatform == null || SelectedIc == null || SelectedProfile == null || SelectedMts == 0) return;
        var r = Calculator.Calculate(BuildInput());
        if (_current != null && _current.Mem == Catalog.Get(r.Input.Platform).Mem) Calculator.AttachCurrent(r, _current);
        Result = new ResultViewModel(r);
        Raise(nameof(CurrentTimings)); Raise(nameof(HasCurrent)); Raise(nameof(CurrentHeader));
    }

    // ------------------------------------------------------------------ SPD / profiles
    public void ApplyProfile(SpdModule m, SpdProfile p)
    {
        _suspend = true;
        if (Catalog.Get(SelectedPlatform!.Value).Mem != m.Mem) SelectedPlatform = Platforms.First(o => Catalog.Get(o.Value).Mem == m.Mem);
        _suspend = true;
        if (m.IcId != null && Ics.FirstOrDefault(o => o.Value == m.IcId) is { } ic) SelectedIc = ic;
        SelectedRank = RankOptions.FirstOrDefault(r => r.Value == Math.Min(2, m.Ranks)) ?? SelectedRank;
        SelectedProfile = Profiles.First(o => o.Value == Profile.Manual);
        SelectedMts = Nearest(Frequencies, p.MTs);
        FillNs(p.Timings);
        _suspend = false;
        Status = L.Format("ui.profileApplied", p.Name, m.Title());
        Recalculate();
    }

    void FillNs(ManualTimings t)
    {
        var map = new Dictionary<string, double>
        {
            ["tCL"] = t.tCL, ["tRCDWR"] = t.tRCDWR, ["tRCDRD"] = t.tRCDRD, ["tRP"] = t.tRP, ["tRAS"] = t.tRAS, ["tRC"] = t.tRC,
            ["tRRDS"] = t.tRRDS, ["tRRDL"] = t.tRRDL, ["tFAW"] = t.tFAW, ["tRFC"] = t.tRFC,
        };
        bool s = _suspend; _suspend = true;
        foreach (var f in NsFields) f.Text = map[f.Name] > 0 ? map[f.Name].ToString("0.000", CultureInfo.InvariantCulture) : "";
        _suspend = s;
    }

    static string IcLabel(SpdModule m) => m.IcId != null ? Catalog.Ic(m.IcId) is var x && x.Name.StartsWith("ic.") ? L[x.Name] : Catalog.Ic(m.IcId).Name : L["ui.ic.unknown"];

    /// <summary>"DDR4-3800  2× F4-4400C17-8GVK (Samsung B-die)": what is in the PC, for the status bar and the report.</summary>
    static string MemSummary(CurrentMemory cur, List<SpdModule> spd) =>
        $"DDR{(cur.Mem == MemGen.DDR5 ? 5 : 4)}-{cur.MTs}  {cur.Dimms}× " + (spd.FirstOrDefault() is { } m0 ? $"{m0.Title()} ({IcLabel(m0)})" : "");

    void RebuildModules()
    {
        Modules.Clear();
        foreach (var g in _spd.GroupBy(m => (m.PartNumber, m.DramVendor, m.DensityGbit, m.CapacityGB, string.Join("|", m.Profiles.Select(p => p.Name + p.MTs + p.Label)))))
        {
            var m = g.First();
            string ic = IcLabel(m);
            Modules.Add(new ModuleViewModel(m, g.Count(), string.Join(", ", g.Select(x => x.Slot)), ic, ApplyProfileCommand));
        }
        HasModules = Modules.Count > 0;
    }

    void ImportThaiphoon()
    {
        var dlg = new OpenFileDialog { Title = L["ui.importTp"], Filter = "Thaiphoon Burner (*.htm;*.html)|*.htm;*.html", CheckFileExists = true };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var rep = ThaiphoonImport.Parse(File.ReadAllText(dlg.FileName));
            _suspend = true;
            if (rep.Mem is { } mem && Catalog.Get(SelectedPlatform!.Value).Mem != mem) SelectedPlatform = Platforms.First(p => Catalog.Get(p.Value).Mem == mem);
            _suspend = true;
            if (rep.SuggestedIcId is { } ic && Ics.FirstOrDefault(o => o.Value == ic) is { } opt) SelectedIc = opt;
            if (rep.Ranks is int rk) SelectedRank = RankOptions.FirstOrDefault(r => r.Value == rk) ?? SelectedRank;
            SelectedProfile = Profiles.First(p => p.Value == Profile.Manual);
            FillNs(rep.Timings);
            _suspend = false;
            Status = L.Format("ui.imported", string.Join("  ", new[] { rep.PartNumber, rep.DramComponents }.Where(s => !string.IsNullOrWhiteSpace(s))));
            Recalculate();
        }
        catch (ThaiphoonImport.ImportException ex) { _suspend = false; Status = L[ex.Key]; }
        catch (Exception ex) { _suspend = false; Status = ex.Message; }
    }

    public void DetectSystem()
    {
        try
        {
            var cpu = SystemProbe.ReadCpuId();
            var dimms = SystemProbe.ReadDimms();
            _cpuName = cpu.Name;
            if (SystemProbe.PlatformOf(cpu) is { } plat) BuildPlatformOptions(plat);
            if (dimms.Count > 0)
            {
                _suspend = true;
                SelectedDimms = DimmOptions.FirstOrDefault(d => d.Value == dimms.Count) ?? SelectedDimms;
                if (dimms[0].ConfiguredMTs > 0) SelectedMts = Nearest(Frequencies, dimms[0].ConfiguredMTs);
                _suspend = false;
                _memText = $"{dimms.Count}× {dimms[0].PartNumber}  {dimms[0].ConfiguredMTs} MT/s";
            }
            RaiseStatusBar();
            Recalculate();
        }
        catch { _suspend = false; }
    }

    /// <summary>Reads the SPD of every stick (IC, XMP/EXPO) and the timings the controller is running now.</summary>
    async void ReadFromPc()
    {
        if (!IsAdmin) { RelaunchElevated(); return; }
        IsBusy = true;
        Status = L["ui.reading"];
        try
        {
            var cpu = SystemProbe.ReadCpuId();
            var plat = SystemProbe.PlatformOf(cpu) is { } p ? Catalog.Get(p) : Catalog.Get(SelectedPlatform!.Value);
            var (spd, cur, spdError) = await Task.Run(() =>
            {
                List<SpdModule> s = new(); string? err = null;
                try { s = SpdReader.ReadAll(plat.Mem); } catch (PawnIoException ex) { err = ex.Key; } catch (Exception ex) { err = ex.Message; }
                return (s, UmcReader.Read(plat.Mem), err);
            });
            _spd = spd; _current = cur;
            if (plat.Id != SelectedPlatform!.Value) SelectedPlatform = Platforms.First(o => o.Value == plat.Id);
            RebuildModules();
            _suspend = true;
            SelectedRank = RankOptions.FirstOrDefault(r => r.Value == cur.Ranks) ?? SelectedRank;
            SelectedDimms = DimmOptions.FirstOrDefault(d => d.Value == cur.Dimms) ?? SelectedDimms;
            if (cur.MTs > 0) SelectedMts = Nearest(Frequencies, cur.MTs);
            _suspend = false;
            _memText = MemSummary(cur, spd);
            RaiseStatusBar();
            NeedsElevation = false;
            // apply the IC of the first stick right away; the user picks a profile from the list
            if (spd.FirstOrDefault() is { IcId: { } icId } && Ics.FirstOrDefault(o => o.Value == icId) is { } ico) { _suspend = true; SelectedIc = ico; _suspend = false; }
            Status = spdError != null ? L.Format("ui.readPartial", L[spdError]) : L.Format("ui.readOk2", spd.Count, cur.MTs);
            MarkRead();
            Recalculate();
        }
        catch (PawnIoException ex) { Status = L[ex.Key]; NeedsElevation = ex.Key == "pawnio.admin"; }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("umc.")) { Status = L[ex.Message]; }
        catch (Exception ex) { Status = ex.Message; }
        finally { _suspend = false; IsBusy = false; }
    }

    /// <summary>QA/preview: load a --spd-dump file (raw SPD + controller timings) as if it had just been read.</summary>
    public void LoadDump(string path)
    {
        var lines = File.ReadAllLines(path);
        var plat = Catalog.Get(SelectedPlatform!.Value);
        if (lines.FirstOrDefault(l => l.StartsWith("CPU: ")) is { } cpuLine)
            _cpuName = System.Text.RegularExpressions.Regex.Match(cpuLine, @"CPU: (.+?)\s{2,}").Groups[1].Value;
        var spd = new List<SpdModule>();
        for (int i = 0; i < lines.Length; i++)
        {
            var m = System.Text.RegularExpressions.Regex.Match(lines[i], @"^SPD 0x([0-9A-F]{2}) .*?(\d+) bytes");
            if (!m.Success) continue;
            int addr = Convert.ToInt32(m.Groups[1].Value, 16), len = int.Parse(m.Groups[2].Value);
            var raw = new List<byte>();
            for (int j = i + 1; j < lines.Length && raw.Count < len && lines[j].StartsWith("  "); j++)
                raw.AddRange(Convert.FromHexString(lines[j].Split(':')[1].Trim()));
            var mod = len == 1024 ? SpdDecoder.DecodeDdr5(raw.ToArray(), addr) : SpdDecoder.DecodeDdr4(raw.ToArray(), addr);
            if (mod != null) spd.Add(mod);
        }
        var umc = lines.FirstOrDefault(l => l.StartsWith("UMC:"));
        var vals = lines.SkipWhile(l => !l.StartsWith("UMC:")).Skip(1).FirstOrDefault() ?? "";
        if (umc != null)
        {
            var mm = System.Text.RegularExpressions.Regex.Match(umc, @"(\d+) MT/s, (\d+) DIMM\(s\), (\d) rank");
            var cur = new CurrentMemory { Mem = plat.Mem, MTs = int.Parse(mm.Groups[1].Value), Dimms = int.Parse(mm.Groups[2].Value), Ranks = int.Parse(mm.Groups[3].Value) };
            // "name=value" pairs separated by two spaces (names and values may contain single spaces)
            foreach (var pair in vals.Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (pair.IndexOf('=') is > 0 and var eq) cur.Values[pair[..eq]] = pair[(eq + 1)..];
            _current = cur;
            _suspend = true;
            SelectedMts = Nearest(Frequencies, cur.MTs);
            SelectedDimms = DimmOptions.FirstOrDefault(d => d.Value == cur.Dimms) ?? SelectedDimms;
            _suspend = false;
        }
        _spd = spd;
        RebuildModules();
        if (spd.FirstOrDefault() is { IcId: { } icId } && Ics.FirstOrDefault(o => o.Value == icId) is { } ico) { _suspend = true; SelectedIc = ico; _suspend = false; }
        Status = L.Format("ui.readOk2", spd.Count, _current?.MTs ?? 0);
        if (_current != null)
            _memText = MemSummary(_current, spd);
        MarkRead();
        Recalculate();
    }

    void RelaunchElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--read --lang {L.Language}") { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception) { /* UAC declined */ }
    }
}

static class SpdModuleExt
{
    public static string Title(this SpdModule m) => string.IsNullOrWhiteSpace(m.PartNumber) ? m.ModuleVendor : m.PartNumber;
}
