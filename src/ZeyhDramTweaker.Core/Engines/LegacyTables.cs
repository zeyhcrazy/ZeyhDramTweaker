using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace ZeyhDramTweaker.Core.Engines;

/// <summary>Inputs exactly as the original 1.7.3 form held them (SelectedIndex values, MT/s text).</summary>
public readonly record struct LegacyKey(string Preset, int Cpu, int Mt, int Profile, int Rank, int Dimm, int Topology, string Mts)
{
    public string Get(string axis) => axis switch
    {
        "preset" => Preset,
        "cpu" => Cpu.ToString(),
        "mt" => Mt.ToString(),
        "profile" => Profile.ToString(),
        "rank" => Rank.ToString(),
        "dimm" => Dimm.ToString(),
        "topology" => Topology.ToString(),
        "mts" => Mts,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };
}

/// <summary>
/// Tables of Ryzen DRAM Calculator 1.7.3, extracted by running the original code over every input
/// combination and factored by functional dependency: each output field sits in one group keyed only
/// by the inputs it actually depends on. Lookups reproduce the original exactly (verified by
/// tests/ZeyhDramTweaker.Verify).
/// </summary>
public sealed class FactoredTables
{
    public sealed record Group(string[] Keys, string[] Fields, Dictionary<string, string[]> Table);
    public sealed record Outcome(bool Supported, string Message);

    /// <summary>A0/B0, A2/B2 and Bad bin profiles (ddr4_legacy.json.gz).</summary>
    public static FactoredTables Legacy => _legacy.Value;
    /// <summary>The Manual (XMP ns) profile: fields that do not depend on the XMP values (ddr4_manual.json.gz).</summary>
    public static FactoredTables Manual => _manual.Value;

    static readonly Lazy<FactoredTables> _legacy = new(() => Load("ddr4_legacy.json.gz",
        new[] { "preset", "cpu", "mt", "profile", "rank", "topology" }, r => r.GetProperty("axes").GetProperty("mts")));
    static readonly Lazy<FactoredTables> _manual = new(() => Load("ddr4_manual.json.gz",
        new[] { "preset", "cpu", "mt", "rank", "topology" }, r => r.GetProperty("mtsAxis")));

    public string[] MtsAxis { get; private init; } = Array.Empty<string>();
    public string[] StatusAxes { get; private init; } = Array.Empty<string>();
    public List<Outcome> Outcomes { get; } = new();
    public Dictionary<string, string> Status { get; } = new();
    public List<Group> Groups { get; } = new();
    public HashSet<string> FormulaFields { get; } = new();

    static FactoredTables Load(string resource, string[] statusAxes, Func<JsonElement, JsonElement> mtsAxis)
    {
        using var raw = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(resource + " missing");
        using Stream s = resource.EndsWith(".gz") ? new GZipStream(raw, CompressionMode.Decompress) : raw;
        using var doc = JsonDocument.Parse(s);
        var root = doc.RootElement;
        var t = new FactoredTables
        {
            MtsAxis = mtsAxis(root).EnumerateArray().Select(e => e.GetString()!).ToArray(),
            StatusAxes = statusAxes,
        };
        foreach (var o in root.GetProperty("outcomes").EnumerateArray())
            t.Outcomes.Add(new Outcome(o[0].GetBoolean(), o[1].GetString() ?? ""));
        foreach (var st in root.GetProperty("status").EnumerateObject())
            t.Status[st.Name] = st.Value.GetString()!;
        foreach (var g in root.GetProperty("groups").EnumerateArray())
        {
            var table = new Dictionary<string, string[]>();
            foreach (var row in g.GetProperty("table").EnumerateObject())
                table[row.Name] = row.Value.EnumerateArray().Select(v => v.GetString() ?? "").ToArray();
            t.Groups.Add(new Group(
                g.GetProperty("keys").EnumerateArray().Select(e => e.GetString()!).ToArray(),
                g.GetProperty("fields").EnumerateArray().Select(e => e.GetString()!).ToArray(),
                table));
        }
        if (root.TryGetProperty("formulaFields", out var ff))
            foreach (var e in ff.EnumerateArray()) t.FormulaFields.Add(e.GetString()!);
        return t;
    }

    public Outcome OutcomeOf(LegacyKey k)
    {
        int i = Array.IndexOf(MtsAxis, k.Mts);
        var s = i < 0 ? null : Status.GetValueOrDefault(string.Join("|", StatusAxes.Select(k.Get)));
        return s == null ? new Outcome(false, "Not supported! | Error") : Outcomes[s[i] - 48];
    }

    /// <summary>Field values (original control names). Fields the original never set are absent.</summary>
    public Dictionary<string, string> Fields(LegacyKey k)
    {
        var result = new Dictionary<string, string>();
        foreach (var g in Groups)
        {
            if (!g.Table.TryGetValue(string.Join("|", g.Keys.Select(k.Get)), out var vals)) continue;
            for (int i = 0; i < g.Fields.Length; i++)
                if (vals[i].Length > 0) result[g.Fields[i]] = vals[i];
        }
        return result;
    }

    /// <summary>Values of the group that holds <paramref name="field"/> (used to borrow e.g. the SOC
    /// table, which depends only on CPU + MT/s).</summary>
    public Dictionary<string, string> GroupOf(string field, LegacyKey k)
    {
        var res = new Dictionary<string, string>();
        var g = Groups.FirstOrDefault(x => x.Fields.Contains(field));
        if (g != null && g.Table.TryGetValue(string.Join("|", g.Keys.Select(k.Get)), out var vals))
            for (int i = 0; i < g.Fields.Length; i++)
                if (vals[i].Length > 0) res[g.Fields[i]] = vals[i];
        return res;
    }
}
