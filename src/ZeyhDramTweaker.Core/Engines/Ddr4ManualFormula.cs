using System.Globalization;
using ZeyhDramTweaker.Core.Model;

namespace ZeyhDramTweaker.Core.Engines;

/// <summary>
/// The "Manual" profile of 1.7.3: XMP nanoseconds (from Thaiphoon) → DDR4 clock counts.
/// Re-implemented from the reverse-engineered behaviour; every branch below was checked against
/// the original over ~200k randomized XMP inputs (tests/ZeyhDramTweaker.Verify).
///
/// cycles = (int)(ns / tCK - offset) with tCK = 2000 / MT/s, plus the original's rounding quirks
/// (odd CL bumped to the next even value, tRAS/tRC forced even, tRRD clamps, tWR/tWTRL pairing).
/// Only the fields that depend on the XMP values live here; everything else (voltage tables,
/// termination, CAD bus...) comes from the extracted tables.
/// </summary>
public static class Ddr4ManualFormula
{
    /// <summary>Debug_profile parameters per (preset, IC). Offsets are subtracted from ns/tCK.</summary>
    sealed record DpParams(bool Safe, double CL, double RCDWR, double RCDRD, double RP, bool RasFromRcd, bool RcFromRcd,
        double RRDS, double RRDL, bool Faw4x, double RdScl, double WrScl, int Rfc, bool Cke, bool Gdm);

    /// <remarks>Hynix CJR has two parameter sets. 1.7.3 meant to pick the first for kits whose XMP tRCD is
    /// ≤ 10.9 ns, but it tests the field before reading the XMP values, i.e. it looks at whatever the
    /// previous calculation left there (almost always a clock count &gt; 10.9, so the second set). We use
    /// the intended XMP test; <paramref name="cjrHighBin"/> forces a branch (verifier).</remarks>
    static DpParams? Params(Preset preset, int mt, double tRCDRDns, bool? cjrHighBin = null) => (preset, mt) switch
    {
        (Preset.Safe, 0) => new(true, 0.2, 0.2, 0.0, 0.2, true, true, 0.6, 1.3, true, 2.15, 2.15, 195, false, true),
        (Preset.Safe, 1) => new(true, 0.9, 0.9, 0.4, 0.4, true, false, 0.0, 0.5, true, 3.0, 3.0, 360, true, true),
        (Preset.Safe, 4) => new(true, 0.9, 0.9, 0.4, 0.4, true, false, -0.5, 0.5, false, 3.4, 3.4, 300, true, true),
        (Preset.Safe, 5) => new(true, 0.9, 0.9, 0.4, 0.4, true, false, -0.9, 0.0, false, 3.0, 3.0, 300, true, true),
        (Preset.Safe, 6) when cjrHighBin ?? tRCDRDns <= 10.9 => new(true, 0.9, 0.9, -0.5, -1.0, true, true, -0.8, 0.5, true, 3.0, 3.0, 262, true, true),
        (Preset.Safe, 6) => new(true, 0.9, 0.9, 0.4, -0.1, true, true, 0.0, 0.5, true, 3.0, 3.0, 262, true, true),
        (Preset.Safe, 7) => new(true, 0.5, 0.5, 0.5, 0.5, true, false, 0.0, 0.5, true, 3.0, 3.0, 350, true, true),
        (Preset.Safe, 8) => new(true, 0.9, 0.9, 0.4, 0.4, true, false, 0.0, 0.5, false, 3.5, 3.5, 360, true, true),
        (Preset.Safe, 9) => new(true, 0.5, 0.5, 0.4, 0.4, true, false, 0.0, 0.5, true, 3.0, 3.0, 312, true, true),
        (Preset.Safe, 10) => new(true, 0.9, 0.9, 0.4, 0.9, true, false, 0.0, 0.5, true, 3.0, 3.0, 360, true, true),
        (Preset.Fast, 0) => new(false, 0.5, 0.5, 0.0, 0.5, true, true, 0.6, 1.3, true, 2.15, 2.15, 160, false, false),
        _ => null,
    };

    static int Cyc(double ns, double f, double add = 0) => (int)(ns / 2000.0 * f + add);
    static string S(double v) => v.ToString(CultureInfo.InvariantCulture);
    static bool Even(double v) => v % 2.0 == 0.0;

    /// <summary>True when the original has a manual formula for this preset/IC.</summary>
    public static bool Has(Preset preset, int mt) => Params(preset, mt, 0) != null || (mt == 2) || (preset == Preset.Safe && mt == 3);

    /// <param name="mt">original IC index</param>
    /// <param name="rank">0 = single rank, 1 = dual rank (original MR index)</param>
    /// <param name="f">effective MT/s (BCLK × ratio)</param>
    public static Dictionary<string, string> Compute(Preset preset, int mt, int rank, double f, ManualTimings x, bool? cjrHighBin = null)
    {
        var o = new Dictionary<string, string>();
        var p = Params(preset, mt, x.tRCDRD, cjrHighBin);
        if (p != null) DebugProfile(o, p, mt, rank, f, x);
        else if (mt == 2) DieDE(o, preset, f, x);
        else if (preset == Preset.Safe && mt == 3) Oem(o, f, x);
        else throw new ArgumentException("no manual formula for this preset/IC");

        // per-IC adjustments the original applies right after the formula
        if (preset == Preset.Safe && mt is 4 or 5 or 7) o["tcke"] = S(Cyc(5.0, f));
        if (preset == Preset.Safe && mt is 6 or 9) { o["powerdown"] = "Disabled"; o["tcke"] = "1"; }

        // Advanced(): the 6 → 7 tRDWR fix for single rank 3133-3333
        if (rank == 0 && f >= 3133.0 && f <= 3333.0 && o.GetValueOrDefault("trdwr") == "6") o["trdwr"] = "7";
        o.Remove("twrrd");   // always overwritten by Advanced() from the rank/DIMM table
        return o;
    }

    static void DebugProfile(Dictionary<string, string> o, DpParams p, int mt, int rank, double f, ManualTimings x)
    {
        OcPotential(o, mt, x.tCL);

        double cl = (int)(x.tCL / 2000.0 * f - p.CL);
        string clText = cl switch { 13 or 15 => "14", 17 => "16", 19 => "18", 21 => "20", _ => S(cl) };
        o["tcl"] = clText; o["tcwl"] = clText;

        double rcdwr = (int)(x.tRCDWR / 2000.0 * f - p.RCDWR); o["trcdwr"] = S(rcdwr);
        double rcdrd = (int)(x.tRCDRD / 2000.0 * f - p.RCDRD); o["trcdrd"] = S(rcdrd);
        double rp = (int)(x.tRP / 2000.0 * f - p.RP); o["trp"] = S(rp);

        double ras;
        if (!p.RasFromRcd) { ras = (int)(x.tRAS / 2000.0 * f - 0.5); if (!Even(ras)) ras -= 1.0; }
        else ras = rcdrd * 2.0;
        o["tras"] = S(ras);

        double rc;
        if (!p.RcFromRcd) { rc = (int)(x.tRC / 2000.0 * f); if (!Even(rc)) rc -= 1.0; }
        else { rc = rcdrd * 2.0 + rp + 2.0; if (!Even(rc)) rc += 1.0; }
        o["trc"] = S(rc);

        o["trfc"] = S(Cyc(p.Rfc, f));
        o["trfcalt"] = S(rc * 8.0);   // the original derives "tRFC alt" from tRC, not from its RFCalt argument

        double rrds = x.tRRDS >= 3.8 ? 3.5 : x.tRRDS;
        double rrdl = x.tRRDL >= 6.0 ? 5.5 : x.tRRDL <= 5.0 ? 5.0 : x.tRRDL;
        int rrdsCyc = (int)(rrds / 2000.0 * f - p.RRDS);
        o["trrds"] = S(rrdsCyc);
        o["trrdl"] = S((int)(rrdl / 2000.0 * f - p.RRDL));
        o["tfaw"] = S(rrdsCyc * (p.Faw4x ? 4 : 6));

        o["twtrs"] = S(Cyc(2.5, f, 0.3));
        double wtrl = Cyc(7.0, f, 0.5);
        if (f <= 3533.0) { o["twtrl"] = "12"; o["twr"] = p.Safe ? "24" : "12"; }
        else if (p.Safe)
        {
            if (Even(wtrl)) { o["twtrl"] = S(wtrl); o["twr"] = S(wtrl * 2.0); }
            else { wtrl += 1.0; o["twtrl"] = S(wtrl); o["twr"] = S((wtrl - 1.0) * 2.0); }
        }
        else
        {
            if (!Even(wtrl)) wtrl += 1.0;
            o["twtrl"] = S(wtrl); o["twr"] = S(wtrl);
        }

        o["trdrd"] = S(Cyc(p.RdScl, f));
        o["twrwr"] = S(Cyc(p.WrScl, f));

        double rtpNs = mt == 0 ? (p.Safe ? (f <= 3466.0 ? 5.0 : 5.8) : 4.25) : 7.5;
        double rtp = Cyc(rtpNs, f); if (!Even(rtp)) rtp += 1.0;
        o["trtp"] = S(rtp);
        o["trdwr"] = S(Cyc(3.9, f, 1.0));

        if (p.Cke) { o["powerdown"] = "Enabled"; o["tcke"] = S((int)(5.0 / 2000.0 * f - 0.5)); }
        else { o["powerdown"] = "Disabled"; o["tcke"] = "1"; }
        o["geardown"] = p.Gdm || rank != 0 ? "Enabled" : "Disabled";
    }

    /// <summary>Samsung D/E-die (SAFE and FAST) — the IC has its own formula, not Debug_profile.</summary>
    static void DieDE(Dictionary<string, string> o, Preset preset, double f, ManualTimings x)
    {
        bool fast = preset == Preset.Fast;
        double cl = (int)(x.tCL / 2000.0 * f);
        o["tcl"] = cl == 15.0 ? "16" : S(cl);
        double rcdwr = (int)(x.tRCDWR / 2000.0 * f - (fast ? 1.0 : 0.0));
        double rcdrd = (int)(x.tRCDRD / 2000.0 * f);
        double rp = (int)(x.tRP / 2000.0 * f - (fast ? 1.0 : 0.0));
        if (fast) { o["trcdwr"] = S(rcdwr); o["trcdrd"] = S(rcdrd); o["trp"] = S(rp); }
        else
        {
            o["trcdwr"] = rcdwr == 15.0 ? "16" : S(rcdwr);
            o["trcdrd"] = rcdrd == 15.0 ? "16" : S(rcdrd);
            o["trp"] = rp == 15.0 ? "16" : S(rp);
        }
        double ras = (int)(x.tRAS / 2000.0 * f - (fast ? 2.0 : 0.0)); if (!Even(ras)) ras += 1.0;
        o["tras"] = S(ras);
        double rc = (int)(x.tRC / 2000.0 * f);
        if (fast) { if (!Even(rc)) rc += 1.0; }
        else if (rc == 52.0) rc = 54.0;
        else if (!Even(rc)) rc += 1.0;
        o["trc"] = S(rc);

        o["trrds"] = S(Cyc(x.tRRDS, f, fast ? 0.5 : 0.0));
        o["trrdl"] = S(Cyc(x.tRRDL, f, 0.5));
        o["tfaw"] = S(fast ? Cyc(x.tRRDS, f) * 4 : Cyc(x.tFAW, f));
        o["twtrs"] = S(Cyc(2.5, f));
        if (fast)
        {
            o["twtrl"] = S(Cyc(7.5, f));
            double wr = Cyc(7.5, f);
            o["twr"] = wr == 11.0 ? "12" : S(wr);
            o["trdrd"] = S(Cyc(3.5, f, -1.0)); o["twrwr"] = S(Cyc(3.5, f, -1.0));
            o["trfc"] = S(Cyc(195.0, f)); o["trfcalt"] = S(Cyc(192.0, f));
            o["powerdown"] = "Disabled"; o["tcke"] = "1";
        }
        else
        {
            o["twtrl"] = "12"; o["twr"] = "12";
            o["trdrd"] = S(Cyc(3.5, f)); o["twrwr"] = S(Cyc(3.5, f));
            o["trfc"] = S(Cyc(260.0, f)); o["trfcalt"] = S(Cyc(232.0, f));
            o["tcke"] = S(Cyc(5.0, f));
        }
        o["tcwl"] = S(cl);   // the original copies the raw (possibly odd) CL into tCWL
        o["trtp"] = S(Cyc(7.5, f));
        o["trdwr"] = S(Cyc(4.15, f));
    }

    /// <summary>Samsung OEM (SAFE only).</summary>
    static void Oem(Dictionary<string, string> o, double f, ManualTimings x)
    {
        double cl = (int)(x.tCL / 2000.0 * f);
        o["tcl"] = cl is 15.0 or 17.0 ? "16" : S(cl);
        double rcdwr = (int)(x.tRCDWR / 2000.0 * f); o["trcdwr"] = rcdwr == 15.0 ? "16" : S(rcdwr);
        double rcdrd = (int)(x.tRCDRD / 2000.0 * f); o["trcdrd"] = rcdrd == 15.0 ? "16" : S(rcdrd);
        double rp = (int)(x.tRP / 2000.0 * f); o["trp"] = rp == 15.0 ? "16" : S(rp);
        double ras = (int)(x.tRAS / 2000.0 * f); if (!Even(ras)) ras += 1.0; o["tras"] = S(ras);
        double rc = (int)(x.tRC / 2000.0 * f); if (!Even(rc)) rc += 1.0; o["trc"] = S(rc);
        o["trrds"] = S(Cyc(x.tRRDS, f));
        o["trrdl"] = S(Cyc(x.tRRDL, f, 0.5));
        o["tfaw"] = S(Cyc(x.tFAW, f));
        o["twtrs"] = S(Cyc(2.5, f));
        o["twtrl"] = S(Cyc(7.5, f));
        o["twr"] = "24";
        o["trdrd"] = S(Cyc(2.8, f, 1.0)); o["twrwr"] = S(Cyc(2.8, f, 1.0));
        o["trfc"] = S(Cyc(350.0, f)); o["trfcalt"] = S(Cyc(300.0, f));
        o["tcwl"] = S(cl);
        o["trtp"] = S(Cyc(7.5, f));
        o["trdwr"] = S(Cyc(4.15, f));
        o["tcke"] = S(Cyc(5.0, f));
    }

    /// <summary>"OC potential" estimate the original prints in Manual mode for a few ICs:
    /// how far the kit's XMP tAA is from a reference die (percent) and the MT/s that implies.</summary>
    static void OcPotential(Dictionary<string, string> o, int mt, double tClNs)
    {
        (double num, double baseMts)? r = mt switch
        {
            0 => (8.174, 4400.0),
            4 or 5 => (9.76, 3533.0),
            6 => (10.0, 3933.0),
            9 => (10.0, 4133.0),
            _ => null,
        };
        if (r is { } v)
        {
            o["lbl_qual"] = ((int)(v.num / tClNs * 100.0)).ToString(CultureInfo.InvariantCulture);
            o["lbl_qualsafe"] = ((int)(v.num / tClNs * v.baseMts)).ToString(CultureInfo.InvariantCulture);
            o["lbl_CAS"] = "CL16";
        }
        else { o["lbl_qual"] = "N/A"; o["lbl_qualsafe"] = "N/A"; o["lbl_CAS"] = "N/A"; }
    }
}
