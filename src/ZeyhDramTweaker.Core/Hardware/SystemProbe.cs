using System.Management;
using System.Runtime.Intrinsics.X86;
using System.Security.Principal;
using ZeyhDramTweaker.Core.Model;

namespace ZeyhDramTweaker.Core.Hardware;

public sealed record CpuId(string Vendor, int Family, int Model, int Stepping, string Name)
{
    public bool IsAmd => Vendor == "AuthenticAMD";
}

public sealed record DimmInfo(string Manufacturer, string PartNumber, ulong CapacityBytes, int ConfiguredMTs, string Slot);

/// <summary>Things we can learn without the driver: CPUID and WMI.</summary>
public static class SystemProbe
{
    public static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static CpuId ReadCpuId()
    {
        if (!X86Base.IsSupported) return new CpuId("?", 0, 0, 0, WmiCpuName());
        var (_, b, c, d) = X86Base.CpuId(0, 0);
        var vendor = string.Concat(new[] { b, d, c }.Select(r => System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(r))));
        var (eax, _, _, _) = X86Base.CpuId(1, 0);
        int baseFam = (eax >> 8) & 0xF, extFam = (eax >> 20) & 0xFF;
        int family = baseFam == 0xF ? baseFam + extFam : baseFam;
        int model = ((eax >> 4) & 0xF) | (baseFam == 0xF ? ((eax >> 12) & 0xF0) : 0);
        return new CpuId(vendor, family, model, eax & 0xF, WmiCpuName());
    }

    static string WmiCpuName()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (var o in s.Get()) return ((string)o["Name"]).Trim();
        }
        catch { }
        return "";
    }

    /// <summary>Maps a Zen CPU (family/model) to the calculator platform. Null for unknown/non-AMD.</summary>
    public static Platform? PlatformOf(CpuId c)
    {
        if (!c.IsAmd) return null;
        bool tr = c.Name.Contains("Threadripper", StringComparison.OrdinalIgnoreCase);
        return (c.Family, c.Model) switch
        {
            (0x17, 0x01) => tr ? Platform.Threadripper1 : Platform.Zen1,
            (0x17, 0x11) => Platform.Zen1,                                   // Raven Ridge APU
            (0x17, 0x08) => tr ? (c.Name.Contains("WX") ? Platform.Threadripper2WX : Platform.ThreadripperPlus) : Platform.ZenPlus,
            (0x17, 0x18) => Platform.ZenPlus,                                // Picasso APU
            (0x17, 0x31 or 0x71) => Platform.Zen2,                           // Castle Peak / Matisse
            (0x17, 0x60 or 0x68) => Platform.Zen3Apu,                        // Renoir / Lucienne: monolithic, FCLK like Cezanne
            (0x19, 0x21) => Platform.Zen3,                                   // Vermeer (incl. 5800X3D)
            (0x19, 0x50) => Platform.Zen3Apu,                                // Cezanne
            (0x19, 0x61) => Platform.Zen4,                                   // Raphael (incl. X3D)
            (0x19, 0x74 or 0x75 or 0x78 or 0x7C) => Platform.Zen4Apu,        // Phoenix / Phoenix 2 / Hawk Point
            (0x1A, 0x44) => Platform.Zen5,                                   // Granite Ridge (incl. X3D)
            (0x1A, _) => Platform.Zen5,
            (0x19, >= 0x40) => Platform.Zen4,
            (0x19, _) => Platform.Zen3,
            (0x17, _) => Platform.Zen2,
            _ => null,
        };
    }

    /// <summary>"ASUSTeK COMPUTER INC. PRIME X570-PRO, BIOS 5044" (WMI, no admin), or "" when WMI has nothing.</summary>
    public static string ReadBoard()
    {
        try
        {
            string maker = "", product = "", bios = "";
            using (var s = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
                foreach (var o in s.Get()) { maker = ((o["Manufacturer"] as string) ?? "").Trim(); product = ((o["Product"] as string) ?? "").Trim(); break; }
            using (var s = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion FROM Win32_BIOS"))
                foreach (var o in s.Get()) { bios = ((o["SMBIOSBIOSVersion"] as string) ?? "").Trim(); break; }
            maker = maker.Replace("ASUSTeK COMPUTER INC.", "ASUS").Replace("Micro-Star International Co., Ltd.", "MSI").Replace("Gigabyte Technology Co., Ltd.", "Gigabyte");
            var name = string.Join(" ", new[] { maker, product }.Where(x => x.Length > 0));
            return bios.Length > 0 && name.Length > 0 ? $"{name}, BIOS {bios}" : name;
        }
        catch { return ""; }
    }

    public static List<DimmInfo> ReadDimms()
    {
        var list = new List<DimmInfo>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, PartNumber, Capacity, ConfiguredClockSpeed, DeviceLocator FROM Win32_PhysicalMemory");
            foreach (var o in s.Get())
                list.Add(new DimmInfo(((o["Manufacturer"] as string) ?? "").Trim(), ((o["PartNumber"] as string) ?? "").Trim(),
                    o["Capacity"] is ulong cap ? cap : 0, o["ConfiguredClockSpeed"] is uint mhz ? (int)mhz : 0,
                    ((o["DeviceLocator"] as string) ?? "").Trim()));
        }
        catch { }
        return list;
    }
}
