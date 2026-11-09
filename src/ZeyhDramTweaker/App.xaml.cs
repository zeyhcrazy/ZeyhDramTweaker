using System.IO;
using System.Text;
using System.Windows;
using ZeyhDramTweaker.Core.Hardware;
using ZeyhDramTweaker.Core.Localization;
using ZeyhDramTweaker.Core.Model;

namespace ZeyhDramTweaker;

/// <summary>
/// Command line:
///   --lang pt|en                 interface language
///   --read                       read the memory on start (used after the UAC relaunch)
///   --spd-dump file.txt          (admin) write raw SPD + decoded profiles + controller timings, then exit
///   --latency file.txt           measure the memory latency three times, write the results, then exit
///   --capture file.png [--report] [--page N] [--tab N] [--platform Zen4 --ic ddr5-hynix-a16 --mts 6000 --preset fast --profile Standard --ranks 2]
///                                render off-screen to a PNG and exit (visual QA without touching the desktop)
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var a = e.Args;
        string? Arg(string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

        if (Arg("--lang") is { } lang) Loc.Instance.Language = lang;
        if (Arg("--spd-dump") is { } dump) { SpdDump(dump); Shutdown(); return; }
        if (Arg("--latency") is { } latOut)
        {
            // QA: three measurements in a row, no window
            var lines = Enumerable.Range(0, 3).Select(_ => ZeyhDramTweaker.Core.Test.LatencyTest.Run("cli"))
                .Select(r => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"block {r.Ns:0.00} ns  ±{r.Spread:0.00}   full random {r.FullRandomNs:0.00} ns"));
            File.WriteAllLines(latOut, lines);
            Shutdown(); return;
        }

        var win = new MainWindow();
        var vm = (ViewModels.MainViewModel)win.DataContext;
        var capture = Arg("--capture");

        if (capture == null) vm.DetectSystem();
        if (Arg("--platform") is { } p && Enum.TryParse<Platform>(p, true, out var plat))
            vm.SelectedPlatform = vm.Platforms.First(o => o.Value == plat);
        if (Arg("--ic") is { } ic && vm.Ics.FirstOrDefault(o => o.Value == ic) is { } ico) vm.SelectedIc = ico;
        if (Arg("--profile") is { } pr && Enum.TryParse<Profile>(pr, true, out var prof) && vm.Profiles.FirstOrDefault(o => o.Value == prof) is { } po) vm.SelectedProfile = po;
        if (Arg("--ranks") is { } rk && vm.RankOptions.FirstOrDefault(o => o.Value.ToString() == rk) is { } ro) vm.SelectedRank = ro;
        if (Arg("--spd-file") is { } sf) vm.LoadDump(sf);
        if (Arg("--apply") is { } ap && int.TryParse(ap, out var api) && vm.Modules.FirstOrDefault()?.Profiles.ElementAtOrDefault(api) is { } pv) vm.ApplyProfile(pv.Module, pv.Profile);
        if (Arg("--mts") is { } m && int.TryParse(m, out var mts) && vm.Frequencies.Contains(mts)) vm.SelectedMts = mts;
        if (int.TryParse(Arg("--page"), out var page)) vm.SelectedPage = page;
        if (Arg("--preset") is { } ps) vm.SelectedPreset = vm.Presets.First(x => x.Value == (ps.Equals("fast", StringComparison.OrdinalIgnoreCase) ? Preset.Fast : Preset.Safe));

        var otherTm5 = vm.FindOtherTm5();   // the user's own TestMem5 copies (Zeyh pack in Downloads...) join the history
        if (capture != null)
        {
            win.ShowActivated = false;
            win.ShowInTaskbar = false;
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = -32000; win.Top = -32000;
            if (int.TryParse(Arg("--w"), out var w)) win.Width = w;
            if (int.TryParse(Arg("--h"), out var h)) win.Height = h;
            win.Show();
            int tab = int.TryParse(Arg("--tab"), out var t) ? t : -1;
            win.Dispatcher.InvokeAsync(async () =>
            {
                await otherTm5;
                if (int.TryParse(Arg("--measure"), out var runs)) for (int i = 0; i < runs; i++) await vm.MeasureLatency();   // real runs, saved to the history
                if (tab >= 0) win.Tabs.SelectedIndex = Math.Min(tab, win.Tabs.Items.Count - 1);
                _ = win.Dispatcher.InvokeAsync(() => { win.CaptureTo(capture, a.Contains("--report"), a.Contains("--config"), double.TryParse(Arg("--scale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sc) ? sc : 1.0); Shutdown(); }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }
        win.Show();
        if (a.Contains("--read")) vm.ReadPcCommand.Execute(null);
    }

    /// <summary>Diagnostic for real hardware: everything the reader sees, in one text file.</summary>
    static void SpdDump(string path)
    {
        var sb = new StringBuilder();
        try
        {
            var cpu = SystemProbe.ReadCpuId();
            var plat = SystemProbe.PlatformOf(cpu);
            sb.AppendLine($"CPU: {cpu.Name}  family 0x{cpu.Family:X} model 0x{cpu.Model:X}  -> {plat}  admin={SystemProbe.IsAdministrator()}  PawnIO={PawnIo.InstalledVersion}");
            var mem = plat is { } pl ? Catalog.Get(pl).Mem : MemGen.DDR4;
            try
            {
                var cur = UmcReader.Read(mem);
                sb.AppendLine($"UMC: channel {cur.Channel}, {cur.MTs} MT/s, {cur.Dimms} DIMM(s), {cur.Ranks} rank");
                sb.AppendLine("  " + string.Join("  ", cur.Values.Select(kv => $"{kv.Key}={kv.Value}")));
                foreach (var l in cur.Log) sb.AppendLine("  log: " + l);
            }
            catch (Exception ex) { sb.AppendLine("UMC error: " + ex.Message); }
            try
            {
                var layout = AodReader.Layout();
                sb.AppendLine($"AOD layout: {layout.Count} fields  " + string.Join(" ", layout.OrderBy(k => k.Value.off).Select(k => $"{k.Key}@{k.Value.off:X2}/{k.Value.size}")));
                if (AodReader.Read() is { } a)
                {
                    sb.AppendLine("AOD functions: " + string.Join(" | ", a.Functions.Select(kv => $"{kv.Value:X8}={kv.Key}")));
                    sb.AppendLine("AOD fields: " + string.Join("  ", a.Fields.OrderBy(k => layout[k.Key].off).Select(kv => $"{kv.Key}={kv.Value}")));
                    sb.AppendLine("AOD raw (Get APCB Config): " + Convert.ToHexString(a.Raw));
                    if (a.RawVoltages != null)
                    {
                        sb.AppendLine("AOD raw (Get memory voltages): " + Convert.ToHexString(a.RawVoltages));
                        string Fv(string n) => layout.TryGetValue(n, out var at) && at.off + at.size <= a.RawVoltages.Length ? (at.size == 2 ? BitConverter.ToUInt16(a.RawVoltages, at.off) : a.RawVoltages[at.off]).ToString() : "?";
                        sb.AppendLine($"  voltages call: VDIO={Fv("VDIO")} VMTT={Fv("VMTT")} VMPP={Fv("VMPP")}");
                    }
                }
                else sb.AppendLine("AOD: not available");
            }
            catch (Exception ex) { sb.AppendLine("AOD error: " + ex); }
            try
            {
                if (SmuMetrics.Read() is { } m) sb.AppendLine($"SMU: {m}");
            }
            catch (Exception ex) { sb.AppendLine("SMU error: " + ex); }
            try
            {
                var mods = SpdReader.ReadAll(mem);
                foreach (var (addr, port, raw) in SpdReader.LastRaw)
                {
                    sb.AppendLine($"SPD 0x{addr:X2} (SMBus port {port}), {raw.Length} bytes:");
                    for (int i = 0; i < raw.Length; i += 32) sb.AppendLine($"  {i:X3}: {Convert.ToHexString(raw, i, Math.Min(32, raw.Length - i))}");
                }
                foreach (var m in mods)
                {
                    sb.AppendLine($"{m.Slot}: {m.ModuleVendor} {m.PartNumber} | {SpdDecoder.Describe(m)} | {m.CapacityGB} GB {m.Ranks}R | IC={m.IcId} certain={m.IcCertain}");
                    foreach (var pr in m.Profiles)
                        sb.AppendLine($"   {pr.Name}: {pr.MTs} MT/s {pr.Label} Vdd={pr.Vdd} | tCL {pr.Timings.tCL:0.000} tRCD {pr.Timings.tRCDRD:0.000} tRP {pr.Timings.tRP:0.000} tRAS {pr.Timings.tRAS:0.000} tRC {pr.Timings.tRC:0.000} tRFC {pr.Timings.tRFC:0.0} tRRDS {pr.Timings.tRRDS:0.000} tRRDL {pr.Timings.tRRDL:0.000} tFAW {pr.Timings.tFAW:0.000}");
                }
                if (mods.Count == 0) sb.AppendLine("SPD: no module found");
            }
            catch (Exception ex) { sb.AppendLine("SPD error: " + ex.Message + " " + ex.InnerException?.Message); }
        }
        catch (Exception ex) { sb.AppendLine("fatal: " + ex); }
        File.WriteAllText(path, sb.ToString());
    }
}
