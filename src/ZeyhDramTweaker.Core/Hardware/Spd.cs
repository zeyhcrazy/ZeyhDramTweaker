using System.Globalization;
using System.Text;
using ZeyhDramTweaker.Core.Model;

namespace ZeyhDramTweaker.Core.Hardware;

/// <summary>An overclocking/JEDEC profile stored in the SPD, in nanoseconds.</summary>
public sealed record SpdProfile(string Name, int MTs, double? Vdd, ManualTimings Timings)
{
    double Tck => 2000.0 / MTs;
    int C(double ns) => (int)Math.Ceiling(ns / Tck - 0.02);
    /// <summary>"16-19-19-39" at the profile's own speed.</summary>
    public string Label => $"{C(Timings.tCL)}-{C(Timings.tRCDRD)}-{C(Timings.tRP)}-{C(Timings.tRAS)}";
}

public sealed record SpdModule(
    int Address, MemGen Mem, string ModuleVendor, string PartNumber, string DramVendor, int DensityGbit, int Width,
    int Ranks, int CapacityGB, char? DieLetter, string? IcId, bool IcCertain, List<SpdProfile> Profiles)
{
    public string Slot => $"DIMM {Address - 0x50 + 1}";
    public string DieText => DieLetter is { } d ? $"{d}-die" : "?";
}

/// <summary>
/// Reads and decodes the SPD EEPROM of every DIMM (what Thaiphoon Burner does). DDR4: EE1004, 2 pages of
/// 256 bytes selected by the SPA0/SPA1 commands at 0x36/0x37; JEDEC timings + Intel XMP 2.0 (bytes 384+).
/// DDR5: SPD5 hub, 8 pages of 128 bytes selected through MR11, data at registers 0x80-0xFF; JEDEC +
/// XMP 3.0 (640+) + AMD EXPO (832+). Read-only: the only writes are the page-select commands.
/// </summary>
public static class SpdReader
{
    /// <summary>Raw SPD images of the last <see cref="ReadAll"/> (for the --spd-dump diagnostic).</summary>
    public static List<(int Address, int Port, byte[] Raw)> LastRaw { get; } = new();

    public static List<SpdModule> ReadAll(MemGen mem)
    {
        LastRaw.Clear();
        using var bus = new Smbus();
        foreach (int port in mem == MemGen.DDR5 ? new[] { 2, 0 } : new[] { 0, 2 })
        {
            if (!bus.SelectPort(port)) continue;
            var found = new List<SpdModule>();
            for (byte addr = 0x50; addr <= 0x57; addr++)
            {
                if (!bus.ReadByteData(addr, 0, out _)) continue;
                var raw = mem == MemGen.DDR5 ? ReadDdr5(bus, addr) : ReadDdr4(bus, addr);
                if (raw == null) continue;
                LastRaw.Add((addr, port, raw));
                var mod = mem == MemGen.DDR5 ? SpdDecoder.DecodeDdr5(raw, addr) : SpdDecoder.DecodeDdr4(raw, addr);
                if (mod != null) found.Add(mod);
            }
            if (found.Count > 0) return found;
        }
        return new();
    }

    static byte[]? ReadDdr4(Smbus bus, byte addr)
    {
        var spd = new byte[512];
        try
        {
            for (int page = 0; page < 2; page++)
            {
                SetDdr4Page(bus, page);
                for (int i = 0; i < 256; i += 2)
                {
                    if (bus.ReadWordData(addr, (byte)i, out var w)) { spd[page * 256 + i] = (byte)w; spd[page * 256 + i + 1] = (byte)(w >> 8); }
                    else
                    {
                        if (!bus.ReadByteData(addr, (byte)i, out spd[page * 256 + i])) return null;
                        bus.ReadByteData(addr, (byte)(i + 1), out spd[page * 256 + i + 1]);
                    }
                }
            }
        }
        finally { SetDdr4Page(bus, 0); }
        return spd[2] == 0x0C ? spd : null;
    }

    static void SetDdr4Page(Smbus bus, int page)
    {
        byte spa = (byte)(page == 0 ? 0x36 : 0x37);
        // EE1004 may NACK the dummy byte; what matters is the page it ends up on (0x36 reads ACK on page 0)
        if (!bus.SendByte(spa, 0)) bus.WriteByteData(spa, 0, 0);
    }

    static byte[]? ReadDdr5(Smbus bus, byte addr)
    {
        var spd = new byte[1024];
        try
        {
            for (int page = 0; page < 8; page++)
            {
                if (!bus.ReadByteData(addr, 11, out var mr11)) return null;
                if (!bus.WriteByteData(addr, 11, (byte)((mr11 & 0x08) | page))) return null;
                for (int off = 0; off < 128; off += 2)
                {
                    byte reg = (byte)(0x80 | off);
                    if (off <= 126 && bus.ReadWordData(addr, reg, out var w)) { spd[page * 128 + off] = (byte)w; spd[page * 128 + off + 1] = (byte)(w >> 8); }
                    else
                    {
                        bus.ReadByteData(addr, reg, out spd[page * 128 + off]);
                        bus.ReadByteData(addr, (byte)(reg + 1), out spd[page * 128 + off + 1]);
                    }
                }
            }
        }
        finally
        {
            if (bus.ReadByteData(addr, 11, out var mr11)) bus.WriteByteData(addr, 11, (byte)(mr11 & 0x08));
        }
        return spd[2] == 0x12 ? spd : null;
    }
}

public static class SpdDecoder
{
    static readonly Dictionary<int, string> Vendors = new()
    {
        [0x80CE] = "Samsung", [0x80AD] = "SK hynix", [0x802C] = "Micron", [0x830B] = "Nanya", [0x8A91] = "CXMT",
        [0x04CD] = "G.Skill", [0x84CD] = "G.Skill", [0x029E] = "Corsair", [0x829E] = "Corsair", [0x0198] = "Kingston", [0x8198] = "Kingston",
        [0x059B] = "Crucial", [0x859B] = "Crucial", [0x04EF] = "TeamGroup", [0x84EF] = "TeamGroup", [0x04CB] = "ADATA", [0x84CB] = "ADATA",
        [0x0B32] = "Klevv", [0x8B32] = "Klevv",
    };

    static string Vendor(byte bank, byte id) =>
        Vendors.TryGetValue((bank << 8) | id, out var v) ? v : Vendors.TryGetValue(((bank & 0x7F) << 8) | id, out v) ? v : $"0x{bank:X2}{id:X2}";

    static string Ascii(byte[] b, int off, int len) => Encoding.ASCII.GetString(b, off, len).Replace('\0', ' ').Trim();
    static double Ns(int mtb, sbyte ftb) => mtb * 0.125 + ftb * 0.001;
    static int U16(byte[] b, int o) => b[o] | (b[o + 1] << 8);
    static char? Letter(byte v) => v is >= 0x41 and <= 0x5A ? (char)v : null;

    /// <summary>Snaps a measured data rate to the nearest common bin (2000/tCK is never exact).</summary>
    static int Bin(double mts, int[] bins) => bins.OrderBy(b => Math.Abs(b - mts)).First() is var b && Math.Abs(b - mts) < 25 ? b : (int)Math.Round(mts);
    static readonly int[] Ddr4Bins = { 1600, 1866, 2133, 2400, 2666, 2800, 2933, 3000, 3200, 3333, 3466, 3600, 3733, 3800, 3866, 4000, 4133, 4266, 4400, 4500, 4600, 4800, 5000, 5100, 5333 };
    static readonly int[] Ddr5Bins = { 4000, 4400, 4800, 5200, 5600, 6000, 6200, 6400, 6600, 6800, 7000, 7200, 7400, 7600, 7800, 8000, 8200, 8400, 8800, 9200 };

    public static SpdModule? DecodeDdr4(byte[] s, int addr)
    {
        if (s.Length < 512 || s[2] != 0x0C) return null;
        int density = (s[4] & 0x0F) switch { 2 => 1, 3 => 2, 4 => 4, 5 => 8, 6 => 16, 7 => 32, _ => 0 };
        int width = 4 << (s[12] & 0x07);
        int ranks = ((s[12] >> 3) & 0x07) + 1;
        int bus = 8 << (s[13] & 0x07);
        int capacity = density * bus / width * ranks / 8;
        var profiles = new List<SpdProfile>();

        // JEDEC base (MTB 0.125 ns + signed FTB in ps)
        double tck = Ns(s[18], (sbyte)s[125]);
        if (tck > 0)
            profiles.Add(new SpdProfile("JEDEC", Bin(2000 / tck, Ddr4Bins), 1.2, new ManualTimings
            {
                tCL = Ns(s[24], (sbyte)s[123]), tRCDWR = Ns(s[25], (sbyte)s[122]), tRCDRD = Ns(s[25], (sbyte)s[122]),
                tRP = Ns(s[26], (sbyte)s[121]), tRAS = (((s[27] & 0x0F) << 8) | s[28]) * 0.125,
                tRC = Ns(((s[27] >> 4) << 8) | s[29], (sbyte)s[120]), tRFC = U16(s, 30) * 0.125,
                tFAW = (((s[36] & 0x0F) << 8) | s[37]) * 0.125, tRRDS = Ns(s[38], (sbyte)s[119]), tRRDL = Ns(s[39], (sbyte)s[118]),
            }));

        // Intel XMP 2.0: header 384, profiles at 393 and 440 (47 bytes, fine offsets at the end)
        if (s[384] == 0x0C && s[385] == 0x4A)
            for (int p = 0; p < 2; p++)
            {
                if ((s[386] & (1 << p)) == 0) continue;
                int o = 393 + 47 * p;
                double pt = Ns(s[o + 3], (sbyte)s[o + 38]);
                if (pt <= 0.2) continue;
                double vdd = ((s[o] & 0x80) >> 7) + (s[o] & 0x7F) / 100.0;
                profiles.Add(new SpdProfile($"XMP {p + 1}", Bin(2000 / pt, Ddr4Bins), vdd, new ManualTimings
                {
                    tCL = Ns(s[o + 8], (sbyte)s[o + 37]), tRCDWR = Ns(s[o + 9], (sbyte)s[o + 36]), tRCDRD = Ns(s[o + 9], (sbyte)s[o + 36]),
                    tRP = Ns(s[o + 10], (sbyte)s[o + 35]), tRAS = (((s[o + 11] & 0x0F) << 8) | s[o + 12]) * 0.125,
                    tRC = Ns(((s[o + 11] >> 4) << 8) | s[o + 13], (sbyte)s[o + 34]), tRFC = U16(s, o + 14) * 0.125,
                    tFAW = (((s[o + 20] & 0x0F) << 8) | s[o + 21]) * 0.125, tRRDS = Ns(s[o + 22], (sbyte)s[o + 33]), tRRDL = Ns(s[o + 23], (sbyte)s[o + 32]),
                }));
            }

        string dram = Vendor(s[350], s[351]);
        char? die = Letter(s[352]);
        var (ic, certain) = IdentifyDdr4(dram, density, width, die, s[323], profiles);
        profiles.Reverse();   // XMP first
        return new SpdModule(addr, MemGen.DDR4, Vendor(s[320], s[321]), Ascii(s, 329, 20), dram, density, width, ranks, capacity, die, ic, certain, profiles);
    }

    public static SpdModule? DecodeDdr5(byte[] s, int addr)
    {
        if (s.Length < 1024 || s[2] != 0x12) return null;
        int density = (s[4] & 0x1F) switch { 1 => 4, 2 => 8, 3 => 12, 4 => 16, 5 => 24, 6 => 32, 7 => 48, 8 => 64, _ => 0 };
        int diesPerPkg = ((s[4] >> 5) & 0x07) switch { 0 => 1, 2 => 2, 3 => 4, 4 => 8, 5 => 16, _ => 1 };
        int width = 4 << ((s[6] >> 5) & 0x07);
        int ranks = ((s[234] >> 3) & 0x07) + 1;
        int subBus = 8 << (s[235] & 0x07), channels = ((s[235] >> 5) & 0x03) + 1;
        int capacity = diesPerPkg * density * subBus * channels / width * ranks / 8;
        var profiles = new List<SpdProfile>();

        SpdProfile? Decode(string name, int tckOff, int firstTimingOff, double? vdd)
        {
            int tckPs = U16(s, tckOff);
            if (tckPs is < 150 or > 1000) return null;
            int o = firstTimingOff;
            var t = new ManualTimings
            {
                tCL = U16(s, o) / 1000.0, tRCDWR = U16(s, o + 2) / 1000.0, tRCDRD = U16(s, o + 2) / 1000.0, tRP = U16(s, o + 4) / 1000.0,
                tRAS = U16(s, o + 6) / 1000.0, tRC = U16(s, o + 8) / 1000.0, tRFC = U16(s, o + 12),
            };
            return t.tCL > 5 && t.tRCDRD > 5 ? new SpdProfile(name, Bin(2_000_000.0 / tckPs, Ddr5Bins), vdd, t) : null;
        }

        if (Decode("JEDEC", 20, 30, 1.1) is { } jedec) profiles.Add(jedec);
        // AMD EXPO: "EXPO" at 832, profile 1 tCK at 846 / timings 848, profile 2 at 878 / 880; VDD code 842/844 (1.1 V + 5 mV steps)
        if (Ascii(s, 832, 4) == "EXPO")
            for (int p = 0; p < 2; p++)
                if (Decode($"EXPO {p + 1}", 846 + 32 * p, 848 + 32 * p, 1.1 + s[842 + 2 * p] * 0.005) is { } e) profiles.Add(e);
        // Intel XMP 3.0: 0x0C 0x4A at 640; profile bases 704/768 (64-byte layout) — fall back to the 32-byte layout some tools use
        if (s[640] == 0x0C && s[641] == 0x4A)
        {
            int n = 1;
            foreach (int b in new[] { 704, 768, 736 })
                if (n <= 3 && Decode($"XMP {n}", b + 5, b + 13, 1.1 + s[b] * 0.005) is { } x && !profiles.Any(q => q.Name.StartsWith("XMP") && q.MTs == x.MTs && q.Label == x.Label))
                { profiles.Add(x); n++; }
        }

        string dram = Vendor(s[552], s[553]);
        char? die = Letter(s[554]);
        var (ic, certain) = IdentifyDdr5(dram, density, die);
        var ordered = profiles.Where(p => p.Name != "JEDEC").Concat(profiles.Where(p => p.Name == "JEDEC")).ToList();
        return new SpdModule(addr, MemGen.DDR5, Vendor(s[512], s[513]), Ascii(s, 521, 30), dram, density, width, ranks, capacity, die, ic, certain, ordered);
    }

    /// <summary>DDR4 die → the 1.7.3 IC list. The SPD rarely names the die, so when the stepping byte is not a
    /// letter we infer it the way overclockers do: Samsung 8 Gb with XMP tRCD ≤ 9.6 ns is B-die; SK hynix
    /// 8 Gb made 2018+ is CJR/DJR; Micron 8 Gb from 2018+ is Rev. E. Uncertain guesses are flagged.</summary>
    public static (string? ic, bool certain) IdentifyDdr4(string dram, int density, int width, char? die, byte yearBcd, List<SpdProfile> profiles)
    {
        int year = 2000 + (yearBcd >> 4) * 10 + (yearBcd & 0x0F);
        var xmp = profiles.FirstOrDefault(p => p.Name.StartsWith("XMP"));
        switch (dram)
        {
            case "Samsung":
                if (die == 'B' && density == 8) return ("samsung-b", true);
                if (die is 'D' or 'E') return ("samsung-de", true);
                if (density == 4) return ("samsung-s", false);
                // B-die is the only Samsung 8 Gb die binned with tRCD under ~9.6 ns (3200 14-14-14, 3600 16-16-16,
                // 4400 17-18-18...); D/E-die kits sit at 10.5 ns and up (3600 16-19-19, 3200 16-18-18)
                if (density == 8 && xmp != null && xmp.Timings.tRCDRD <= 9.6) return ("samsung-b", false);
                if (density == 8 && xmp == null) return ("samsung-oem", false);
                return ("samsung-de", false);
            case "SK hynix":
                if (die is 'C' or 'D' && density == 8) return ("hynix-cjr", true);
                if (die == 'A') return ("hynix-afr", true);
                if (die == 'M') return ("hynix-mfr", true);
                return (year >= 2018 || density >= 16 ? "hynix-cjr" : "hynix-afr", false);
            case "Micron":
                if (die is 'E' or 'H' or 'R' or 'J') return ("micron-e", true);
                if (die == 'B') return ("micron-b", true);
                if (die == 'A') return ("micron-a", true);
                if (die == 'D') return ("micron-d", true);
                return (year >= 2018 || density >= 16 ? "micron-e" : "micron-b", false);
            default:
                return (null, false);
        }
    }

    /// <summary>DDR5: the stepping byte is the die letter in ASCII on SK hynix/Samsung/Micron (0x41 'A', 0x4D 'M').</summary>
    public static (string? ic, bool certain) IdentifyDdr5(string dram, int density, char? die) => dram switch
    {
        "SK hynix" when density == 16 => (die == 'A' ? "ddr5-hynix-a16" : "ddr5-hynix-m16", die is 'A' or 'M'),
        "SK hynix" when density == 24 => ("ddr5-hynix-m24", true),
        "SK hynix" when density >= 32 => ("ddr5-hynix-32", true),
        "Samsung" when density == 16 => ("ddr5-samsung-b16", die == 'B'),
        "Samsung" => ("ddr5-samsung-24", true),
        "Micron" when density == 16 => ("ddr5-micron-16", true),
        "Micron" => ("ddr5-micron-24", true),
        _ => ("ddr5-generic", false),
    };

    public static string Describe(SpdModule m) =>
        string.Create(CultureInfo.InvariantCulture, $"{m.DramVendor} {m.DensityGbit}Gb x{m.Width} {m.DieText}");
}
