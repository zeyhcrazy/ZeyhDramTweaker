using System.Globalization;
using ZeyhDramTweaker.Core.Model;

namespace ZeyhDramTweaker.Core.Engines;

/// <summary>DDR4 calculator. Zen 1 / Zen+ / Threadripper / Zen 2 use the 1.7.3 tables and Manual
/// formula unchanged; Zen 3 (desktop and APU) builds on the Zen 2 data — same 12 nm IOD on Vermeer —
/// and replaces FCLK and the SoC-side voltages, extending the range past the 3866 MT/s where the
/// original stopped.</summary>
public static class Ddr4Engine
{
    public sealed record Raw(FactoredTables.Outcome Outcome, Dictionary<string, string> Fields, bool Manual);

    static string Preset(Preset p) => p == Model.Preset.Safe ? "SAFE" : "FAST";
    static int RankIdx(int ranks) => ranks >= 2 ? 1 : 0;
    static int DimmIdx(int dimms) => dimms switch { 1 => 0, 4 => 2, _ => 1 };

    public static LegacyKey KeyFor(CalcInput i, PlatformInfo plat, string mts)
    {
        var topo = Catalog.TopologiesFor(plat);
        int t = topo[Math.Clamp(i.TopologyIndex, 0, topo.Count - 1)].LegacyIndex;
        return new LegacyKey(Preset(i.Preset), plat.LegacyCpuIndex, Catalog.Ic(i.IcId).LegacyIndex,
            (int)i.Profile, RankIdx(i.Ranks), DimmIdx(i.Dimms), t, mts);
    }

    /// <summary>What the original 1.7.3 produces for these inputs (original control names).</summary>
    public static Raw Original(CalcInput input, PlatformInfo plat, int mts) =>
        Original(KeyFor(input, plat, mts.ToString(CultureInfo.InvariantCulture)), input.Manual);

    /// <summary>Same, addressed with the original form indices (used by the verifier).</summary>
    public static Raw Original(LegacyKey key, ManualTimings? xmp, bool? cjrHighBin = null)
    {
        if (key.Profile != (int)Profile.Manual)
        {
            var t = FactoredTables.Legacy;
            var outcome = t.OutcomeOf(key);
            return new Raw(outcome, outcome.Supported ? t.Fields(key) : new(), false);
        }
        var m = FactoredTables.Manual;
        var mo = m.OutcomeOf(key);
        if (!mo.Supported || xmp is not { IsComplete: true } ns)
            return new Raw(mo, new(), true);
        var fields = m.Fields(key);
        var preset = key.Preset == "FAST" ? Model.Preset.Fast : Model.Preset.Safe;
        double f = double.Parse(key.Mts, CultureInfo.InvariantCulture);
        foreach (var (k, v) in Ddr4ManualFormula.Compute(preset, key.Mt, key.Rank, f, ns, cjrHighBin)) fields[k] = v;
        return new Raw(mo, fields, true);
    }

    public static CalcResult Calculate(CalcInput input)
    {
        var plat = Catalog.Get(input.Platform);
        return input.Platform is Platform.Zen3 or Platform.Zen3Apu ? Zen3(input, plat) : Legacy(input, plat);
    }

    static CalcResult Legacy(CalcInput input, PlatformInfo plat)
    {
        var raw = Original(input, plat, input.MTs);
        var res = new CalcResult { Ok = raw.Outcome.Supported && raw.Fields.ContainsKey("tcl"), Input = input, EffectiveMTs = input.MTs };
        AddOutcomeNotices(res, raw);
        if (!res.Ok) return res;
        FixOriginalCrash(res, raw.Fields);
        VoltageModel.CalibrateDdr4(raw.Fields, input.IcId, input.MTs, res);
        Ddr4Layout.Fill(res, raw.Fields, sec => sec == Ddr4Layout.Voltages || sec == Ddr4Layout.MainVoltages ? Provenance.Calibrated
            : raw.Manual ? Provenance.OriginalFormula : Provenance.Original);
        Finish(res, raw.Fields, input);
        return res;
    }

    // ---------------------------------------------------------------- Zen 3

    /// <summary>Highest MT/s the 1.7.3 data covers (any IC / profile).</summary>
    const int OriginalCeiling = 3866;

    /// <summary>How far past the original data each IC is worth pushing on Zen 3 (community norms).</summary>
    static int Zen3Ceiling(string ic, bool apu) => ic switch
    {
        "samsung-b" => apu ? 4800 : 4600,
        "hynix-cjr" => apu ? 4400 : 4000,
        "micron-e" => apu ? 4600 : 4400,
        _ => 0,   // no extrapolation: stay inside what the original validated
    };

    static CalcResult Zen3(CalcInput input, PlatformInfo plat)
    {
        bool apu = plat.Apu;
        int f = input.MTs;
        var res = new CalcResult { Input = input, EffectiveMTs = f };
        Raw raw;
        bool extrapolated = false;
        int baseMts = f;
        if (f <= OriginalCeiling)
            raw = Original(input, plat, f);
        else
        {
            if (f > Zen3Ceiling(input.IcId, apu))
            {
                res.Notices.Add(new Notice(NoticeLevel.Error, "err.zen3.ceiling", Zen3Ceiling(input.IcId, apu)));
                return new CalcResult { Ok = false, Input = input, EffectiveMTs = f, Notices = { res.Notices[0] } };
            }
            // highest frequency the original supports for this exact combination
            baseMts = Catalog.Ddr4Axis.Where(m => m <= OriginalCeiling).Reverse()
                .FirstOrDefault(m => Original(input, plat, m) is { Outcome.Supported: true } r && r.Fields.ContainsKey("tcl"));
            if (baseMts == 0) { raw = Original(input, plat, OriginalCeiling); }
            else
            {
                raw = Original(input, plat, baseMts);
                var ext = Extrapolate(raw.Fields, baseMts, f, input);
                if (raw.Manual && input.Manual is { IsComplete: true } xmp)
                {
                    // Manual is nanosecond-based: run the formula at the real target instead of scaling clocks
                    var key = KeyFor(input, plat, baseMts.ToString(CultureInfo.InvariantCulture));
                    foreach (var (k, v) in Ddr4ManualFormula.Compute(input.Preset, key.Mt, key.Rank, f, xmp)) ext[k] = v;
                    ext["geardown"] = "Enabled";
                }
                raw = raw with { Fields = ext };
                extrapolated = true;
            }
        }

        bool ok = raw.Outcome.Supported && raw.Fields.ContainsKey("tcl");
        res = new CalcResult { Ok = ok, Input = input, EffectiveMTs = f };
        AddOutcomeNotices(res, raw);
        if (!ok) return res;
        FixOriginalCrash(res, raw.Fields);

        var fields = raw.Fields;
        Zen3Voltages(fields, f, apu, res);
        VoltageModel.CalibrateDdr4(fields, input.IcId, f, res);

        res.Notices.Add(new Notice(NoticeLevel.Info, apu ? "info.zen3apu.base" : "info.zen3.base"));
        if (extrapolated) res.Notices.Add(new Notice(NoticeLevel.Warning, "warn.zen3.extrapolated", baseMts, f));

        Ddr4Layout.Fill(res, fields, sec => sec switch
        {
            Ddr4Layout.Voltages or Ddr4Layout.Misc or Ddr4Layout.MainVoltages => Provenance.Extended,
            _ when extrapolated && sec is Ddr4Layout.Primary or Ddr4Layout.Secondary or Ddr4Layout.Tertiary => Provenance.Extended,
            _ => raw.Manual ? Provenance.OriginalFormula : Provenance.Original,
        });
        Finish(res, fields, input);
        return res;
    }

    /// <summary>Above the original ceiling: keep every timing's nanoseconds constant
    /// (cycles × f/base, rounded up) and the evenness the original enforces.</summary>
    static Dictionary<string, string> Extrapolate(Dictionary<string, string> src, int baseMts, int f, CalcInput input)
    {
        var o = new Dictionary<string, string>(src);
        double k = (double)f / baseMts;
        string[] scale = { "tcl", "tcwl", "trcdrd", "trcdwr", "trp", "tras", "trc", "trrds", "trrdl", "tfaw", "twtrs", "twtrl",
                           "twr", "trtp", "trfc", "trfcalt", "trdwr" };
        foreach (var name in scale)
        {
            if (!o.TryGetValue(name, out var s) || !int.TryParse(s, out var c)) continue;
            int v = (int)Math.Ceiling(c * k - 1e-9);
            if (name is "tcl" or "tcwl" or "tras" or "trc" or "twr" or "trtp" && v % 2 == 1) v++;   // GDM / original evenness
            o[name] = v.ToString(CultureInfo.InvariantCulture);
        }
        o["geardown"] = "Enabled";
        // DRAM voltage: +10 mV per 66 MT/s step above the last validated point (B-die scales with voltage)
        if (double.TryParse(o.GetValueOrDefault("vdramrec"), NumberStyles.Float, CultureInfo.InvariantCulture, out var vr))
        {
            int steps = (int)Math.Round((f - baseMts) / 66.67);
            double rec = Math.Min(vr + 0.01 * steps, input.IcId == "samsung-b" ? 1.50 : 1.45);
            double alt = Math.Min(rec + 0.03, input.IcId == "samsung-b" ? 1.55 : 1.47);
            o["vdramrec"] = Fmt(rec, 3); o["vdramalt"] = Fmt(alt, 3);
            DeriveDramVoltages(o);
        }
        return o;
    }

    /// <summary>Zen 3 FCLK and SoC-side voltages by fabric clock. Vermeer: 1:1 up to FCLK 2000
    /// (DDR4-4000), 2:1 above with FCLK 1900-2000. Cezanne/Renoir APU: monolithic, 1:1 to ~2400.</summary>
    static void Zen3Voltages(Dictionary<string, string> o, int f, bool apu, CalcResult res)
    {
        int oneToOneMax = apu ? 4800 : 4000;
        bool sync = f <= oneToOneMax;
        int fclk = sync ? f / 2 : 2000;
        o["FCLK_textbox"] = sync ? fclk.ToString(CultureInfo.InvariantCulture) : "1900-2000";

        // (VSOC min/rec/max, VDDG CCD min/rec/max, VDDG IOD min/rec/max, VDDP rec, cLDO VDDP rec)
        (double[] soc, double[] ccd, double[] iod, double vddp) t;
        if (!apu)
        {
            t = (sync ? fclk : 2000) switch
            {
                <= 1600 => (new[] { 1.000, 1.050, 1.100 }, new[] { 0.900, 0.900, 0.950 }, new[] { 0.950, 1.000, 1.050 }, 0.900),
                <= 1800 => (new[] { 1.050, 1.100, 1.125 }, new[] { 0.900, 0.950, 1.000 }, new[] { 1.000, 1.050, 1.075 }, 0.950),
                <= 1900 => (new[] { 1.075, 1.125, 1.150 }, new[] { 0.950, 1.000, 1.050 }, new[] { 1.050, 1.075, 1.100 }, 1.000),
                _ => (new[] { 1.125, 1.150, 1.200 }, new[] { 1.000, 1.025, 1.050 }, new[] { 1.075, 1.100, 1.150 }, 1.000),
            };
            if (!sync) t.soc = new[] { 1.100, 1.150, 1.200 };
        }
        else
        {
            t = fclk switch
            {
                <= 1800 => (new[] { 1.050, 1.100, 1.150 }, new[] { 0.950, 1.000, 1.050 }, new[] { 0.950, 1.000, 1.050 }, 0.950),
                <= 2000 => (new[] { 1.100, 1.150, 1.200 }, new[] { 1.000, 1.050, 1.100 }, new[] { 1.000, 1.050, 1.100 }, 1.000),
                <= 2200 => (new[] { 1.150, 1.200, 1.250 }, new[] { 1.050, 1.100, 1.150 }, new[] { 1.050, 1.100, 1.150 }, 1.000),
                _ => (new[] { 1.200, 1.250, 1.300 }, new[] { 1.100, 1.150, 1.200 }, new[] { 1.100, 1.150, 1.200 }, 1.050),
            };
        }
        o["vsocmin"] = Fmt(t.soc[0], 3); o["vsocrec"] = Fmt(t.soc[1], 3); o["vsocalt"] = Fmt(t.soc[2], 3);
        o["vddgmin"] = Fmt(t.ccd[0], 3); o["vddgrec"] = Fmt(t.ccd[1], 3); o["vddgmax"] = Fmt(t.ccd[2], 3);
        o["vddgmin2"] = Fmt(t.iod[0], 3); o["vddgrec2"] = Fmt(t.iod[1], 3); o["vddgmax2"] = Fmt(t.iod[2], 3);
        o["vddprec"] = Fmt(t.vddp, 3); o["vddpalt1"] = Fmt(t.vddp - 0.05, 3); o["vddpalt2"] = Fmt(t.vddp + 0.05, 3);
        o["cldorec"] = Fmt(t.vddp, 3); o["cldomin"] = "0.850"; o["cldomax"] = "1.050";

        if (!sync) res.Notices.Add(new Notice(NoticeLevel.Warning, "warn.zen3.desync", f / 2, f / 4));
        if (!apu && sync && fclk > 1900) res.Notices.Add(new Notice(NoticeLevel.Warning, "warn.zen3.fclk2000", fclk));
        if (apu && fclk > 2200) res.Notices.Add(new Notice(NoticeLevel.Warning, "warn.zen3apu.fclk", fclk));
        res.Notices.Add(new Notice(NoticeLevel.Info, "info.zen3.vddgrule"));
    }

    // ---------------------------------------------------------------- shared helpers

    static string Fmt(double v, int decimals) => v.ToString("F" + decimals, CultureInfo.InvariantCulture);

    /// <summary>The original's VTTDDR() and v_drammin(): VTT/Vref = VDIMM/2, boot = VDIMM.
    /// .NET Framework printed doubles with 15 significant digits; G15 reproduces that.</summary>
    public static void DeriveDramVoltages(Dictionary<string, string> o)
    {
        if (!double.TryParse(o.GetValueOrDefault("vdramrec"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rec)) return;
        double alt = double.TryParse(o.GetValueOrDefault("vdramalt"), NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : rec;
        string G(double v) => v.ToString("G15", CultureInfo.InvariantCulture);
        o["bootdramrec"] = o["vdramrec"]; o["bootdramalt"] = o.GetValueOrDefault("vdramalt") ?? o["vdramrec"];
        o["vttddrrecmin"] = G(rec / 2.0 - 0.002); o["vttddraltmin"] = G(alt / 2.0 - 0.002);
        o["vttddrrecmax"] = G(rec / 2.0 + 0.0015); o["vttddraltmax"] = G(alt / 2.0 + 0.0015);
        o["Vrefrec"] = G(rec / 2.0); o["Vrefalt"] = G(rec * 0.55);
        o["vdrammin"] = G(rec - 0.01);
    }

    /// <summary>1.7.3 bug: Samsung S-die A0/B0 never sets a DRAM voltage, so the original throws
    /// (FormatException) before filling VSOC. We complete it and say so.</summary>
    static void FixOriginalCrash(CalcResult res, Dictionary<string, string> f)
    {
        if (f.ContainsKey("vdramrec")) return;
        f["vdramrec"] = "1.350"; f["vdramalt"] = "1.380";
        DeriveDramVoltages(f);
        res.Notices.Add(new Notice(NoticeLevel.Warning, "warn.original.crash"));
    }

    static void AddOutcomeNotices(CalcResult res, Raw raw)
    {
        foreach (var part in raw.Outcome.Message.Split("||", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var msg = part.Split('|')[0].Trim();
            if (msg.StartsWith("EXC")) continue;   // handled by FixOriginalCrash
            var level = part.EndsWith("Warning") ? NoticeLevel.Warning : NoticeLevel.Error;
            res.Notices.Add(msg switch
            {
                "Limit for GEN 1 is reached!" => new Notice(level, "warn.gen1limit"),
                "Only dual rank!" => new Notice(level, "warn.onlydualrank"),
                "Not supported!" or "Coming soon!" or "Coming in 1.7.2 !" => new Notice(level, "err.notsupported"),
                "Not supported , Only FAST with A2/B2 profile!" => new Notice(level, "err.onlyfasta2"),
                "Manual profile only for SAFE!" => new Notice(level, "err.manualsafeonly"),
                "Enter the values!" => new Notice(level, "err.entervalues"),
                _ when msg.StartsWith("Please") => new Notice(level, "err.tryprofiles", SuggestedProfiles(msg)),
                _ => new Notice(level, "err.raw", msg),
            });
        }
        if (raw.Manual && raw.Outcome.Supported && raw.Fields.Count == 0)
            res.Notices.Add(new Notice(NoticeLevel.Error, "err.entervalues"));
    }

    static string SuggestedProfiles(string msg)
    {
        var names = new List<string>();
        if (msg.Contains("A2/B2")) names.Add("A2/B2");
        if (msg.Contains("A0/B0")) names.Add("A0/B0");
        if (msg.Contains("Bad bin")) names.Add("Bad bin");
        if (msg.Contains("Manual")) names.Add("Manual");
        return string.Join(", ", names);
    }

    /// <summary>VDIMM hint with the 1.7.3 value, factory reference kits, OC potential.</summary>
    static void Finish(CalcResult res, Dictionary<string, string> f, CalcInput input)
    {
        if (res.Section(Ddr4Layout.Voltages) is { } v && f.TryGetValue("__vdimm_range", out var range))
        {
            int i = v.Rows.FindIndex(r => r.Name == "DRAM (VDIMM)");
            var parts = range.Split('|');
            string orig = f.GetValueOrDefault("__vdimm_orig") ?? parts[0];
            if (i >= 0) v.Rows[i] = v.Rows[i] with { HintKey = $"hint.vdimm.range|{parts[2]}|{orig}" };
        }
        if (int.TryParse(f.GetValueOrDefault("tcl"), out var cl) && VoltageModel.ReferenceSection(VoltageModel.FamilyOf(input.IcId), input.MTs, cl) is { } refs)
            res.Sections.Add(refs);
        AddOcPotential(res, f);
    }

    static void AddOcPotential(CalcResult res, Dictionary<string, string> f)
    {
        if (f.TryGetValue("lbl_qual", out var q) && q != "N/A")
            res.Notices.Add(new Notice(NoticeLevel.Info, "info.ocpotential", q, f.GetValueOrDefault("lbl_qualsafe") ?? "?"));
    }
}
