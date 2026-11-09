// Differential verification of Zeyh DRAM Tweaker against Ryzen DRAM Calculator 1.7.3.
//
// The sweeps in DRAM-Calc-RE/dados-originais were produced by running the ORIGINAL calculation code
// (decompiled, hardware/dialogs stubbed) over every input combination. This program replays every
// one of those runs through the new engine and requires identical output, then checks invariants of
// the new Zen 3 / DDR5 engines.
//
//   dotnet run --project tests/ZeyhDramTweaker.Verify -- <path to dados-originais>
using System.Globalization;
using System.Text.Json;
using ZeyhDramTweaker.Core.Engines;
using ZeyhDramTweaker.Core.Model;
using ZeyhDramTweaker.Core.Localization;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
string data = args.Length > 0 ? args[0] : @"..\..\..\DRAM-Calc-RE\dados-originais";
int failures = 0, printed = 0;
void Fail(string msg) { failures++; if (printed++ < 8) Console.WriteLine("  FAIL " + msg); }
void Section() => printed = 0;

string Norm(string msg) => string.Join(" || ", msg.Split("||").Select(m => m.Trim()).Where(m => m.Length > 0).Distinct().OrderBy(m => m, StringComparer.Ordinal));

// ------------------------------------------------------------------ 1+2: replay the original runs
foreach (var (dir, manual) in new[] { ("", false), ("manual", true), ("manual-full", true) })
{
    var path = Path.Combine(data, dir);
    if (!File.Exists(Path.Combine(path, "rows.tsv"))) { Console.WriteLine($"skip {dir} (no data)"); continue; }
    var vectors = File.ReadLines(Path.Combine(path, "vectors.jsonl")).Where(l => l.Length > 0)
        .Select(l => JsonSerializer.Deserialize<Dictionary<string, string>>(l)!).ToList();
    int rows = 0, compared = 0, crashed = 0, cjrOther = 0, before = failures; Section();
    foreach (var line in File.ReadLines(Path.Combine(path, "rows.tsv")))
    {
        var p = line.Split('\t');
        if (p[0] is "preset" or "#ns") continue;
        rows++;
        var key = new LegacyKey(p[0], int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]), int.Parse(p[4]), int.Parse(p[5]), int.Parse(p[6]), p[7]);
        ManualTimings? xmp = null;
        if (manual)
        {
            double D(int i) => double.Parse(p[10 + i], CultureInfo.InvariantCulture);
            xmp = new ManualTimings { tCL = D(0), tRCDWR = D(1), tRCDRD = D(2), tRP = D(3), tRAS = D(4), tRC = D(5), tRRDS = D(6), tRRDL = D(7), tFAW = D(8), tRFC = D(9) };
        }
        var orig = vectors[int.Parse(p[8])];
        bool origOk = orig.ContainsKey("tcl");
        string origMsg = Norm(p.Length > 9 ? p[9] : "");
        bool isCrash = origMsg.Contains("EXC");

        var ours = Ddr4Engine.Original(key, xmp);
        // Hynix CJR SAFE manual: the original picks its parameter set from stale state (see Ddr4ManualFormula);
        // accept whichever of the two branches it took.
        if (manual && key.Mt == 6 && key.Preset == "SAFE" && ours.Fields.Count > 0 && !orig.All(kv => ours.Fields.GetValueOrDefault(kv.Key) == kv.Value))
        {
            var other = Ddr4Engine.Original(key, xmp, cjrHighBin: !(xmp!.tRCDRD <= 10.9));
            if (orig.All(kv => other.Fields.GetValueOrDefault(kv.Key) == kv.Value)) { ours = other; cjrOther++; }
        }
        if (ours.Outcome.Supported != origOk || Norm(ours.Outcome.Message) != origMsg)
        {
            Fail($"{dir} outcome {line[..Math.Min(60, line.Length)]}: ours=({ours.Outcome.Supported},{ours.Outcome.Message}) orig=({origOk},{origMsg})");
            continue;
        }
        if (!origOk) continue;
        compared++;
        if (isCrash) crashed++;
        foreach (var (k, v) in orig)
            if (!ours.Fields.TryGetValue(k, out var mine) || mine != v)
                Fail($"{dir} field {k} at {string.Join("|", p.Take(8))}: ours={mine ?? "<none>"} orig={v}");
        if (!isCrash)
            foreach (var k in ours.Fields.Keys)
                if (!orig.ContainsKey(k)) Fail($"{dir} extra field {k} at {string.Join("|", p.Take(8))}");
    }
    Console.WriteLine($"[{(failures == before ? "OK" : "FAIL")}] {(dir == "" ? "A2/B2, A0/B0, Bad bin" : "Manual (" + dir + ")")}: {rows} original runs replayed, {compared} with output compared field by field ({crashed} where 1.7.3 itself crashed{(cjrOther > 0 ? $", {cjrOther} CJR runs on the stale-state branch" : "")})");
}

// ------------------------------------------------------------------ 3: every original field is shown
{
    var shown = Ddr4Layout.AllFields.ToHashSet();
    var all = FactoredTables.Legacy.Groups.Concat(FactoredTables.Manual.Groups).SelectMany(g => g.Fields).Where(f => !f.StartsWith("lbl_")).ToHashSet();
    var missing = all.Where(f => !shown.Contains(f)).ToList();
    Console.WriteLine($"[{(missing.Count == 0 ? "OK" : "FAIL")}] layout shows all {all.Count} original output fields{(missing.Count > 0 ? ": missing " + string.Join(",", missing) : "")}");
    failures += missing.Count;
}

// ------------------------------------------------------------------ 4: Zen 3 sanity over every input
{
    int ok = 0, total = 0, before = failures; Section();
    var rnd = new Random(1);
    foreach (var plat in new[] { Platform.Zen3, Platform.Zen3Apu })
    foreach (var ic in Catalog.Ics.Where(i => i.Mem == MemGen.DDR4))
    foreach (var preset in new[] { Preset.Safe, Preset.Fast })
    foreach (var prof in Catalog.ProfilesFor(MemGen.DDR4))
    foreach (var ranks in new[] { 1, 2 })
    foreach (var dimms in new[] { 1, 2, 4 })
    for (int topo = 0; topo < Catalog.TopologiesFor(Catalog.Get(plat)).Count; topo++)
    foreach (var mts in Catalog.Get(plat).Frequencies)
    {
        total++;
        var xmp = prof == Profile.Manual ? new ManualTimings { tCL = 9 + rnd.NextDouble() * 3, tRCDWR = 9 + rnd.NextDouble() * 4, tRCDRD = 9 + rnd.NextDouble() * 4, tRP = 9 + rnd.NextDouble() * 4, tRAS = 20 + rnd.NextDouble() * 15, tRC = 30 + rnd.NextDouble() * 18, tRRDS = 2.6 + rnd.NextDouble() * 2.5, tRRDL = 5 + rnd.NextDouble() * 2.4, tFAW = 14 + rnd.NextDouble() * 15, tRFC = 160 + rnd.NextDouble() * 380 } : null;
        var r = Ddr4Engine.Calculate(new CalcInput { Platform = plat, IcId = ic.Id, Preset = preset, Profile = prof, Ranks = ranks, Dimms = dimms, TopologyIndex = topo, MTs = mts, Manual = xmp });
        if (!r.Ok) continue;
        ok++;
        double Num(string sec, string row, int col) => double.Parse(r.Value(sec, row, col)!, CultureInfo.InvariantCulture);
        double soc = Num(Ddr4Layout.Voltages, "SOC (VSOC)", 1), iod = Num(Ddr4Layout.Voltages, "VDDG IOD", 1), ccd = Num(Ddr4Layout.Voltages, "VDDG CCD", 1);
        if (iod > soc - 0.04 + 1e-9 || ccd > soc - 0.04 + 1e-9) Fail($"Zen3 VDDG {iod}/{ccd} too close to VSOC {soc} at {plat} {ic.Id} {mts}");
        if (Num(Ddr4Layout.Voltages, "SOC (VSOC)", 2) > 1.30 + 1e-9) Fail($"Zen3 VSOC max > 1.30 at {plat} {mts}");
        var fclk = r.Value(Ddr4Layout.Misc, "FCLK");
        if (plat == Platform.Zen3 && mts <= 4000 && fclk != (mts / 2).ToString()) Fail($"Zen3 FCLK {fclk} at {mts}");
        int cl = int.Parse(r.Value(Ddr4Layout.Primary, "tCL")!);
        if (cl < (prof == Profile.Manual ? 10 : 12) || cl > (prof == Profile.Manual ? 32 : 24)) Fail($"Zen3 tCL {cl} at {plat} {ic.Id} {preset} {prof} {mts}");
    }
    Console.WriteLine($"[{(failures == before ? "OK" : "FAIL")}] Zen 3 / Zen 3 APU: {total} combinations, {ok} supported, invariants hold");
}

// ------------------------------------------------------------------ 5: DDR5 invariants
{
    var mr6 = new Dictionary<int, int> { [48] = 12, [54] = 14, [60] = 15, [66] = 17, [72] = 18, [78] = 20, [84] = 21, [90] = 23, [96] = 24 };
    int ok = 0, total = 0, before = failures; Section();
    foreach (var plat in new[] { Platform.Zen4, Platform.Zen4Apu, Platform.Zen5 })
    foreach (var ic in Catalog.Ics.Where(i => i.Mem == MemGen.DDR5))
    foreach (var preset in new[] { Preset.Safe, Preset.Fast })
    foreach (var prof in Catalog.ProfilesFor(MemGen.DDR5))
    foreach (var ranks in new[] { 1, 2 })
    foreach (var dimms in new[] { 2, 4 })
    foreach (var mts in Catalog.Ddr5Axis)
    {
        total++;
        var xmp = prof == Profile.Manual ? new ManualTimings { tCL = 10, tRCDWR = 12.5, tRCDRD = 12.5, tRP = 12.5, tRAS = 32, tRC = 44.5, tRRDS = 2.7, tRRDL = 5, tFAW = 10.7, tRFC = 295 } : null;
        var r = Ddr5Engine.Calculate(new CalcInput { Platform = plat, IcId = ic.Id, Preset = preset, Profile = prof, Ranks = ranks, Dimms = dimms, MTs = mts, Manual = xmp });
        if (!r.Ok) continue;
        ok++;
        int I(string sec, string row) => int.Parse(r.Value(sec, row)!);
        double D(string sec, string row, int col) => double.Parse(r.Value(sec, row, col)!, CultureInfo.InvariantCulture);
        string where = $"{plat} {ic.Id} {preset} {prof} {ranks}R {dimms}D {mts}";
        int cl = I(Ddr4Layout.Primary, "tCL"), wr = I(Ddr4Layout.Secondary, "tWR"), rtp = I(Ddr4Layout.Secondary, "tRTP");
        if (cl % 2 != 0) Fail($"DDR5 odd CL {cl} {where}");
        if (!mr6.TryGetValue(wr, out var pair) || pair != rtp) Fail($"DDR5 tWR/tRTP {wr}/{rtp} not an MR6 pair {where}");
        if (I(Ddr4Layout.Primary, "tRC") != I(Ddr4Layout.Primary, "tRAS") + I(Ddr4Layout.Primary, "tRP")) Fail($"DDR5 tRC != tRAS+tRP {where}");
        if (D(Ddr4Layout.Voltages, "VSOC", 2) > Ddr5Engine.VsocCap + 1e-9) Fail($"DDR5 VSOC above AGESA cap {where}");
        if (D(Ddr4Layout.Voltages, "VDD (MEM)", 2) > Ddr5Engine.VddDailyCap + 1e-9) Fail($"DDR5 VDD above daily cap {where}");
        double tck = 2000.0 / mts;
        if (cl * tck < 8.5) Fail($"DDR5 CL {cl} = {cl * tck:F2} ns too tight {where}");
        if (I(Ddr4Layout.Primary, "tRCDRD") * tck < 11.0) Fail($"DDR5 tRCD too tight {where}");
        if (dimms == 4 && mts > 6000) Fail($"DDR5 2DPC above 6000 accepted {where}");
    }
    Console.WriteLine($"[{(failures == before ? "OK" : "FAIL")}] DDR5 (Zen 4 / Zen 4 APU / Zen 5): {total} combinations, {ok} supported, invariants hold");
}


// ------------------------------------------------------------------ 6: every key the engines emit is translated
{
    int before = failures; Section();
    var keys = new HashSet<string>();
    var rnd = new Random(3);
    foreach (var plat in Catalog.Platforms)
    {
        keys.Add(plat.NameKey);
        foreach (var t in Catalog.TopologiesFor(plat)) keys.Add(t.NameKey);
        foreach (var prof in Catalog.ProfilesFor(plat.Mem)) keys.Add("prof." + prof);
        foreach (var ic in Catalog.Ics.Where(i => i.Mem == plat.Mem))
        foreach (var preset in new[] { Preset.Safe, Preset.Fast })
        foreach (var prof in Catalog.ProfilesFor(plat.Mem))
        foreach (var mts in plat.Frequencies)
        {
            var xmp = prof == Profile.Manual ? new ManualTimings { tCL = 9.5, tRCDWR = 11, tRCDRD = 11, tRP = 11, tRAS = 28, tRC = 42, tRRDS = 3.3, tRRDL = 5.5, tFAW = 21, tRFC = 300 } : null;
            var r = Calculator.Calculate(new CalcInput { Platform = plat.Id, IcId = ic.Id, Preset = preset, Profile = prof, Ranks = 1 + rnd.Next(2), Dimms = 2, MTs = mts, Manual = xmp });
            foreach (var n in r.Notices) { keys.Add(n.Key); foreach (var a in n.Args.OfType<string>().Where(a => System.Text.RegularExpressions.Regex.IsMatch(a, @"^[a-z]+\.[a-z0-9.]+$"))) keys.Add(a); }
            foreach (var sec in r.Sections) { keys.Add(sec.TitleKey); keys.Add("prov." + sec.Provenance); foreach (var c in sec.ColumnKeys) keys.Add(c); foreach (var row in sec.Rows) if (row.HintKey != null) keys.Add(row.HintKey.Split('|')[0]); }
            _ = ZeyhDramTweaker.Core.Export.TextExport.Build(r);
        }
    }
    foreach (var lang in new[] { "pt", "en" })
    {
        ZeyhDramTweaker.Core.Localization.Loc.Instance.Language = lang;
        foreach (var k in keys) if (ZeyhDramTweaker.Core.Localization.Loc.Instance[k] == k) Fail($"missing {lang} string '{k}'");
    }
    Console.WriteLine($"[{(failures == before ? "OK" : "FAIL")}] localization: {keys.Count} keys emitted by the engines, all present in pt and en; text export runs for every case");
}

// ------------------------------------------------------------------ 7: Thaiphoon import
{
    int before = failures; Section();
    const string html = "<html><body><table><tr><td>Module Manufacturer:</td><td class=vcell>G.Skill</td></tr><tr><td>Module Part Number:</td><td class=vcell>F4-3600C16-8GTZNC</td></tr><tr><td>DRAM Components:</td><td class=vcell>Samsung K4A8G085WB-BCPB</td></tr><tr><td>Ranks:</td><td>1</td></tr>"
        + "<tr><td>Minimum CAS Latency Time (tAA):</td><td>13.750 ns</td></tr><tr><td>Minimum RAS to CAS Delay Time (tRCD):</td><td>13.750 ns</td></tr><tr><td>(tRP):</td><td>13.750 ns</td></tr>"
        + "<tr><td>(tRAS):</td><td>32.000 ns</td></tr><tr><td>(tRC):</td><td>45.750 ns</td></tr><tr><td>(tRRD_S):</td><td>5.300 ns</td></tr><tr><td>(tRRD_L):</td><td>6.400 ns</td></tr><tr><td>(tFAW):</td><td>21.000 ns</td></tr><tr><td>(tRFC1):</td><td>350.000 ns</td></tr>"
        + "<tr><td>XMP (tAA):</td><td>8.889 ns</td></tr><tr><td>XMP (tRCD):</td><td>8.889 ns</td></tr><tr><td>(tRP):</td><td>8.889 ns</td></tr><tr><td>(tRAS):</td><td>20.000 ns</td></tr><tr><td>(tRC):</td><td>28.889 ns</td></tr><tr><td>(tRRD_S):</td><td>3.333 ns</td></tr><tr><td>(tRRD_L):</td><td>5.000 ns</td></tr><tr><td>(tFAW):</td><td>20.000 ns</td></tr><tr><td>(tRFC1):</td><td>350.000 ns</td></tr><tr><td>DDR4 SDRAM</td></tr></table></body></html>";
    var rep = ZeyhDramTweaker.Core.Import.ThaiphoonImport.Parse(html);
    if (rep.Timings.tCL != 8.889 || rep.Timings.tRCDRD != 8.889 || rep.Timings.tRFC != 350 || rep.Timings.tRRDS != 3.333) Fail($"thaiphoon timings {rep.Timings}");
    if (rep.Ranks != 1 || rep.SuggestedIcId != "samsung-b" || rep.PartNumber != "F4-3600C16-8GTZNC") Fail($"thaiphoon meta {rep.Ranks} {rep.SuggestedIcId} {rep.PartNumber}");
    try { ZeyhDramTweaker.Core.Import.ThaiphoonImport.Parse("<a>Show delays in nanoseconds</a>"); Fail("thaiphoon: ns warning not raised"); } catch (ZeyhDramTweaker.Core.Import.ThaiphoonImport.ImportException) { }
    foreach (var (part, want) in new[] { ("SK Hynix H5AN8G8NCJR-VKC", "hynix-cjr"), ("Micron MT40A1G8SA-075:E", "micron-e"), ("SK Hynix H5CG48AGBDX018N", "ddr5-hynix-a16"), ("SK Hynix H5CG48MEBDX014", "ddr5-hynix-m16"), ("SK Hynix H5CGD8MEBDX021", "ddr5-hynix-m24"), ("Samsung K4RAH086VB-BCQK", "ddr5-samsung-b16") })
        if (ZeyhDramTweaker.Core.Import.ThaiphoonImport.SuggestIc(part, null) != want) Fail($"SuggestIc({part}) != {want}");
    // manual result from the imported XMP equals the original formula path
    var res = Calculator.Calculate(new CalcInput { Platform = Platform.Zen2, IcId = "samsung-b", Preset = Preset.Safe, Profile = Profile.Manual, Ranks = 1, Dimms = 2, MTs = 3600, Manual = rep.Timings });
    // 8.889 ns @3600 = 16.0 clocks, minus the B-die SAFE offset 0.2 → 15 → bumped to 14 by the original (same as its A2/B2 table)
    if (!res.Ok || res.Value(Ddr4Layout.Primary, "tCL") != "14" || res.Value(Ddr4Layout.Primary, "tRCDRD") != "16") Fail($"manual from import: ok={res.Ok} tCL={res.Value(Ddr4Layout.Primary, "tCL")} tRCDRD={res.Value(Ddr4Layout.Primary, "tRCDRD")}");
    Console.WriteLine($"[{(failures == before ? "OK" : "FAIL")}] Thaiphoon import: XMP block picked over JEDEC, metadata and IC detection, Manual result");
}

// ------------------------------------------------------------------ 8: SPD decoding (synthetic images built from the specs)
{
    int before = failures; Section();
    // DDR4 B-die 8Gb x8 single rank, XMP 3600 16-16-16-36 1.35 V, JEDEC 2133 15-15-15
    var d4 = new byte[512];
    d4[2] = 0x0C; d4[4] = 0x45; d4[12] = 0x01; d4[13] = 0x03;
    void Mtb(byte[] b, int at, int fineAt, double ns) { int m = (int)Math.Ceiling(ns / 0.125); b[at] = (byte)m; b[fineAt] = unchecked((byte)(sbyte)Math.Round((ns - m * 0.125) * 1000)); }
    Mtb(d4, 18, 125, 0.938); Mtb(d4, 24, 123, 14.06); Mtb(d4, 25, 122, 14.06); Mtb(d4, 26, 121, 14.06);
    d4[27] = 0x11; d4[28] = (byte)(264 & 0xFF); d4[29] = (byte)(377 & 0xFF);   // tRAS 33 ns (264 MTB), tRC 47.125 ns (377)
    d4[30] = (byte)(2800 & 0xFF); d4[31] = (byte)(2800 >> 8);                    // tRFC1 350 ns
    d4[36] = 0x00; d4[37] = 168; Mtb(d4, 38, 119, 3.7); Mtb(d4, 39, 118, 5.3);   // tFAW 21 ns
    d4[320] = 0x04; d4[321] = 0xCD; System.Text.Encoding.ASCII.GetBytes("F4-3600C16-8GTZNC   ").CopyTo(d4, 329);
    d4[323] = 0x20; d4[350] = 0x80; d4[351] = 0xCE;                               // Samsung, 2020
    d4[384] = 0x0C; d4[385] = 0x4A; d4[386] = 0x01; d4[387] = 0x20;
    int o = 393; d4[o] = 0x80 | 35;                                               // 1.35 V
    Mtb(d4, o + 3, o + 38, 0.555); Mtb(d4, o + 8, o + 37, 8.888); Mtb(d4, o + 9, o + 36, 8.888); Mtb(d4, o + 10, o + 35, 8.888);
    d4[o + 11] = 0x00; d4[o + 12] = 160; d4[o + 13] = 232;                        // tRAS 20 ns, tRC 29 ns
    d4[o + 14] = (byte)(2800 & 0xFF); d4[o + 15] = (byte)(2800 >> 8);
    d4[o + 21] = 160; Mtb(d4, o + 22, o + 33, 3.333); Mtb(d4, o + 23, o + 32, 5.0);
    var m4 = ZeyhDramTweaker.Core.Hardware.SpdDecoder.DecodeDdr4(d4, 0x50)!;
    var x4 = m4.Profiles.First();
    if (x4.Name != "XMP 1" || x4.MTs != 3600 || x4.Label != "16-16-16-36" || Math.Abs(x4.Vdd!.Value - 1.35) > 1e-9) Fail($"DDR4 XMP decode {x4.Name} {x4.MTs} {x4.Label} {x4.Vdd}");
    if (Math.Abs(x4.Timings.tCL - 8.888) > 0.002 || Math.Abs(x4.Timings.tRFC - 350) > 1e-6 || Math.Abs(x4.Timings.tRRDS - 3.333) > 0.002 || Math.Abs(x4.Timings.tFAW - 20) > 1e-6) Fail($"DDR4 XMP ns {x4.Timings}");
    if (m4.CapacityGB != 8 || m4.Ranks != 1 || m4.DramVendor != "Samsung" || m4.IcId != "samsung-b" || m4.PartNumber != "F4-3600C16-8GTZNC") Fail($"DDR4 module {m4}");
    var j4 = m4.Profiles.Last();
    if (j4.Name != "JEDEC" || j4.MTs != 2133 || j4.Label != "15-15-15-36") Fail($"DDR4 JEDEC {j4.MTs} {j4.Label}");

    // DDR5 SK hynix 16Gb A-die, EXPO 6000 30-36-36-76 1.35 V, JEDEC 4800 40-39-39
    var d5 = new byte[1024];
    d5[2] = 0x12; d5[4] = 0x04; d5[6] = 0x20; d5[234] = 0x00; d5[235] = 0x22;
    void U16(byte[] b, int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); }
    U16(d5, 20, 416); U16(d5, 30, 16640); U16(d5, 32, 16250); U16(d5, 34, 16250); U16(d5, 36, 32000); U16(d5, 38, 48250); U16(d5, 42, 295);
    d5[552] = 0x80; d5[553] = 0xAD; d5[554] = (byte)'A';
    System.Text.Encoding.ASCII.GetBytes("EXPO").CopyTo(d5, 832); d5[842] = 50;   // 1.10 + 50 × 5 mV = 1.35 V
    U16(d5, 846, 333); U16(d5, 848, 10000); U16(d5, 850, 12000); U16(d5, 852, 12000); U16(d5, 854, 25333); U16(d5, 856, 37333); U16(d5, 860, 160);
    var m5 = ZeyhDramTweaker.Core.Hardware.SpdDecoder.DecodeDdr5(d5, 0x51)!;
    var e5 = m5.Profiles.First();
    if (e5.Name != "EXPO 1" || e5.MTs != 6000 || e5.Label != "30-36-36-76" || Math.Abs(e5.Vdd!.Value - 1.35) > 1e-9) Fail($"DDR5 EXPO {e5.Name} {e5.MTs} {e5.Label} {e5.Vdd}");
    if (m5.IcId != "ddr5-hynix-a16" || !m5.IcCertain || m5.CapacityGB != 16 || m5.DieText != "A-die") Fail($"DDR5 module {m5.IcId} {m5.CapacityGB} {m5.DieText}");
    if (m5.Profiles.Last() is var j5 && (j5.Name != "JEDEC" || j5.MTs != 4800)) Fail($"DDR5 JEDEC {j5.Name} {j5.MTs}");
    // the EXPO profile feeds the DDR5 Manual mode
    var r5 = Calculator.Calculate(new CalcInput { Platform = Platform.Zen5, IcId = m5.IcId!, Preset = Preset.Safe, Profile = Profile.Manual, Ranks = 1, Dimms = 2, MTs = e5.MTs, Manual = e5.Timings });
    if (!r5.Ok || r5.Value(Ddr4Layout.Primary, "tCL") != "30" || r5.Value(Ddr4Layout.Primary, "tRCDRD") != "36") Fail($"DDR5 manual from EXPO ok={r5.Ok} tCL={r5.Value(Ddr4Layout.Primary, "tCL")} tRCD={r5.Value(Ddr4Layout.Primary, "tRCDRD")}");
    // real kit read on the dev PC: G.Skill F4-4400C17-8GVK, XMP 4400 17-18-18-38 (tRCD 8.077 ns) -> B-die
    var gvk = new List<ZeyhDramTweaker.Core.Hardware.SpdProfile> { new("XMP 1", 4400, 1.5, new ManualTimings { tCL = 7.532, tRCDRD = 8.077, tRCDWR = 8.077, tRP = 8.077, tRAS = 17.25, tRC = 25.326, tRFC = 350, tRRDS = 1.683, tRRDL = 4.849, tFAW = 24 }) };
    if (ZeyhDramTweaker.Core.Hardware.SpdDecoder.IdentifyDdr4("Samsung", 8, 8, null, 0x19, gvk).ic != "samsung-b") Fail("F4-4400C17 not identified as B-die");
    var de = new List<ZeyhDramTweaker.Core.Hardware.SpdProfile> { new("XMP 1", 3600, 1.35, new ManualTimings { tCL = 8.889, tRCDRD = 10.556, tRCDWR = 10.556, tRP = 10.556, tRAS = 21.1, tRC = 31.7, tRFC = 350, tRRDS = 2.5, tRRDL = 5, tFAW = 20 }) };
    if (ZeyhDramTweaker.Core.Hardware.SpdDecoder.IdentifyDdr4("Samsung", 8, 8, null, 0x20, de).ic != "samsung-de") Fail("3600 16-19-19 Samsung not identified as D/E-die");
    Console.WriteLine($"[{(failures == before ? "OK" : "FAIL")}] SPD decoding: DDR4 JEDEC + XMP 2.0, DDR5 JEDEC + EXPO, die/IC identification, profile into Manual mode");
}

// ------------------------------------------------------------------ 9: voltage model vs factory kits
{
    int before = failures; Section();
    double worst = 0; string worstKit = "";
    foreach (var k in VoltageModel.Kits)
    {
        var fam = k.Family switch { "B-die" => VoltageModel.Family.BDie, "Hynix" => VoltageModel.Family.Hynix, "Micron E" => VoltageModel.Family.MicronE, _ => VoltageModel.Family.Ddr5 };
        double v = VoltageModel.Needed(fam, k.MTs, k.CL, k.Rcd)!.Value;
        double err = v - k.Volt;
        // the model may ask a bit more than a factory bin (binned sticks), never clearly less
        if (err < -0.05 || err > 0.06) Fail($"voltage model {k.Part}: model {v:0.000} vs kit {k.Volt:0.00}");
        if (Math.Abs(err) > Math.Abs(worst)) { worst = err; worstKit = k.Part; }
    }
    // every DDR4 recommendation now carries the fitted VDIMM; B-die never above 1.60 shown as max
    foreach (var plat in new[] { Platform.Zen2, Platform.Zen3 })
    foreach (var preset in new[] { Preset.Safe, Preset.Fast })
    foreach (var prof in new[] { Profile.A2B2, Profile.A0B0, Profile.BadBin })
    foreach (var mts in Catalog.Ddr4Axis.Where(m => m <= 4000))
    {
        var r = Calculator.Calculate(new CalcInput { Platform = plat, IcId = "samsung-b", Preset = preset, Profile = prof, Ranks = 1, Dimms = 2, MTs = mts });
        if (!r.Ok) continue;
        int cl = int.Parse(r.Value(Ddr4Layout.Primary, "tCL")!), rcd = int.Parse(r.Value(Ddr4Layout.Primary, "tRCDRD")!);
        double rec = double.Parse(r.Value(Ddr4Layout.Voltages, "DRAM (VDIMM)", 1)!, CultureInfo.InvariantCulture);
        double want = VoltageModel.Recommend(VoltageModel.Family.BDie, mts, cl, rcd)!.Rec;
        if (Math.Abs(rec - want) > 1e-9) Fail($"B-die VDIMM {rec} != model {want} at {plat} {preset} {prof} {mts}");
        if (r.Section("sec.reference") == null) Fail($"no reference kits at {plat} {preset} {mts}");
    }
    // user's field data: 3800 CL15-15 must ask 1.50 V, 3800 CL14 at least 1.52 V, nothing above the 1.55 V cap as "rec" without a warning
    if (VoltageModel.Recommend(VoltageModel.Family.BDie, 3800, 15, 15)!.Rec != 1.50) Fail($"3800 CL15-15 rec {VoltageModel.Recommend(VoltageModel.Family.BDie, 3800, 15, 15)!.Rec}");
    if (VoltageModel.Recommend(VoltageModel.Family.BDie, 3800, 14, 16)!.Rec < 1.52) Fail("3800 CL14-16 under 1.52 V");
    if (VoltageModel.Recommend(VoltageModel.Family.BDie, 3600, 16, 16)!.Rec != 1.35) Fail("3600 CL16-16 should stay 1.35 V");
    Console.WriteLine($"[{(failures == before ? "OK" : "FAIL")}] voltage model: {VoltageModel.Kits.Count} factory kits within tolerance (worst {worst:+0.000;-0.000} V on {worstKit}); DDR4 B-die VDIMM follows it everywhere");
}

// ------------------------------------------------------------------ 10: TestMem5 log (format copied from a real TM5 0.13.1 Log.txt)
{
    int before = failures; Section();
    const string log = """
        -------- TestMem5 0.13.1 2026-06-12 18:18 --------
        00:00:00  Configuration: Extreme @ anta777
        00:00:00  Testing 1088 MB x 12 (80%) out of 16293 MB
        00:00:31  Error in test 4
        00:00:31  Testing stopped

        -------- TestMem5 0.13.1 2026-06-17 8:09 --------
        00:00:00  Configuration: Extreme @ anta777
        00:00:00  Testing 1072 MB x 12 (78%) out of 16293 MB
        01:38:26  Testing completed

        -------- TestMem5 0.13.1 2026-06-19 9:15 --------
        00:00:00  Configuration: Extreme @ anta777
        00:00:00  Testing 1056 MB x 12 (77%) out of 16293 MB
        00:03:49  Testing stopped by user

        -------- TestMem5 0.13.1 2026-11-05 14:07 --------
        00:00:00  Configuration: DDR5 Ryzen3D @ anta777
        00:00:00  Testing 624 MB x 16 (61%) out of 16295 MB
        """;
    var s = ZeyhDramTweaker.Core.Test.Tm5Service.Parse(log);
    if (s.Count != 4) Fail($"tm5: {s.Count} sessions, expected 4");
    else
    {
        // newest first
        if (s[0].Outcome != ZeyhDramTweaker.Core.Test.Tm5Outcome.Running || s[0].Config != "DDR5 Ryzen3D @ anta777") Fail($"tm5 running: {s[0]}");
        if (s[1].Outcome != ZeyhDramTweaker.Core.Test.Tm5Outcome.Stopped || s[1].Started != new DateTime(2026, 6, 19, 9, 15, 0)) Fail($"tm5 stopped: {s[1]}");
        if (s[2].Outcome != ZeyhDramTweaker.Core.Test.Tm5Outcome.Passed || s[2].Duration != new TimeSpan(1, 38, 26) || s[2].Started.Hour != 8) Fail($"tm5 passed: {s[2]}");
        if (s[3].Outcome != ZeyhDramTweaker.Core.Test.Tm5Outcome.Errors || s[3].Errors.Count != 1 || !s[3].Size.StartsWith("1088 MB")) Fail($"tm5 errors: {s[3]}");
    }
    Console.WriteLine($"[{(failures == before ? "OK" : "FAIL")}] TestMem5 log: passed / errors / stopped / running sessions and single-digit hours parsed");
}

// ------------------------------------------------------------------ 11: AOD layout from this machine's SSDT (skipped where there is none)
{
    int before = failures; Section();
    var layout = ZeyhDramTweaker.Core.Hardware.AodReader.Layout();
    if (layout.Count == 0) Console.WriteLine("skip AOD layout (no AMD AOD SSDT on this machine)");
    else
    {
        foreach (var n in new[] { "VDIO", "VMTT", "PODT", "RTNM", "RTWR", "RTPK", "ACSP", "CBCK", "CDVP", "FCLK" })
            if (!layout.ContainsKey(n)) Fail($"AOD layout lacks {n}");
        if (layout.TryGetValue("VDIO", out var vio) && vio != (0x1B, 2)) Fail($"VDIO at {vio}");
        if (layout.TryGetValue("PODT", out var odt) && odt != (0x21, 2)) Fail($"PODT at {odt}");
        foreach (var (code, ohm) in new[] { (56L, "40"), (59L, "32"), (25L, "53.3"), (63L, "28.2") })
            if (ZeyhDramTweaker.Core.Hardware.LiveExtras.ProcOdt(code) != ohm) Fail($"ProcODT {code}");
        Console.WriteLine($"[{(failures == before ? "OK" : "FAIL")}] AOD layout parsed from the BIOS SSDT: {layout.Count} fields (VDDIO @0x{layout.GetValueOrDefault("VDIO").off:X2})");
    }
}

// ------------------------------------------------------------------ 12: display rules from the QA pass
{
    Section();
    int before = failures;
    // the "only well binned kits run this at X V" sentence only when X is clearly below the start voltage
    if (VoltageModel.RangeKey(1.39, 1.38) != "info.vdimm.range.nobin") Fail("bin 0.01 V under rec should drop the bin sentence");
    if (VoltageModel.RangeKey(1.35, 1.35) != "info.vdimm.range.nobin") Fail("bin == rec should drop the bin sentence");
    if (VoltageModel.RangeKey(1.50, 1.45) != "info.vdimm.range") Fail("bin 0.05 V under rec should keep the bin sentence");
    // one spelling for termination values, whatever wrote them
    foreach (var (raw, shown) in new[] { ("(48)RZQ/5", "RZQ/5 (48)"), ("(34)RZQ/7", "RZQ/7 (34)"), ("RZQ/5 (48)", "RZQ/5 (48)"), ("OFF", "Off"), ("Off", "Off"), ("Hi-Z", "Hi-Z"), ("36.9", "36.9"), ("1.150", "1.15"), ("1.125", "1.125"), ("1.000", "1.00"), ("34.30", "34.30") })
        if (Loc.Display(raw) != shown) Fail($"Display({raw}) = {Loc.Display(raw)}");
    // no PT/EN text mixes a decimal comma into voltages, and DDR5 voltages use the DDR4 two-decimal style
    foreach (var lang in new[] { "pt", "en" })
    {
        Loc.Instance.Language = lang;
        var r5 = Ddr5Engine.Calculate(new CalcInput { Platform = Platform.Zen5, IcId = "ddr5-hynix-a16", Preset = Preset.Fast, Profile = Profile.Standard, Ranks = 1, Dimms = 2, MTs = 8000 });
        foreach (var n in r5.Notices)
            if (System.Text.RegularExpressions.Regex.IsMatch(Loc.Instance.Notice(n), @"\d,\d+ V")) Fail($"{lang} decimal comma: {Loc.Instance.Notice(n)}");
        if (r5.Value(Ddr4Layout.Voltages, "VDD (MEM)", 1) is { } v5 && System.Text.RegularExpressions.Regex.IsMatch(v5, @"^\d\.\d{2}0$")) Fail($"DDR5 VDD shown as {v5}");
    }
    Loc.Instance.Language = "pt";
    if (ZeyhDramTweaker.Core.Test.Tm5Service.ShortName(@"C:\Users\x\Downloads\Zeyh Tweaks\PROGRAMAS\TestMem5") != @"…\PROGRAMAS\TestMem5") Fail("TM5 short folder name");
    Console.WriteLine($"[{(failures == before ? "OK" : "FAIL")}] display rules: bin sentence only with a real gap, one RTT spelling, dot decimals, TM5 folder names");
}

// ------------------------------------------------------------------ 13: memory latency (real hardware, short run)
{
    Section();
    int before = failures;
    var a = ZeyhDramTweaker.Core.Test.LatencyTest.Run("verify", sets: 5, passScale: 0.3);
    var b = ZeyhDramTweaker.Core.Test.LatencyTest.Run("verify", sets: 5, passScale: 0.3);
    if (a.Ns is < 25 or > 250) Fail($"latency {a.Ns:0.0} ns outside any DRAM");
    if (Math.Abs(a.Ns - b.Ns) > 3) Fail($"latency not repeatable: {a.Ns:0.0} vs {b.Ns:0.0} ns");
    // reusing open rows can only help: block-random below full random, but not by a prefetcher-sized gap
    if (a.FullRandomNs < a.Ns || a.FullRandomNs - a.Ns > 15) Fail($"block {a.Ns:0.0} vs full random {a.FullRandomNs:0.0} ns");
    Console.WriteLine($"[{(failures == before ? "OK" : "FAIL")}] memory latency: block {a.Ns:0.0} / {b.Ns:0.0} ns, full random {a.FullRandomNs:0.0} ns on this machine");
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
