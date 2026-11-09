using ZeyhDramTweaker.Core.Model;

namespace ZeyhDramTweaker.Core.Engines;

/// <summary>Turns the original control-name → value map into result sections. Section, row and
/// column order follow the blocks of the 1.7.3 window (Main, Voltage Block, Termination Block,
/// CAD_BUS Block/Timings, Misc items, PMU Training, Memory Interleaving + tweaks).</summary>
public static class Ddr4Layout
{
    public const string Primary = "sec.primary", Secondary = "sec.secondary", Tertiary = "sec.tertiary",
        Misc = "sec.misc", Voltages = "sec.voltages", MainVoltages = "sec.mainvoltages", Termination = "sec.termination",
        CadBus = "sec.cadbus", CadTimings = "sec.cadtimings", Pmu = "sec.pmu", Interleave = "sec.interleave";

    static readonly (string section, string[] cols, (string name, string[] fields)[] rows)[] Spec =
    {
        (Primary, new[] { "col.value" }, new[]
        {
            ("tCL", new[] { "tcl" }), ("tRCDRD", new[] { "trcdrd" }), ("tRCDWR", new[] { "trcdwr" }),
            ("tRP", new[] { "trp" }), ("tRAS", new[] { "tras" }), ("tRC", new[] { "trc" }),
            ("tRFC", new[] { "trfc" }), ("tRFC (alt)", new[] { "trfcalt" }),
        }),
        (Secondary, new[] { "col.value" }, new[]
        {
            ("tRRDS", new[] { "trrds" }), ("tRRDL", new[] { "trrdl" }), ("tFAW", new[] { "tfaw" }),
            ("tWTRS", new[] { "twtrs" }), ("tWTRL", new[] { "twtrl" }), ("tWR", new[] { "twr" }),
            ("tRTP", new[] { "trtp" }), ("tCWL", new[] { "tcwl" }), ("tCKE", new[] { "tcke" }),
        }),
        (Tertiary, new[] { "col.value" }, new[]
        {
            ("tRDRDSCL", new[] { "trdrd" }), ("tWRWRSCL", new[] { "twrwr" }), ("tRDWR", new[] { "trdwr" }),
            ("tWRRD", new[] { "twrrd" }), ("tRDRDSC", new[] { "trdrdsc" }), ("tRDRDSD", new[] { "trdrdsd" }),
            ("tRDRDDD", new[] { "trdrddd" }), ("tWRWRSC", new[] { "twrwrsc" }), ("tWRWRSD", new[] { "twrwrsd" }),
            ("tWRWRDD", new[] { "twrwrdd" }),
        }),
        (Misc, new[] { "col.value" }, new[]
        {
            ("Gear Down Mode", new[] { "geardown" }), ("Power Down Mode", new[] { "powerdown" }),
            ("Command Rate", new[] { "cr" }), ("BankGroupSwap", new[] { "BGS" }), ("BankGroupSwap Alt", new[] { "BGSalt" }),
            ("FCLK", new[] { "FCLK_textbox" }),
        }),
        (Voltages, new[] { "col.min", "col.rec", "col.max" }, new[]
        {
            ("DRAM (VDIMM)", new[] { "vdrammin", "vdramrec", "vdramalt" }),
            ("SOC (VSOC)", new[] { "vsocmin", "vsocrec", "vsocalt" }),
            ("VDDG CCD", new[] { "vddgmin", "vddgrec", "vddgmax" }),
            ("VDDG IOD", new[] { "vddgmin2", "vddgrec2", "vddgmax2" }),
            ("cLDO VDDP", new[] { "cldomin", "cldorec", "cldomax" }),
            ("VDDP", new[] { "vddpalt1", "vddprec", "vddpalt2" }),
            ("VPP", new[] { "vppmin", "vpprec", "vppmax" }),
            ("PLL (1.8V)", new[] { "pllmin", "pllrec", "pllmax" }),
        }),
        (MainVoltages, new[] { "col.rec", "col.alt" }, new[]
        {
            ("Boot DRAM", new[] { "bootdramrec", "bootdramalt" }),
            ("VTT DDR min", new[] { "vttddrrecmin", "vttddraltmin" }),
            ("VTT DDR max", new[] { "vttddrrecmax", "vttddraltmax" }),
            ("Vref (CHA/CHB)", new[] { "Vrefrec", "Vrefalt" }),
        }),
        (Termination, new[] { "col.rec", "col.alt1", "col.alt2" }, new[]
        {
            ("ProcODT", new[] { "procodtrec", "procodtalt1", "procodtalt2" }),
            ("RTT_NOM", new[] { "rttnomrec", "rttnomalt1", "rttnomalt2" }),
            ("RTT_WR", new[] { "rttwrrec", "rttwralt1", "rttwralt2" }),
            ("RTT_PARK", new[] { "rttparkrec", "rttparkalt1", "rttparkalt2" }),
        }),
        (CadBus, new[] { "col.rec", "col.alt1", "col.alt2", "col.alt3" }, new[]
        {
            ("ClkDrvStren", new[] { "clkrec", "clkalt1", "clkalt2", "clkalt3" }),
            ("AddrCmdDrvStren", new[] { "addrrec", "addralt1", "addralt2", "addralt3" }),
            ("CsOdtDrvStren", new[] { "csodtrec", "csodtalt1", "csodtalt2", "csodtalt3" }),
            ("CkeDrvStren", new[] { "ckerec", "ckealt1", "ckealt2", "ckealt3" }),
        }),
        (CadTimings, new[] { "col.rec", "col.alt" }, new[]
        {
            ("AddrCmdSetup", new[] { "CAD_BUS_AddrCmd", "CAD_BUS_AddrCmd2" }),
            ("CsOdtSetup", new[] { "CAD_BUS_CsOdt", "CAD_BUS_CsOdt2" }),
            ("CkeSetup", new[] { "CAD_BUS_Cke", "CAD_BUS_Cke2" }),
        }),
        (Pmu, new[] { "col.value" }, new[]
        {
            ("DFE Read Training", new[] { "dfe" }), ("FFE Write Training", new[] { "ffe" }),
            ("PMU Pattern Bits Control", new[] { "pmu1" }), ("PMU Pattern Bits", new[] { "pmu2" }),
            ("MR6 VrefDQ Control", new[] { "MR6" }), ("CPU Vref Training Seed", new[] { "cpuvref" }),
        }),
        (Interleave, new[] { "col.value" }, new[]
        {
            ("Memory Interleaving", new[] { "typeinter" }), ("Memory Interleaving Size", new[] { "sizeinter" }),
            ("Channel Interleaving Hash", new[] { "Channel_interleaving_hash" }), ("DRAM R1-R4 Tune", new[] { "Dramrtune" }),
            ("L1 Stream HW Prefetcher", new[] { "metroTextBox7" }), ("L2 Stream HW Prefetcher", new[] { "metroTextBox6" }),
            ("Super I/O Clock Skew", new[] { "metroTextBox5" }), ("Opcache", new[] { "metroTextBox4" }),
            ("Spread Spectrum", new[] { "metroTextBox2" }), ("Memory Clear", new[] { "MC" }),
        }),
    };

    /// <summary>Every original field name the layout shows (the verifier checks nothing is lost).</summary>
    public static IEnumerable<string> AllFields => Spec.SelectMany(s => s.rows).SelectMany(r => r.fields);

    public static void Fill(CalcResult result, IReadOnlyDictionary<string, string> f, Func<string, Provenance> provenance)
    {
        foreach (var (section, cols, rows) in Spec)
        {
            var list = new List<ResultRow>();
            foreach (var (name, fields) in rows)
            {
                var vals = fields.Select(x => f.TryGetValue(x, out var v) ? v : null).ToArray();
                if (vals.All(v => v is null or "N/A")) continue;   // e.g. VDDG on Zen 1, which the original shows as N/A
                list.Add(new ResultRow(name, vals));
            }
            if (list.Count > 0) result.Sections.Add(new ResultSection(section, cols, list, provenance(section)));
        }
    }
}
