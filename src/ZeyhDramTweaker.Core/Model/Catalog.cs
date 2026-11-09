namespace ZeyhDramTweaker.Core.Model;

public enum MemGen { DDR4, DDR5 }

public enum Preset { Safe, Fast }

/// <summary>CPU families the calculator knows. The first six map 1:1 to the CPU list of the
/// original 1.7.3 (same order = original SelectedIndex).</summary>
public enum Platform
{
    Zen1, ZenPlus, Threadripper1, ThreadripperPlus, Threadripper2WX, Zen2,
    Zen3, Zen3Apu,
    Zen4, Zen4Apu, Zen5,
}

public sealed record PlatformInfo(
    Platform Id,
    string NameKey,
    MemGen Mem,
    int LegacyCpuIndex,      // CPU index used for the original tables (Zen 3 reuses Zen 2 = 5)
    bool Apu,
    int[] Frequencies);

public sealed record MemoryIc(
    string Id,
    string Name,
    MemGen Mem,
    int LegacyIndex,         // original MT index (DDR4) or -1
    int DensityGbit);        // DDR5: die density, drives JEDEC tRFC fallback

/// <summary>DDR4: PCB revision / bin quality as in 1.7.3. DDR5: standard per-IC or manual (EXPO/XMP ns).</summary>
public enum Profile { A2B2 = 0, A0B0 = 1, BadBin = 2, Manual = 3, Standard = 10 }

public sealed record Topology(string NameKey, int LegacyIndex);

public static class Catalog
{
    public static readonly int[] Ddr4Axis =
    {
        2666, 2733, 2800, 2866, 2933, 3000, 3066, 3133, 3200, 3266, 3333, 3400, 3466, 3533, 3600, 3666,
        3733, 3800, 3866, 3933, 4000, 4066, 4133, 4200, 4266, 4333, 4400, 4466, 4533, 4600,
    };

    static readonly int[] Ddr4Apu = Ddr4Axis.Concat(new[] { 4666, 4733, 4800 }).ToArray();

    public static readonly int[] Ddr5Axis =
    {
        4800, 5200, 5600, 6000, 6200, 6400, 6600, 6800, 7000, 7200, 7400, 7600, 7800, 8000, 8200, 8400,
    };

    public static readonly IReadOnlyList<PlatformInfo> Platforms = new[]
    {
        new PlatformInfo(Platform.Zen5, "plat.zen5", MemGen.DDR5, -1, false, Ddr5Axis),
        new PlatformInfo(Platform.Zen4, "plat.zen4", MemGen.DDR5, -1, false, Ddr5Axis),
        new PlatformInfo(Platform.Zen4Apu, "plat.zen4apu", MemGen.DDR5, -1, true, Ddr5Axis),
        new PlatformInfo(Platform.Zen3, "plat.zen3", MemGen.DDR4, 5, false, Ddr4Axis),
        new PlatformInfo(Platform.Zen3Apu, "plat.zen3apu", MemGen.DDR4, 5, true, Ddr4Apu),
        new PlatformInfo(Platform.Zen2, "plat.zen2", MemGen.DDR4, 5, false, Ddr4Axis),
        new PlatformInfo(Platform.ZenPlus, "plat.zenplus", MemGen.DDR4, 1, false, Ddr4Axis),
        new PlatformInfo(Platform.Zen1, "plat.zen1", MemGen.DDR4, 0, false, Ddr4Axis),
        new PlatformInfo(Platform.ThreadripperPlus, "plat.trplus", MemGen.DDR4, 3, false, Ddr4Axis),
        new PlatformInfo(Platform.Threadripper2WX, "plat.tr2wx", MemGen.DDR4, 4, false, Ddr4Axis),
        new PlatformInfo(Platform.Threadripper1, "plat.tr1", MemGen.DDR4, 2, false, Ddr4Axis),
    };

    public static PlatformInfo Get(Platform p) => Platforms.First(x => x.Id == p);

    /// <summary>Platforms whose numbers come straight from the 1.7.3 tables.</summary>
    public static bool IsLegacy(Platform p) => p <= Platform.Zen2;

    public static readonly IReadOnlyList<MemoryIc> Ics = new[]
    {
        // DDR4 - same order/index as the original "MT" list
        new MemoryIc("samsung-b", "Samsung B-die", MemGen.DDR4, 0, 8),
        new MemoryIc("samsung-s", "Samsung S-die", MemGen.DDR4, 1, 8),
        new MemoryIc("samsung-de", "Samsung D/E-die", MemGen.DDR4, 2, 8),
        new MemoryIc("samsung-oem", "Samsung OEM", MemGen.DDR4, 3, 8),
        new MemoryIc("hynix-mfr", "SK hynix MFR", MemGen.DDR4, 4, 8),
        new MemoryIc("hynix-afr", "SK hynix AFR", MemGen.DDR4, 5, 8),
        new MemoryIc("hynix-cjr", "SK hynix CJR / DJR", MemGen.DDR4, 6, 8),
        new MemoryIc("micron-b", "Micron B-die", MemGen.DDR4, 7, 8),
        new MemoryIc("micron-a", "Micron A-die", MemGen.DDR4, 8, 8),
        new MemoryIc("micron-e", "Micron E/H-die (Rev. E)", MemGen.DDR4, 9, 8),
        new MemoryIc("micron-d", "Micron D-die", MemGen.DDR4, 10, 8),
        // DDR5
        new MemoryIc("ddr5-hynix-a16", "SK hynix 16Gb A-die", MemGen.DDR5, -1, 16),
        new MemoryIc("ddr5-hynix-m16", "SK hynix 16Gb M-die", MemGen.DDR5, -1, 16),
        new MemoryIc("ddr5-hynix-m24", "SK hynix 24Gb M-die", MemGen.DDR5, -1, 24),
        new MemoryIc("ddr5-hynix-32", "SK hynix 32Gb", MemGen.DDR5, -1, 32),
        new MemoryIc("ddr5-samsung-b16", "Samsung 16Gb B-die", MemGen.DDR5, -1, 16),
        new MemoryIc("ddr5-samsung-24", "Samsung 24/32Gb", MemGen.DDR5, -1, 24),
        new MemoryIc("ddr5-micron-16", "Micron 16Gb", MemGen.DDR5, -1, 16),
        new MemoryIc("ddr5-micron-24", "Micron 24/32Gb", MemGen.DDR5, -1, 24),
        new MemoryIc("ddr5-generic", "ic.generic", MemGen.DDR5, -1, 16),
    };

    public static MemoryIc Ic(string id) => Ics.First(x => x.Id == id);

    /// <summary>Board/topology choices. DDR4 entries carry the original TOPOLOGY index.</summary>
    public static IReadOnlyList<Topology> TopologiesFor(PlatformInfo p) => p.Mem == MemGen.DDR5
        ? new[] { new Topology("topo.ddr5.4slot", 0), new Topology("topo.ddr5.2slot", 1) }
        : p.Id switch
        {
            Platform.Threadripper1 or Platform.ThreadripperPlus or Platform.Threadripper2WX =>
                new[] { new Topology("topo.x399", 3) },
            Platform.Zen2 => new[]
            {
                new Topology("topo.x570", 4), new Topology("topo.b450", 1), new Topology("topo.b350", 0),
                new Topology("topo.dual", 2), new Topology("topo.strx4", 4),
            },
            Platform.Zen3 or Platform.Zen3Apu => new[]
            {
                new Topology("topo.x570b550", 4), new Topology("topo.b450", 1), new Topology("topo.a520b350", 0),
                new Topology("topo.dual", 2),
            },
            _ => new[]
            {
                new Topology("topo.b350", 0), new Topology("topo.b450", 1), new Topology("topo.dual", 2),
            },
        };

    public static IReadOnlyList<Profile> ProfilesFor(MemGen g) => g == MemGen.DDR4
        ? new[] { Profile.A2B2, Profile.A0B0, Profile.BadBin, Profile.Manual }
        : new[] { Profile.Standard, Profile.Manual };
}
