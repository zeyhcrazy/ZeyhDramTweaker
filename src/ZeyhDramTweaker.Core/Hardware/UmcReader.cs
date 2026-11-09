using System.Globalization;
using ZeyhDramTweaker.Core.Model;

namespace ZeyhDramTweaker.Core.Hardware;

public sealed class CurrentMemory
{
    public MemGen Mem { get; init; }
    public int MTs { get; init; }
    public int Ranks { get; init; }
    public int Dimms { get; init; }
    public int Channel { get; init; }
    /// <summary>Keyed by the row names the result sections use (tCL, tRCDRD, ..., Gear Down Mode).</summary>
    public Dictionary<string, string> Values { get; } = new();
    /// <summary>What each extra source returned (diagnostics, shown in the dump).</summary>
    public List<string> Log { get; } = new();
}

/// <summary>
/// Reads the active timings from the Unified Memory Controller registers over SMN (offset 0x50000 + channel
/// &lt;&lt; 20). The DDR4 layout is the same one the 1.7.3 read through WinRing0 (PCI 0:0.0 0xB8/0xBC); the
/// DDR5 layout differs for GDM/2T, the clock ratio and the refresh registers. Read-only: nothing is written.
/// </summary>
public static class UmcReader
{
    // (register, name, hi, lo) — names match the result rows
    static readonly (uint reg, string name, int hi, int lo)[] Common =
    {
        (0x50204, "tRCDWR", 29, 24), (0x50204, "tRCDRD", 21, 16), (0x50204, "tRAS", 14, 8), (0x50204, "tCL", 5, 0),
        (0x50208, "tRP", 21, 16), (0x50208, "tRC", 7, 0),
        (0x5020C, "tRTP", 28, 24), (0x5020C, "tRRDL", 12, 8), (0x5020C, "tRRDS", 4, 0),
        (0x50210, "tFAW", 7, 0),
        (0x50214, "tWTRL", 22, 16), (0x50214, "tWTRS", 12, 8), (0x50214, "tCWL", 5, 0),
        (0x50218, "tWR", 7, 0),
        (0x50220, "tRDRDSCL", 29, 24), (0x50220, "tRDRDSC", 19, 16), (0x50220, "tRDRDSD", 11, 8), (0x50220, "tRDRDDD", 3, 0),
        (0x50224, "tWRWRSCL", 29, 24), (0x50224, "tWRWRSC", 19, 16), (0x50224, "tWRWRSD", 11, 8), (0x50224, "tWRWRDD", 3, 0),
        (0x50228, "tRDWR", 13, 8), (0x50228, "tWRRD", 3, 0),
        (0x50254, "tCKE", 28, 24),
    };

    static uint Bits(uint v, int hi, int lo) => (v >> lo) & (uint)((1L << (hi - lo + 1)) - 1);

    public static CurrentMemory Read(MemGen mem)
    {
        var cur = ReadController(mem);
        // voltages / termination / CAD bus from the BIOS (AOD), clocks and SoC rails from the SMU — after the SMN handle is closed
        LiveExtras.Fill(cur, cur.Log);
        return cur;
    }

    static CurrentMemory ReadController(MemGen mem)
    {
        using var io = PawnIo.OpenWithModule("AMDFamily17.bin");
        uint Smn(uint addr) => (uint)io.Execute("ioctl_read_smn", new ulong[] { addr }, 1).FirstOrDefault();

        int channel = -1, dimms = 0, ranks = 1;
        for (int ch = 0; ch < 12; ch++)
        {
            uint b = (uint)ch << 20;
            bool present = Bits(Smn(b | 0x50DF0), 19, 19) == 0;
            bool cs0 = (Smn(b | 0x50000) & 1) == 1, cs1 = (Smn(b | 0x50008) & 1) == 1;
            if (!present || !(cs0 || cs1)) continue;
            if (channel < 0) channel = ch;
            dimms += (cs0 ? 1 : 0) + (cs1 ? 1 : 0);
            if (channel == ch && IsDualRank(Smn, b, mem, cs0)) ranks = 2;
        }
        if (channel < 0) throw new InvalidOperationException("umc.nochannel");
        uint o = (uint)channel << 20;

        var cur = new CurrentMemory
        {
            Mem = mem,
            Channel = channel,
            Ranks = ranks,
            Dimms = dimms,
            MTs = mem == MemGen.DDR4
                ? (int)Math.Round(Bits(Smn(o | 0x50200), 6, 0) / 3.0 * 200)
                : (int)Math.Round(Bits(Smn(o | 0x50200), 15, 0) / 100.0 * 200),
        };
        var v = cur.Values;
        string S(uint x) => x.ToString(CultureInfo.InvariantCulture);
        foreach (var (reg, name, hi, lo) in Common) v[name] = S(Bits(Smn(o | reg), hi, lo));

        uint cfg = Smn(o | 0x50200);
        bool gdm = mem == MemGen.DDR4 ? Bits(cfg, 11, 11) == 1 : Bits(cfg, 18, 18) == 1;
        bool t2 = mem == MemGen.DDR4 ? Bits(cfg, 10, 10) == 1 : Bits(cfg, 17, 17) == 1;
        v["Gear Down Mode"] = gdm ? "Enabled" : "Disabled";
        v["Command Rate"] = t2 ? "2T" : "1T";
        v["Power Down Mode"] = Bits(Smn(o | 0x5012C), 28, 28) == 1 ? "Enabled" : "Disabled";

        if (mem == MemGen.DDR4)
        {
            uint a = Smn(o | 0x50260), b2 = Smn(o | 0x50264);
            uint rfc = a == b2 ? a : (a != 0x21060138 ? a : b2);
            if (rfc != 0) v["tRFC"] = S(Bits(rfc, 10, 0));
        }
        else
        {
            foreach (uint r in new uint[] { 0x50260, 0x50264, 0x50268, 0x5026C })
            {
                uint x = Smn(o | r);
                if (x == 0x00C00138 || x == 0) continue;
                v["tRFC"] = S(Bits(x, 15, 0)); v["tRFC2"] = S(Bits(x, 31, 16));
                break;
            }
            foreach (uint r in new uint[] { 0x502C0, 0x502C4, 0x502C8, 0x502CC })
            {
                uint x = Bits(Smn(o | r), 10, 0);
                if (x != 0) { v["tRFCsb"] = S(x); break; }
            }
            v["tREFI"] = S(Bits(Smn(o | 0x50230), 15, 0));
        }
        // DDR4 shows tRDRDSD/DD and tWRWRSD/DD separately; DDR5 results merge them
        v["tRDRDSD / DD"] = $"{v["tRDRDSD"]} / {v["tRDRDDD"]}";
        v["tWRWRSD / DD"] = $"{v["tWRWRSD"]} / {v["tWRWRDD"]}";

        LiveExtras.FillBgs(cur, o, Smn);
        return cur;
    }

    static bool IsDualRank(Func<uint, uint> smn, uint b, MemGen mem, bool cs0)
    {
        if (mem == MemGen.DDR4) return Bits(smn(b | (cs0 ? 0x50080u : 0x50084u)), 0, 0) == 1;
        foreach (uint a in cs0 ? new uint[] { 0x50020, 0x50024 } : new uint[] { 0x50028, 0x5002C })
        {
            uint x = smn(b | a);
            if (x != 0 && (x == 0x07FFFFFE || Bits(x, 10, 9) < 3)) return true;
        }
        return false;
    }
}
