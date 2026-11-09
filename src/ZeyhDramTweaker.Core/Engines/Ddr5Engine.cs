using System.Globalization;
using ZeyhDramTweaker.Core.Model;

namespace ZeyhDramTweaker.Core.Engines;

/// <summary>
/// DDR5 on AM5 (Zen 4 Raphael, Zen 4 Phoenix/Hawk Point APU, Zen 5 Granite Ridge). The 1.7.3 never had
/// DDR5, so this engine is new. It keeps the original's approach — per-IC targets in nanoseconds,
/// converted to clocks for the chosen MT/s (cycles = ns / tCK, tCK = 2000 / MT/s) — and adds the rules
/// DDR5 and AGESA impose:
///   * CL is even only (JEDEC DDR5);
///   * tWR takes the MR6 values 48..96 in steps of 6, and tRTP is paired with it (MR6: 12/14/15/17/18/20/21/23/24);
///   * VSOC is hard-capped at 1.30 V by AGESA since 2023 — never recommend more;
///   * UCLK = MEMCLK (1:1) only up to what the IMC holds (~6400; Phoenix more), 2:1 above;
///   * tRFC fallback = JEDEC minimum for the die density (295 ns 16 Gb, 410 ns 24/32 Gb).
/// IC targets are community starting points (SK hynix data is solid, Samsung/Micron less so — the UI
/// shows the confidence and points to the Manual/EXPO mode for those).
/// </summary>
public static class Ddr5Engine
{
    public enum Confidence { High, Medium, Low }

    /// <summary>Per-IC targets. *Ns values in nanoseconds (Safe, Fast); Vdd at DDR5-6000.</summary>
    public sealed record IcTargets(
        (double Safe, double Fast) ClNs, (double Safe, double Fast) RcdNs, (double Safe, double Fast) RfcNs,
        int FastRrdl, (double Safe, double Fast) Vdd, Confidence Confidence);

    public static IcTargets Targets(MemoryIc ic) => ic.Id switch
    {
        "ddr5-hynix-a16" => new((10.00, 9.33), (12.67, 12.00), (160, 130), 8, (1.35, 1.40), Confidence.High),
        "ddr5-hynix-m16" => new((10.67, 10.00), (13.33, 12.67), (180, 160), 8, (1.35, 1.40), Confidence.High),
        "ddr5-hynix-m24" => new((10.67, 10.00), (13.33, 12.67), (200, 175), 8, (1.35, 1.40), Confidence.Medium),
        "ddr5-hynix-32" => new((10.67, 10.00), (13.33, 12.67), (230, 200), 8, (1.35, 1.40), Confidence.Medium),
        "ddr5-samsung-b16" => new((10.67, 10.00), (13.33, 12.67), (260, 230), 12, (1.35, 1.40), Confidence.Medium),
        "ddr5-samsung-24" => new((11.33, 10.67), (14.00, 13.33), (330, 290), 12, (1.35, 1.40), Confidence.Low),
        "ddr5-micron-16" => new((12.00, 10.67), (14.00, 12.67), (295, 260), 12, (1.35, 1.40), Confidence.Low),
        "ddr5-micron-24" => new((12.00, 11.33), (14.00, 13.33), (350, 310), 12, (1.35, 1.40), Confidence.Low),
        _ => new((12.00, 12.00), (14.67, 14.67), (JedecRfc(ic.DensityGbit), JedecRfc(ic.DensityGbit)), 12, (1.35, 1.35), Confidence.Low),
    };

    public static double JedecRfc(int densityGbit) => densityGbit switch { <= 8 => 195, 16 => 295, _ => 410 };

    /// <summary>JEDEC DDR5 MR6: legal tWR values and the tRTP encoded with each.</summary>
    static readonly (int wr, int rtp)[] Mr6 = { (48, 12), (54, 14), (60, 15), (66, 17), (72, 18), (78, 20), (84, 21), (90, 23), (96, 24) };

    public const double VsocCap = 1.30;
    public static double VddDailyCap => VoltageModel.DailyCap(VoltageModel.Family.Ddr5);

    static int Cyc(double ns, double tck) => (int)Math.Ceiling(ns / tck - 0.02);   // 2% slack: 9.33 ns @6000 → 28, not 29
    static int Even(int v) => v % 2 == 0 ? v : v + 1;
    static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
    static string V(double v) => v.ToString("0.00#", CultureInfo.InvariantCulture);   // 1.35, 1.355 — same as the DDR4 tables

    public static (int sync1to1Safe, int sync1to1Max) SyncLimits(Platform p) => p switch
    {
        Platform.Zen4Apu => (6800, 7200),
        _ => (6200, 6400),
    };

    public static CalcResult Calculate(CalcInput input)
    {
        var plat = Catalog.Get(input.Platform);
        var ic = Catalog.Ic(input.IcId);
        var t = Targets(ic);
        int mts = input.MTs;
        bool fast = input.Preset == Preset.Fast;
        bool dualRank = input.Ranks >= 2;
        bool twoDpc = input.Dimms >= 4;
        bool manual = input.Profile == Profile.Manual;
        var res = new CalcResult { Ok = true, Input = input, EffectiveMTs = mts };

        if (manual && input.Manual is not { IsPrimaryComplete: true })
            return Fail(input, "err.entervalues");
        if (twoDpc && mts > 6000) return Fail(input, "err.ddr5.2dpc", 6000);
        if (mts > 8400 || (plat.Id == Platform.Zen4 && mts > 8000)) return Fail(input, "err.notsupported");

        double tck = 2000.0 / mts;
        var (syncSafe, syncMax) = SyncLimits(plat.Id);
        bool sync = mts <= (fast ? syncMax : syncSafe);
        if (twoDpc || dualRank) sync = mts <= 6000 || (fast && mts <= 6200);

        // ---- primaries
        double clNs = fast ? t.ClNs.Fast : t.ClNs.Safe, rcdNs = fast ? t.RcdNs.Fast : t.RcdNs.Safe;
        double rfcNs = fast ? t.RfcNs.Fast : t.RfcNs.Safe;
        if (dualRank || twoDpc) { clNs += 0.67; rcdNs += 0.67; rfcNs += 10; }   // one CL / tRCD step looser at 6000
        if (manual)
        {
            var x = input.Manual!;
            clNs = fast ? Math.Min(x.tCL, Math.Max(clNs, x.tCL * 0.93)) : x.tCL;
            rcdNs = fast ? Math.Min(x.tRCDRD, Math.Max(rcdNs, x.tRCDRD * 0.95)) : x.tRCDRD;
            rfcNs = fast ? Math.Min(x.tRFC, Math.Max(rfcNs, x.tRFC * 0.80)) : x.tRFC;
        }
        int cl = Even(Cyc(clNs, tck));
        int rcd = Cyc(rcdNs, tck);
        int rp = manual ? Cyc(fast ? Math.Min(input.Manual!.tRP, rcdNs) : input.Manual!.tRP, tck) : rcd;
        int rtp, wr;
        if (fast) (wr, rtp) = Mr6[0];
        else (wr, rtp) = Mr6.FirstOrDefault(m => m.wr >= Cyc(30.0, tck), Mr6[^1]);
        int ras = fast ? 2 * rcd : Math.Max(Cyc(32.0, tck), manual ? Cyc(input.Manual!.tRAS, tck) : 0);
        int rc = ras + rp;
        int rfc = Cyc(rfcNs, tck);
        int rfc2 = (int)Math.Ceiling(rfc * 0.8), rfcsb = (int)Math.Ceiling(rfc * 0.6);

        res.Sections.Add(new ResultSection(Ddr4Layout.Primary, new[] { "col.value" }, new()
        {
            new("tCL", new[] { I(cl) }), new("tRCDRD", new[] { I(rcd) }), new("tRCDWR", new[] { I(rcd) }),
            new("tRP", new[] { I(rp) }), new("tRAS", new[] { I(ras) }), new("tRC", new[] { I(rc) }),
            new("tRFC", new[] { I(rfc) }, "hint.ddr5.rfcns"), new("tRFC2", new[] { I(rfc2) }, "hint.ddr5.rfc2"),
            new("tRFCsb", new[] { I(rfcsb) }, "hint.ddr5.rfc2"),
        }, manual ? Provenance.OriginalFormula : Provenance.New));

        // ---- secondaries
        int rrds = 8, rrdl = fast ? t.FastRrdl : 12, faw = 32;
        int wtrs = fast ? 4 : Math.Max(4, Cyc(2.5, tck)), wtrl = fast ? 16 : Math.Max(16, Cyc(10.0, tck));
        if (manual && input.Manual!.tRRDS > 0) { rrds = Math.Max(8, Cyc(input.Manual.tRRDS, tck)); rrdl = Math.Max(rrdl, fast ? rrdl : Cyc(input.Manual.tRRDL, tck)); faw = Math.Max(32, fast ? 32 : Cyc(input.Manual.tFAW, tck)); }
        string refi = fast ? "50000" : $"Auto (≈{(int)(3900 / tck)})";
        res.Sections.Add(new ResultSection(Ddr4Layout.Secondary, new[] { "col.value" }, new()
        {
            new("tRRDS", new[] { I(rrds) }), new("tRRDL", new[] { I(rrdl) }, ic.Id == "ddr5-hynix-a16" && fast ? "hint.ddr5.adie.rrdl" : null),
            new("tFAW", new[] { I(faw) }), new("tWTRS", new[] { I(wtrs) }), new("tWTRL", new[] { I(wtrl) }),
            new("tWR", new[] { I(wr) }, "hint.ddr5.mr6"), new("tRTP", new[] { I(rtp) }, "hint.ddr5.mr6"),
            new("tCWL", new[] { $"Auto ({cl - 2})" }), new("tREFI", new[] { refi }, fast ? "hint.ddr5.refi" : null),
            new("tCKE", new[] { "Auto" }),
        }, Provenance.New));

        // ---- tertiaries
        res.Sections.Add(new ResultSection(Ddr4Layout.Tertiary, new[] { "col.value" }, new()
        {
            new("tRDRDSCL", new[] { fast ? "4" : "Auto" }), new("tWRWRSCL", new[] { fast ? "4" : "Auto" }),
            new("tRDWR", new[] { fast ? (dualRank ? "18" : "16") : "Auto" }), new("tWRRD", new[] { fast ? (dualRank ? "4" : "2") : "Auto" }),
            new("tRDRDSC", new[] { "1" }), new("tWRWRSC", new[] { "1" }),
            new("tRDRDSD / DD", new[] { dualRank || twoDpc ? (fast ? "8" : "Auto") : "Auto" }),
            new("tWRWRSD / DD", new[] { dualRank || twoDpc ? (fast ? "8" : "Auto") : "Auto" }),
        }, Provenance.New));

        // ---- misc / controller
        int fclk = !fast ? 2000 : plat.Id switch { Platform.Zen5 => 2100, Platform.Zen4Apu => 2200, _ => 2067 };
        bool gdmOff = fast && !dualRank && !twoDpc;
        res.Sections.Add(new ResultSection(Ddr4Layout.Misc, new[] { "col.value" }, new()
        {
            new("UCLK DIV1 MODE", new[] { sync ? "UCLK=MEMCLK (1:1)" : "UCLK=MEMCLK/2 (2:1)" }),
            new("FCLK", new[] { $"{fclk} MHz" }, "hint.ddr5.fclk"),
            new("Gear Down Mode", new[] { gdmOff ? "Disabled" : "Enabled" }),
            new("Command Rate", new[] { "1T" }),
            new("Power Down Mode", new[] { "Disabled" }),
            new("Memory Context Restore", new[] { "mcr" }),
            new("DRAM Refresh Mode", new[] { "Normal" }),
        }, Provenance.New));

        // ---- voltages
        // VDD/VDDQ fitted to G.Skill Trident Z5 EXPO kits for the timings actually recommended (see VoltageModel)
        var vr = VoltageModel.Recommend(VoltageModel.Family.Ddr5, mts, cl, rcd, dualRank || twoDpc ? 0.02 : 0.0)!;
        double vdd = vr.Rec, vddMax = vr.Max;
        double vsoc = sync
            ? mts switch { <= 5600 => 1.100, <= 6000 => 1.200, <= 6200 => 1.250, _ => 1.280 }
            : 1.180;
        if (plat.Id == Platform.Zen4Apu && sync) vsoc = Math.Min(vsoc, mts <= 6400 ? 1.200 : 1.250);
        vsoc = Math.Min(vsoc, VsocCap);
        double vddio = sync ? (mts <= 6000 ? 1.300 : 1.350) : 1.350;
        res.Sections.Add(new ResultSection(Ddr4Layout.Voltages, new[] { "col.min", "col.rec", "col.max" }, new()
        {
            new("VDD (MEM)", new[] { V(Math.Min(vr.Bin, vdd)), V(vdd), V(vddMax) }, $"hint.vdd.range|{V(vr.Bin)}"),
            new("VDDQ (MEM)", new[] { V(Math.Min(vr.Bin, vdd)), V(vdd), V(vddMax) }),
            new("VSOC", new[] { V(vsoc - 0.05), V(vsoc), V(Math.Min(vsoc + 0.03, VsocCap)) }, "hint.ddr5.vsoc"),
            new("CPU VDDIO / MC", new[] { V(vddio - 0.05), V(vddio), V(Math.Min(vddio + 0.05, 1.40)) }),
            new("VDD MISC", new[] { "Auto", fclk >= 2100 ? "1.15" : "Auto", "1.20" }),
            new("CPU VDDP", new[] { "Auto", "Auto", "1.10" }),
            new("VDDG CCD / IOD", new[] { "Auto", fclk >= 2100 ? "1.00" : "Auto", "1.05" }),
        }, Provenance.New));

        // ---- termination (AM5 trains well on Auto; alternatives only for boards that will not)
        res.Sections.Add(new ResultSection(Ddr4Layout.Termination, new[] { "col.rec", "col.alt1", "col.alt2" }, new()
        {
            new("ProcODT", new[] { "Auto", "48 Ω", "40 Ω" }),
            new("ProcDataDrvStren", new[] { "Auto", "40 Ω", "34.3 Ω" }),
            new("DramDataDrvStren", new[] { "Auto", "34 Ω", "40 Ω" }),
            new("RTT_NOM_WR", new[] { "Auto", "Off", "RZQ/7 (34)" }),
            new("RTT_NOM_RD", new[] { "Auto", "Off", "RZQ/7 (34)" }),
            new("RTT_WR", new[] { "Auto", "RZQ/5 (48)", "RZQ/6 (40)" }),
            new("RTT_PARK", new[] { "Auto", "RZQ/5 (48)", "RZQ/4 (60)" }),
            new("DQS_RTT_PARK", new[] { "Auto", "RZQ/5 (48)", "RZQ/6 (40)" }),
        }, Provenance.New));

        // ---- notices
        res.Notices.Add(new Notice(NoticeLevel.Info, "info.ddr5.confidence", $"conf.{t.Confidence.ToString().ToLowerInvariant()}"));
        if (t.Confidence != Confidence.High && !manual) res.Notices.Add(new Notice(NoticeLevel.Info, "info.ddr5.useexpo"));
        if (!sync && mts < 7600) res.Notices.Add(new Notice(NoticeLevel.Warning, "warn.ddr5.badzone", mts));
        else if (!sync) res.Notices.Add(new Notice(NoticeLevel.Info, "info.ddr5.desync"));
        if (sync && mts > syncSafe) res.Notices.Add(new Notice(NoticeLevel.Warning, "warn.ddr5.syncedge", mts));
        if (fast) res.Notices.Add(new Notice(NoticeLevel.Warning, "warn.ddr5.refi"));
        if (vdd > VoltageModel.DailyCap(VoltageModel.Family.Ddr5) + 1e-9) res.Notices.Add(new Notice(NoticeLevel.Warning, "warn.vdimm.overcap", V(vdd), V(VoltageModel.DailyCap(VoltageModel.Family.Ddr5))));
        else if (vddMax > VoltageModel.AirflowFrom(VoltageModel.Family.Ddr5) + 1e-9) res.Notices.Add(new Notice(NoticeLevel.Warning, "warn.ddr5.vdd", V(vddMax)));
        if (vddMax > vdd + 0.004) res.Notices.Add(new Notice(vdd >= 1.40 ? NoticeLevel.Warning : NoticeLevel.Info, VoltageModel.RangeKey(vdd, vr.Bin), vdd.ToString("0.00", CultureInfo.InvariantCulture), vddMax.ToString("0.00", CultureInfo.InvariantCulture), vr.Bin.ToString("0.00", CultureInfo.InvariantCulture)));
        res.Notices.Add(new Notice(NoticeLevel.Info, "info.vdimm.model.ddr5"));
        if (VoltageModel.ReferenceSection(VoltageModel.Family.Ddr5, mts, cl) is { } refs) res.Sections.Add(refs);
        res.Notices.Add(new Notice(NoticeLevel.Info, "info.ddr5.vsoccap"));
        if (dualRank || twoDpc) res.Notices.Add(new Notice(NoticeLevel.Info, "info.ddr5.dr"));
        res.Notices.Add(new Notice(NoticeLevel.Info, "info.test"));
        return res;
    }

    static CalcResult Fail(CalcInput input, string key, params object[] args)
    {
        var r = new CalcResult { Ok = false, Input = input, EffectiveMTs = input.MTs };
        r.Notices.Add(new Notice(NoticeLevel.Error, key, args));
        return r;
    }
}
