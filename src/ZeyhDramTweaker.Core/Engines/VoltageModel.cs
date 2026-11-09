using System.Globalization;
using ZeyhDramTweaker.Core.Model;

namespace ZeyhDramTweaker.Core.Engines;

/// <summary>A retail kit with its factory-validated XMP/EXPO profile (what the label and the SPD say).</summary>
public sealed record RefKit(string Family, string Part, int MTs, int CL, int Rcd, int Rp, int Ras, double Volt)
{
    public string Timings => $"{CL}-{Rcd}-{Rp}-{Ras}";
    public double ClNs => CL * 2000.0 / MTs;
}

/// <summary>
/// DRAM voltage needed for a set of timings, fitted to factory XMP/EXPO specs of high-end retail kits
/// (mostly G.Skill Trident Z / Royal / Neo / Z5). The 1.7.3 tables were optimistic here — e.g. B-die 3800
/// CL14 at 1.42 V, which practically never trains; G.Skill bins exactly that at 1.50 V.
///
/// Samsung B-die (11 points, max error ±0.02 V except the extreme-bin 3600 15-15-15 @ 1.35 V):
///   V = 1.326 + 0.086·max(0, 8.9 − tCL ns) + 0.036·max(0, 8.9 − tRCD ns) + 0.057·max(0, (MT/s − 3600)/1000)
/// SK hynix CJR/DJR and Micron Rev. E scale far less with voltage, so their models are flatter and capped.
/// DDR5 (SK hynix, Trident Z5 EXPO): 1.35 V at 10 ns CL (6000 CL30), +0.075 V per ns tighter, +0.02 V up to
/// 6400 and +0.01 V per 400 MT/s in 2:1 territory (6000 CL28 → 1.40, CL26 → 1.45, 6400 CL32 → 1.37,
/// 8000 CL38 → 1.45); never under 1.35 V at 6000+ (no factory kit is).
/// </summary>
public static class VoltageModel
{
    public enum Family { BDie, Hynix, MicronE, OtherDdr4, Ddr5 }

    public static Family FamilyOf(string icId) => icId switch
    {
        "samsung-b" => Family.BDie,
        "hynix-cjr" or "hynix-afr" or "hynix-mfr" => Family.Hynix,
        "micron-e" => Family.MicronE,
        _ when icId.StartsWith("ddr5") => Family.Ddr5,
        _ => Family.OtherDdr4,
    };

    /// <summary>Highest voltage worth running daily (airflow over the sticks assumed above 1.50 V on B-die).</summary>
    public static double DailyCap(Family f) => f switch
    {
        Family.BDie => 1.55,
        Family.Hynix => 1.50,
        Family.MicronE => 1.45,
        Family.Ddr5 => 1.50,
        _ => 1.45,
    };

    /// <summary>Above this the sticks need a fan on them.</summary>
    public static double AirflowFrom(Family f) => f switch { Family.Ddr5 => 1.40, Family.BDie => 1.55, _ => 1.50 };

    public static double? Needed(Family f, int mts, int cl, int rcd)
    {
        double t = 2000.0 / mts, clNs = cl * t, rcdNs = rcd * t, ghz = Math.Max(0, (mts - 3600) / 1000.0);
        return f switch
        {
            Family.BDie => Math.Max(1.35, 1.326 + 0.0858 * Math.Max(0, 8.9 - clNs) + 0.0355 * Math.Max(0, 8.9 - rcdNs) + 0.0571 * ghz),
            // G.Skill Hynix kits: 3600 16-19-19 @1.35, 4000 18-22-22 @1.40; CJR/DJR stop gaining past ~1.45-1.50
            Family.Hynix => Math.Max(1.35, 1.35 + 0.08 * Math.Max(0, 9.0 - clNs) + 0.05 * Math.Max(0, 10.5 - rcdNs) + 0.10 * ghz),
            // Crucial Ballistix (Micron Rev. E): 3600 16-18-18 @1.35, 4000 18-19-19 @1.35; poor voltage scaling
            Family.MicronE => Math.Max(1.35, 1.35 + 0.06 * Math.Max(0, 9.0 - clNs) + 0.04 * Math.Max(0, 10.0 - rcdNs) + 0.05 * ghz),
            // floor: EXPO/XMP kits at 6000+ never ship below 1.35 V whatever the CL (F5-6000J3636 CL36 is 1.35 V)
            Family.Ddr5 => Math.Max(mts >= 6000 ? 1.35 : mts >= 5600 ? 1.30 : 1.25,
                1.35 + 0.075 * (10.0 - clNs) + 0.02 * Math.Clamp((mts - 6000) / 400.0, 0, 1) + 0.01 * Math.Max(0, (mts - 6400) / 400.0)),
            _ => null,
        };
    }

    public static double RoundUp(double v) => Math.Ceiling(v * 100 - 1e-6) / 100;

    /// <summary>
    /// What to actually set. <paramref name="Bin"/> is the voltage a factory-binned kit (the G.Skill XMP) needs for
    /// these timings; ordinary retail sticks are binned lower, so tight timings get a margin on top — up to +0.04 V
    /// once the bin voltage passes ~1.45 V (B-die 3800 CL15-15 → bin 1.46, set 1.50; 3800 CL14 → set 1.53+).
    /// Max is how far it is worth going to stabilise (the daily limit of the IC).
    /// </summary>
    public sealed record Recommendation(double Bin, double Rec, double Max, double Cap);

    public static Recommendation? Recommend(Family f, int mts, int cl, int rcd, double extra = 0)
    {
        if (Needed(f, mts, cl, rcd) is not { } bin) return null;
        bin += extra;
        double floor = f == Family.Ddr5 ? 1.35 : 1.35;
        double tight = Math.Clamp((bin - floor) / 0.10, 0, 1);
        double margin = f switch { Family.BDie => 0.04, Family.Hynix => 0.03, Family.MicronE => 0.02, Family.Ddr5 => 0.03, _ => 0.02 } * tight;
        double cap = DailyCap(f);
        double rec = RoundUp(bin + margin);
        double max = Math.Max(rec, Math.Min(RoundUp(rec + 0.05), cap));
        return new Recommendation(RoundUp(bin), rec, max, cap);
    }

    // ------------------------------------------------------------------ reference kits

    public static readonly IReadOnlyList<RefKit> Kits = new RefKit[]
    {
        // Samsung B-die (G.Skill)
        new("B-die", "F4-3200C14D-16GTZRX", 3200, 14, 14, 14, 34, 1.35),
        new("B-die", "F4-3600C16D-16GTZN", 3600, 16, 16, 16, 36, 1.35),
        new("B-die", "F4-3600C15D-16GTZ", 3600, 15, 15, 15, 35, 1.35),
        new("B-die", "F4-3600C14D-16GTZR", 3600, 14, 15, 15, 35, 1.40),
        new("B-die", "F4-3600C14D-16GTZNB", 3600, 14, 15, 15, 35, 1.45),
        new("B-die", "F4-3800C14D-16GTZN", 3800, 14, 16, 16, 36, 1.50),
        new("B-die", "F4-4000C15D-16GTRS", 4000, 15, 16, 16, 36, 1.50),
        new("B-die", "F4-4000C14D-32GTRS", 4000, 14, 15, 15, 35, 1.55),
        new("B-die", "F4-4400C17-8GVK", 4400, 17, 18, 18, 38, 1.50),
        new("B-die", "F4-4800C18D-16GTRG", 4800, 18, 22, 22, 41, 1.50),
        new("B-die", "F4-4800C17D-16GTRG", 4800, 17, 19, 19, 39, 1.60),
        // SK hynix (G.Skill)
        new("Hynix", "F4-3600C16D-16GTZNC", 3600, 16, 19, 19, 39, 1.35),
        new("Hynix", "F4-4000C18D-32GVK", 4000, 18, 22, 22, 42, 1.40),
        // Micron Rev. E (Crucial Ballistix)
        new("Micron E", "BL2K8G36C16U4B", 3600, 16, 18, 18, 38, 1.35),
        new("Micron E", "BLM2K8G40C18U4B", 4000, 18, 19, 19, 39, 1.35),
        // DDR5 SK hynix (G.Skill Trident Z5 Neo, EXPO)
        new("DDR5", "Trident Z5 Neo 6000 CL26", 6000, 26, 36, 36, 96, 1.45),
        new("DDR5", "Trident Z5 Neo 6000 CL28", 6000, 28, 36, 36, 96, 1.40),
        new("DDR5", "F5-6000J3036F16GX2-TZ5NRW", 6000, 30, 36, 36, 96, 1.35),
        new("DDR5", "F5-6000J3038F16GX2-TZ5N", 6000, 30, 38, 38, 96, 1.35),
        new("DDR5", "F5-6000J3636F16GX2-TZ5RK", 6000, 36, 36, 36, 96, 1.35),
        new("DDR5", "Trident Z5 Neo 6400 CL30", 6400, 30, 39, 39, 102, 1.40),
        new("DDR5", "Trident Z5 Neo 6400 CL32", 6400, 32, 39, 39, 102, 1.40),
    };

    static string FamilyName(Family f) => f switch
    {
        Family.BDie => "B-die", Family.Hynix => "Hynix", Family.MicronE => "Micron E", Family.Ddr5 => "DDR5", _ => "",
    };

    /// <summary>The factory kits closest to a recommendation: same IC family, nearest speed, then nearest CL in ns.</summary>
    public static List<RefKit> Nearest(Family f, int mts, int cl, int take = 4)
    {
        string fam = FamilyName(f);
        double clNs = cl * 2000.0 / mts;
        return Kits.Where(k => k.Family == fam)
            .OrderBy(k => Math.Abs(k.MTs - mts) / 200.0 + Math.Abs(k.ClNs - clNs) * 1.5)
            .Take(take).OrderBy(k => k.MTs).ThenBy(k => k.CL).ToList();
    }

    public static ResultSection? ReferenceSection(Family f, int mts, int cl)
    {
        var kits = Nearest(f, mts, cl);
        if (kits.Count == 0) return null;
        return new ResultSection("sec.reference", new[] { "col.speed", "col.timings", "col.volt" },
            kits.Select(k => new ResultRow(k.Part, new[] { k.MTs.ToString(CultureInfo.InvariantCulture), k.Timings, k.Volt.ToString("0.00", CultureInfo.InvariantCulture) + " V" })).ToList(),
            Provenance.Reference);
    }

    /// <summary>"Only well binned kits run this at X V" is only worth saying when X is clearly below the start voltage.</summary>
    public static string RangeKey(double rec, double bin) => rec - bin >= 0.015 ? "info.vdimm.range" : "info.vdimm.range.nobin";

    // ------------------------------------------------------------------ DDR4 calibration

    /// <summary>Replaces the DRAM voltage of a DDR4 result by the fitted need for its own tCL/tRCD at its speed,
    /// keeps the 1.7.3 value as a hint, and recomputes VTT/Vref/boot. Other ICs: never below the 1.7.3 value.</summary>
    public static void CalibrateDdr4(Dictionary<string, string> f, string icId, int mts, CalcResult res)
    {
        var fam = FamilyOf(icId);
        if (!int.TryParse(f.GetValueOrDefault("tcl"), out var cl) || !int.TryParse(f.GetValueOrDefault("trcdrd"), out var rcd)) return;
        double? orig = double.TryParse(f.GetValueOrDefault("vdramrec"), NumberStyles.Float, CultureInfo.InvariantCulture, out var o) ? o : null;
        var r = Recommend(fam, mts, cl, rcd);
        double bin = r?.Bin ?? orig ?? 1.35, rec = r?.Rec ?? RoundUp(orig ?? 1.35), cap = DailyCap(fam);
        if (fam != Family.BDie && orig is { } o2 && o2 > rec) rec = RoundUp(o2);       // never below the 1.7.3 for the other ICs
        double max = Math.Max(rec, Math.Min(RoundUp(rec + 0.05), cap));
        f["vdramrec"] = rec.ToString("0.00", CultureInfo.InvariantCulture);
        f["vdramalt"] = max.ToString("0.00", CultureInfo.InvariantCulture);
        Ddr4Engine.DeriveDramVoltages(f);
        f["vdrammin"] = Math.Min(bin, rec).ToString("0.00", CultureInfo.InvariantCulture);
        f["__vdimm_range"] = $"{rec.ToString("0.00", CultureInfo.InvariantCulture)}|{max.ToString("0.00", CultureInfo.InvariantCulture)}|{bin.ToString("0.00", CultureInfo.InvariantCulture)}";
        if (orig is { } ov && Math.Abs(ov - rec) >= 0.005) f["__vdimm_orig"] = ov.ToString("0.00#", CultureInfo.InvariantCulture);

        string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        // the "start at X, go up to Y" advice only makes sense while there is headroom under the daily limit
        if (max > rec + 0.004) res.Notices.Add(new Notice(rec >= 1.45 ? NoticeLevel.Warning : NoticeLevel.Info, RangeKey(rec, bin), F(rec), F(max), F(bin)));
        res.Notices.Add(new Notice(NoticeLevel.Info, fam == Family.BDie ? "info.vdimm.model.bdie" : "info.vdimm.model"));
        if (rec > cap + 1e-9) res.Notices.Add(new Notice(NoticeLevel.Warning, "warn.vdimm.overcap", F(rec), F(cap)));
        else if (max > AirflowFrom(fam) + 1e-9) res.Notices.Add(new Notice(NoticeLevel.Warning, "warn.vdimm.airflow", F(AirflowFrom(fam)), F(cap)));
    }
}
