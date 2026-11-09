using System.Globalization;
using System.Management;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using ZeyhDramTweaker.Core.Model;

namespace ZeyhDramTweaker.Core.Hardware;

/// <summary>
/// "AMD Overdrive" interface the BIOS publishes for Ryzen Master: the AMD_ACPI WMI class (root\wmi) backed by the
/// "AMD AOD" SSDT. Its RunCommand(5) handler (AM05) answers "Get APCB Config" by asking SMM to refresh the AODT
/// region and returning a copy of it (OBUF): memory clock, timings, VDDIO, VTT, VPP, ProcODT, RTT, CAD bus,
/// CLDO VDDP/VDDG, FCLK... as configured in the BIOS. Only the two "Get" commands are ever sent.
/// The OBUF layout is read from the SSDT itself (CreateXxxField(OBUF, offset, Bxxx) + Store(AODT field, Bxxx)),
/// so it follows the BIOS it runs on.
/// </summary>
public static class AodReader
{
    public sealed record Snapshot(Dictionary<string, long> Fields, Dictionary<string, uint> Functions, byte[] Raw, byte[]? RawVoltages);

    public static Snapshot? Read()
    {
        var layout = Layout();
        if (layout.Count == 0) return null;
        using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM AMD_ACPI");
        using var mo = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
        if (mo == null) return null;

        var functions = new Dictionary<string, uint>();
        foreach (var method in new[] { "GetObjectID", "GetObjectID2" })
        {
            try
            {
                var outp = mo.InvokeMethod(method, null, null);
                if (outp?["pack"] is not ManagementBaseObject pack) continue;
                var ids = (uint[])pack["ID"];
                var names = (string[])pack["IDString"];
                int n = Convert.ToInt32(pack["Length"]);
                for (int i = 0; i < Math.Min(n, Math.Min(ids.Length, names.Length)); i++)
                    if (!string.IsNullOrEmpty(names[i])) functions.TryAdd(names[i], ids[i]);
            }
            catch (ManagementException) { }
        }
        if (!functions.TryGetValue("Get APCB Config", out var getApcb)) return null;

        var raw = Run(mo, getApcb);
        if (raw == null) return null;
        byte[]? volt = functions.TryGetValue("Get memory voltages", out var getV) ? Run(mo, getV) : null;

        var fields = new Dictionary<string, long>();
        foreach (var (name, (off, size)) in layout)
        {
            if (off + size > raw.Length) continue;
            long v = 0;
            for (int i = size - 1; i >= 0; i--) v = (v << 8) | raw[off + i];
            fields[name] = v;
        }
        return new Snapshot(fields, functions, raw, volt);
    }

    static byte[]? Run(ManagementObject mo, uint command)
    {
        try
        {
            var inp = mo.GetMethodParameters("RunCommand");
            var buf = new byte[8];
            BitConverter.GetBytes(command).CopyTo(buf, 0);
            inp["Inbuf"] = buf;
            var outp = mo.InvokeMethod("RunCommand", inp, null);
            return (outp?["Outbuf"] as ManagementBaseObject)?["Result"] as byte[];
        }
        catch (ManagementException) { return null; }
    }

    /// <summary>AODT field name → (offset, size) inside the returned buffer, parsed from the AOD SSDT.</summary>
    public static Dictionary<string, (int off, int size)> Layout()
    {
        var aml = FindAodTable();
        var result = new Dictionary<string, (int, int)>();
        if (aml == null) return result;
        var obuf = new Dictionary<string, (int off, int size)>();
        for (int i = 0; i + 10 < aml.Length; i++)
        {
            int size = aml[i] switch { 0x8A => 4, 0x8B => 2, 0x8C => 1, 0x8F => 8, _ => 0 };
            if (size == 0 || aml[i + 1] != 'O' || aml[i + 2] != 'B' || aml[i + 3] != 'U' || aml[i + 4] != 'F') continue;
            int p = i + 5, off;
            switch (aml[p])
            {
                case 0x00: off = 0; p += 1; break;
                case 0x01: off = 1; p += 1; break;
                case 0x0A: off = aml[p + 1]; p += 2; break;
                case 0x0B: off = aml[p + 1] | aml[p + 2] << 8; p += 3; break;
                default: continue;
            }
            if (p + 4 > aml.Length || !IsSeg(aml, p)) continue;
            obuf[Encoding.ASCII.GetString(aml, p, 4)] = (off, size);
        }
        // Store(<AODT field>, <OBUF field>)
        for (int i = 0; i + 9 <= aml.Length; i++)
        {
            if (aml[i] != 0x70 || !IsSeg(aml, i + 1) || !IsSeg(aml, i + 5)) continue;
            var dst = Encoding.ASCII.GetString(aml, i + 5, 4);
            if (obuf.TryGetValue(dst, out var at)) result.TryAdd(Encoding.ASCII.GetString(aml, i + 1, 4), at);
        }
        return result;
    }

    static bool IsSeg(byte[] a, int p)
    {
        if (p + 4 > a.Length || !(a[p] is >= (byte)'A' and <= (byte)'Z' or (byte)'_')) return false;
        for (int k = 1; k < 4; k++) if (!(a[p + k] is >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'_')) return false;
        return true;
    }

    /// <summary>Windows copies every ACPI table to HKLM\HARDWARE\ACPI; the AOD one is an SSDT with an AODT/AODE region.</summary>
    public static byte[]? FindAodTable()
    {
        using var acpi = Registry.LocalMachine.OpenSubKey(@"HARDWARE\ACPI");
        if (acpi == null) return null;
        foreach (var name in acpi.GetSubKeyNames().Where(n => n.StartsWith("SSD", StringComparison.OrdinalIgnoreCase)))
            if (Walk(acpi.OpenSubKey(name)) is { } t) return t;
        return null;

        static byte[]? Walk(RegistryKey? k)
        {
            if (k == null) return null;
            using (k)
            {
                foreach (var v in k.GetValueNames())
                    if (k.GetValue(v) is byte[] b && Contains(b, "OBUF") && (Contains(b, "AODT") || Contains(b, "AODE"))) return b;
                foreach (var s in k.GetSubKeyNames())
                    if (Walk(k.OpenSubKey(s)) is { } r) return r;
            }
            return null;
        }
        static bool Contains(byte[] b, string s) => Encoding.ASCII.GetString(b).Contains(s, StringComparison.Ordinal);
    }
}

/// <summary>SMU metrics table ("PM table") through the official RyzenSMU PawnIO module: measured FCLK/UCLK/MCLK and
/// the SoC rails (VDDCR_SOC, CLDO VDDP, VDDG IOD/CCD). Offsets per table version are public knowledge (ZenStates,
/// Ryzen Master Monitoring SDK dumps); unknown versions fall back to the family layout.</summary>
public static class SmuMetrics
{
    public sealed record Values(uint Version, float Fclk, float Uclk, float Mclk, float Vsoc, float Vddp, float VddgIod, float VddgCcd);

    record Def(int Size, int Fclk, int Uclk, int Mclk, int Vsoc, int Vddp, int Iod, int Ccd);

    static Def? DefFor(uint v)
    {
        switch (v)
        {
            // Matisse (Zen 2)
            case 0x240902: return new(1300, 188, 196, 200, 176, 496, 500, -1);
            case 0x240802: return new(2016, 188, 196, 200, 176, 496, 500, -1);
            case 0x240903: return new(1304, 192, 200, 204, 180, 500, 504, -1);
            case 0x240803 or 0x240703: return new(2020, 192, 200, 204, 180, 500, 504, -1);
            case 0x240503: return new(2736, 192, 200, 204, 180, 500, 504, -1);
            case 0x240403: return new(3452, 192, 200, 204, 180, 500, 504, -1);
            // Vermeer (Zen 3)
            case 0x380005: return new(7088, 192, 200, 204, 180, 548, 552, 556);
            case 0x380505: return new(3888, 192, 200, 204, 180, 548, 552, 556);
            case 0x380605: return new(3088, 192, 200, 204, 180, 548, 552, 556);
            case 0x380705 or 0x380805: return new(2288, 192, 200, 204, 180, 548, 552, 556);
            case 0x380804: return new(2212, 192, 200, 204, 180, 548, 552, 556);
            case 0x380904: return new(1444, 192, 200, 204, 180, 548, 552, 556);
            case 0x380905: return new(1488, 192, 200, 204, 180, 548, 552, 556);
            // Renoir / Cezanne APUs
            case 0x370005: return new(2240, 1512, 1516, 1520, 408, 2156, -1, -1);
            case 0x400005: return new(2372, 1636, 1640, 1644, 412, 2260, -1, -1);
            // Raphael (Zen 4)
            case 0x540208: return new(2256, 284, 300, 316, 212, 1076, -1, -1);
        }
        return (v >> 16) switch
        {
            0x24 => (v & 7) switch { 0 => new(2020, 176, 184, 188, 164, 484, 488, -1), 1 or 2 or 4 => new(2020, 188, 196, 200, 176, 496, 500, -1), _ => new(2020, 192, 200, 204, 180, 500, 504, -1) },
            0x38 => new(2376, 192, 200, 204, 180, 548, 552, 556),
            0x37 => new(2240, 1512, 1516, 1520, 408, 2156, -1, -1),
            0x40 => new(2376, 1636, 1640, 1644, 412, 2260, -1, -1),
            0x54 => new(2376, 280, 296, 312, 208, 1072, -1, -1),
            0x62 => new(2452, 284, 300, 316, 332, 1076, 1036, 1044),
            _ => null,
        };
    }

    public static Values? Read()
    {
        using var pci = new GlobalLock(@"Global\Access_PCI");
        using var io = PawnIo.OpenWithModule("RyzenSMU.bin");
        ulong[]? r = null;
        for (int attempt = 0; attempt < 4 && r == null; attempt++)
        {
            r = io.TryExecute("ioctl_resolve_pm_table", new ulong[2], 2);
            if (r == null) { io.TryExecute("ioctl_update_pm_table", Array.Empty<ulong>(), 0); Thread.Sleep(50); }
        }
        if (r == null || r.Length < 2) throw new PawnIoException("pawnio.exec", new InvalidOperationException("ioctl_resolve_pm_table"));
        uint version = (uint)r[0];
        var def = DefFor(version);
        if (def == null) return new Values(version, 0, 0, 0, 0, 0, 0, 0);
        int q = (def.Size + 7) / 8;
        float[] t = Array.Empty<float>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            io.Execute("ioctl_update_pm_table", Array.Empty<ulong>(), 0);
            Thread.Sleep(20);
            var raw = io.Execute("ioctl_read_pm_table", new ulong[q], q);
            t = new float[raw.Length * 2];
            Buffer.BlockCopy(raw, 0, t, 0, raw.Length * 8);
            if (t.Length > 0 && Get(t, def.Fclk) > 0) break;
        }
        return new Values(version, Get(t, def.Fclk), Get(t, def.Uclk), Get(t, def.Mclk), Get(t, def.Vsoc), Get(t, def.Vddp), Get(t, def.Iod), Get(t, def.Ccd));
    }

    static float Get(float[] t, int byteOffset) => byteOffset >= 0 && byteOffset / 4 < t.Length ? t[byteOffset / 4] : 0f;
}

/// <summary>Named machine-wide mutex shared with other hardware tools (HWiNFO, ZenTimings, LibreHardwareMonitor).</summary>
public sealed class GlobalLock : IDisposable
{
    readonly Mutex? _m;
    readonly bool _owned;
    public GlobalLock(string name, int timeoutMs = 2000)
    {
        try
        {
            _m = new Mutex(false, name);
            _owned = _m.WaitOne(timeoutMs);
        }
        catch (AbandonedMutexException) { _owned = true; }
        catch (UnauthorizedAccessException) { _m = null; }
    }
    public void Dispose()
    {
        if (_owned) try { _m?.ReleaseMutex(); } catch (ApplicationException) { }
        _m?.Dispose();
    }
}

/// <summary>Puts BIOS (AOD) and measured (SMU) values under the result row names, next to the controller timings.</summary>
public static class LiveExtras
{
    static string F(double v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);

    // JEDEC MR1/MR5 RTT_NOM / RTT_PARK and MR2 RTT_WR codes, written the way the 1.7.3 tables write them
    static readonly string[] RttNomPark = { "OFF", "(60)RZQ/4", "(120)RZQ/2", "(40)RZQ/6", "(240)RZQ/1", "(48)RZQ/5", "(80)RZQ/3", "(34)RZQ/7" };
    static readonly string[] RttWr = { "OFF", "(120)RZQ/2", "(240)RZQ/1", "Hi-Z", "(80)RZQ/3" };

    /// <summary>AGESA ProcODT code → ohms.</summary>
    public static string? ProcOdt(long code) => code switch
    {
        0 => "Hi-Z", 1 => "480", 2 => "240", 3 => "160", 8 => "120", 9 => "96", 10 => "80", 11 => "68.6",
        24 => "60", 25 => "53.3", 26 => "48", 27 => "43.6", 56 => "40", 57 => "36.9", 58 => "34.3", 59 => "32", 62 => "30", 63 => "28.2",
        _ => null,
    };

    /// <summary>AGESA CAD bus drive strength code → ohms.</summary>
    public static string? CadDrive(long code) => code switch { 0 => "120", 1 => "60", 3 => "40", 7 => "30", 15 => "24", 31 => "20", _ => null };

    /// <summary>BankGroupSwap: the address-hash registers keep their reset pattern when BGS is off; BGS Alt lives in 0xD0/0xD4.</summary>
    public static void FillBgs(CurrentMemory cur, uint umcBase, Func<uint, uint> smn)
    {
        var v = cur.Values;
        {
            try
            {
                uint h0 = smn(umcBase | 0x50050), h1 = smn(umcBase | 0x50058), a0 = smn(umcBase | 0x500D0), a1 = smn(umcBase | 0x500D4);
                v["BankGroupSwap"] = h0 != 0x87654321 || h1 != 0x87654321 ? "Enabled" : "Disabled";
                v["BankGroupSwap Alt"] = ((a0 >> 4) & 0x7F) != 0 || ((a1 >> 4) & 0x7F) != 0 ? "Enabled" : "Disabled";
            }
            catch (Exception ex) { cur.Log.Add("bgs: " + ex.Message); }
        }
    }

    public static void Fill(CurrentMemory cur, List<string> log)
    {
        var v = cur.Values;
        bool ddr4 = cur.Mem == MemGen.DDR4;
        try
        {
            if (AodReader.Read() is { } a)
            {
                var f = a.Fields;
                long G(string n) => f.TryGetValue(n, out var x) ? x : -1;
                if (ddr4)
                {
                    v["DRAM (VDIMM)"] = G("VDIO") is > 800 and < 2000 and var vio ? F(vio / 1000.0, "0.00#") : "—";
                    if (G("VMTT") is > 300 and < 1000 and var vtt) v["VTT DDR min"] = v["VTT DDR max"] = F(vtt / 1000.0, "0.00#");
                    if (G("VMPP") is > 2000 and < 3000 and var vpp) v["VPP"] = F(vpp / 1000.0, "0.00#");
                    if (ProcOdt(G("PODT")) is { } odt) v["ProcODT"] = odt;
                    if (G("RTNM") is >= 0 and < 8 and var nom) v["RTT_NOM"] = RttNomPark[nom];
                    if (G("RTWR") is >= 0 and < 5 and var wr) v["RTT_WR"] = RttWr[wr];
                    if (G("RTPK") is >= 0 and < 8 and var pk) v["RTT_PARK"] = RttNomPark[pk];
                    foreach (var (fld, row) in new[] { ("CBCK", "ClkDrvStren"), ("CBAC", "AddrCmdDrvStren"), ("CBCO", "CsOdtDrvStren"), ("CBCE", "CkeDrvStren") })
                        if (CadDrive(G(fld)) is { } ds) v[row] = ds;
                    foreach (var (fld, row) in new[] { ("ACSP", "AddrCmdSetup"), ("COSP", "CsOdtSetup"), ("CESP", "CkeSetup") })
                        if (G(fld) is >= 0 and < 64 and var su) v[row] = su.ToString(CultureInfo.InvariantCulture);
                }
                // CLDO rails and FCLK as set in the BIOS; the SMU readings below replace them when available
                if ((G("CDVP") & 0xFFFF) is > 500 and < 1600 and var vddp) v["cLDO VDDP"] = F(vddp / 1000.0, "0.000");
                if (G("CVDG") is > 500 and < 1600 and var vddg) v["VDDG CCD"] = F(vddg / 1000.0, "0.000");
                if (G("IVDG") is > 500 and < 1600 and var iod) v["VDDG IOD"] = F(iod / 1000.0, "0.000");
                if (G("FCLK") is > 300 and < 4000 and var fclk) v["FCLK"] = fclk.ToString(CultureInfo.InvariantCulture);
                log.Add($"aod: {a.Fields.Count} fields, {a.Functions.Count} functions");
            }
            else log.Add("aod: not available");
        }
        catch (Exception ex) { log.Add("aod: " + ex.Message); }

        try
        {
            if (SmuMetrics.Read() is { } m)
            {
                log.Add($"smu: table 0x{m.Version:X6} fclk {m.Fclk:0} uclk {m.Uclk:0} mclk {m.Mclk:0} vsoc {m.Vsoc:0.000} vddp {m.Vddp:0.000} iod {m.VddgIod:0.000} ccd {m.VddgCcd:0.000}");
                // FCLK moves in 33.3 MHz steps; the SMU reading carries the BCLK error (1903 on a 100.15 MHz BCLK)
                if (m.Fclk > 300) v["FCLK"] = F(Math.Round(Math.Round(m.Fclk / (100.0 / 3)) * (100.0 / 3)), "0") + (ddr4 ? "" : " MHz");
                if (m.Vsoc is > 0.6f and < 1.6f) v[ddr4 ? "SOC (VSOC)" : "VSOC"] = F(m.Vsoc, "0.000");
                if (m.Vddp is > 0.5f and < 1.6f) v[ddr4 ? "cLDO VDDP" : "CPU VDDP"] = F(m.Vddp, "0.000");
                if (m.VddgIod is > 0.5f and < 1.6f) v["VDDG IOD"] = F(m.VddgIod, "0.000");
                if (m.VddgCcd is > 0.5f and < 1.6f) v["VDDG CCD"] = F(m.VddgCcd, "0.000");
                if (!ddr4 && m.VddgCcd > 0.5f && m.VddgIod > 0.5f) v["VDDG CCD / IOD"] = $"{F(m.VddgCcd, "0.000")} / {F(m.VddgIod, "0.000")}";
                if (!ddr4 && m.Uclk > 300 && m.Mclk > 300) v["UCLK DIV1 MODE"] = Math.Abs(m.Uclk - m.Mclk) < 50 ? "UCLK=MEMCLK (1:1)" : "UCLK=MEMCLK/2 (2:1)";
            }
        }
        catch (Exception ex) { log.Add("smu: " + ex.Message); }
    }
}
