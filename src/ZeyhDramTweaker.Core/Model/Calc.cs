namespace ZeyhDramTweaker.Core.Model;

/// <summary>Timings from an XMP/EXPO/JEDEC profile, in nanoseconds (what Thaiphoon prints with
/// "Show delays in nanoseconds", or what the SPD stores).</summary>
public sealed record ManualTimings
{
    public double tCL { get; init; }
    public double tRCDWR { get; init; }
    public double tRCDRD { get; init; }
    public double tRP { get; init; }
    public double tRAS { get; init; }
    public double tRC { get; init; }
    public double tRRDS { get; init; }
    public double tRRDL { get; init; }
    public double tFAW { get; init; }
    public double tRFC { get; init; }

    public bool IsComplete => new[] { tCL, tRCDWR, tRCDRD, tRP, tRAS, tRC, tRRDS, tRRDL, tFAW, tRFC }.All(v => v > 0);

    /// <summary>DDR5 EXPO/XMP profiles only carry the primaries (+ tRFC) in nanoseconds.</summary>
    public bool IsPrimaryComplete => new[] { tCL, tRCDRD, tRP, tRAS, tRC, tRFC }.All(v => v > 0);
}

public sealed record CalcInput
{
    public Platform Platform { get; init; } = Platform.Zen3;
    public string IcId { get; init; } = "samsung-b";
    public Preset Preset { get; init; } = Preset.Safe;
    public Profile Profile { get; init; } = Profile.A0B0;
    public int Ranks { get; init; } = 1;          // 1 or 2
    public int Dimms { get; init; } = 2;          // 1, 2 or 4
    public int TopologyIndex { get; init; }       // index in Catalog.TopologiesFor(platform)
    public int MTs { get; init; } = 3600;
    public ManualTimings? Manual { get; init; }
}

public enum NoticeLevel { Info, Warning, Error }

public sealed record Notice(NoticeLevel Level, string Key, params object[] Args);

/// <summary>Where a section's numbers come from, so the user knows what is the original 1.7.3
/// data and what is new.</summary>
public enum Provenance { Original, OriginalFormula, Extended, New, Calibrated, Reference }

public sealed record ResultRow(string Name, string?[] Values, string? HintKey = null)
{
    public string? Current { get; set; }   // filled by "Ler do PC"
}

public sealed record ResultSection(string TitleKey, string[] ColumnKeys, List<ResultRow> Rows, Provenance Provenance);

public sealed class CalcResult
{
    public bool Ok { get; init; }
    public List<Notice> Notices { get; } = new();
    public List<ResultSection> Sections { get; } = new();
    public CalcInput Input { get; init; } = new();
    public double EffectiveMTs { get; init; }
    /// <summary>Data rate the PC was running when the "current" column was read.</summary>
    public int? CurrentMTs { get; set; }

    public ResultSection? Section(string titleKey) => Sections.FirstOrDefault(s => s.TitleKey == titleKey);

    public string? Value(string section, string name, int col = 0) =>
        Section(section)?.Rows.FirstOrDefault(r => r.Name == name)?.Values.ElementAtOrDefault(col);
}
