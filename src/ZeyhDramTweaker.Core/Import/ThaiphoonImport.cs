using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using ZeyhDramTweaker.Core.Model;

namespace ZeyhDramTweaker.Core.Import;

public sealed record ThaiphoonReport(
    ManualTimings Timings, int? Ranks, string? ModuleManufacturer, string? PartNumber, string? DramComponents,
    string? SuggestedIcId, MemGen? Mem);

/// <summary>
/// Reads the HTML report Thaiphoon Burner exports ("Show delays in nanoseconds" must be on). Same fields
/// the 1.7.3 read — tAA, tRCD, tRP, tRAS, tRC, tRRD_S, tRRD_L, tFAW, tRFC1, ranks, part number — taking
/// the LAST occurrence of each label like the original did (the XMP/EXPO block comes after JEDEC).
/// </summary>
public static partial class ThaiphoonImport
{
    public sealed class ImportException(string key) : Exception(key) { public string Key { get; } = key; }

    public static ThaiphoonReport Parse(string html)
    {
        if (html.Contains("show delays in nanoseconds", StringComparison.OrdinalIgnoreCase))
            throw new ImportException("import.tp.nsmissing");

        string raw = html;
        string text = WebUtility.HtmlDecode(Tags().Replace(Breaks().Replace(html, "\n"), " "));
        text = Spaces().Replace(text, " ");

        double Ns(params string[] labels)
        {
            foreach (var label in labels)
            {
                int i = text.LastIndexOf(label, StringComparison.Ordinal);
                if (i < 0) continue;
                var m = NsValue().Match(text, i + label.Length);
                if (m.Success && m.Index - (i + label.Length) < 40)
                    return double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            }
            return 0;
        }

        var t = new ManualTimings
        {
            tCL = Ns("(tAA):", "(tAA)"),
            tRCDWR = Ns("(tRCD):", "(tRCD)"),
            tRCDRD = Ns("(tRCD):", "(tRCD)"),
            tRP = Ns("(tRP):", "(tRP)"),
            tRAS = Ns("(tRAS):", "(tRAS)"),
            tRC = Ns("(tRC):", "(tRC)"),
            tRRDS = Ns("(tRRD_S):", "(tRRD_S)"),
            tRRDL = Ns("(tRRD_L):", "(tRRD_L)"),
            tFAW = Ns("(tFAW):", "(tFAW)"),
            tRFC = Ns("(tRFC1):", "(tRFC1)", "(tRFC):"),
        };
        if (t.tCL <= 0 || t.tRCDRD <= 0) throw new ImportException("import.tp.notreport");

        int? ranks = null;
        var rm = Ranks().Match(text);
        if (rm.Success) ranks = int.Parse(rm.Groups[1].Value, CultureInfo.InvariantCulture);

        string? Cell(string label)
        {
            var m = Regex.Match(raw, Regex.Escape(label) + @"\s*</td>\s*<td[^>]*>(.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return m.Success ? WebUtility.HtmlDecode(Tags().Replace(m.Groups[1].Value, "")).Trim() : null;
        }
        var comps = Cell("DRAM Components:");
        var mem = text.Contains("DDR5", StringComparison.Ordinal) ? MemGen.DDR5 : text.Contains("DDR4", StringComparison.Ordinal) ? MemGen.DDR4 : (MemGen?)null;
        return new ThaiphoonReport(t, ranks, Cell("Module Manufacturer:"), Cell("Module Part Number:"), comps, SuggestIc(comps, mem), mem);
    }

    /// <summary>Best-effort IC from the DRAM part number printed on the chips.</summary>
    public static string? SuggestIc(string? part, MemGen? mem)
    {
        if (string.IsNullOrWhiteSpace(part)) return null;
        var p = part.ToUpperInvariant().Replace(" ", "");
        // DDR5
        if (p.Contains("H5CG") || p.Contains("H5CGD") || (mem == MemGen.DDR5 && p.Contains("HYNIX")))
        {
            if (p.Contains("H5CGD")) return "ddr5-hynix-m24";              // 24 Gb
            if (p.Contains("H5CGE")) return "ddr5-hynix-32";               // 32 Gb
            if (Regex.IsMatch(p, @"H5CG4\dAG")) return "ddr5-hynix-a16";
            if (Regex.IsMatch(p, @"H5CG4\dME")) return "ddr5-hynix-m16";
            return "ddr5-hynix-m16";
        }
        if (p.StartsWith("K4RAH") || p.Contains("K4RAH")) return "ddr5-samsung-b16";
        if (p.Contains("K4RBH") || p.Contains("K4RCH")) return "ddr5-samsung-24";
        if (p.Contains("MT60B")) return p.Contains("MT60B3G") || p.Contains("MT60B4G") ? "ddr5-micron-24" : "ddr5-micron-16";
        // DDR4
        if (p.Contains("H5AN"))
        {
            if (p.Contains("CJR") || p.Contains("DJR")) return "hynix-cjr";
            if (p.Contains("MFR")) return "hynix-mfr";
            if (p.Contains("AFR")) return "hynix-afr";
        }
        if (p.Contains("K4A"))
        {
            if (Regex.IsMatch(p, @"K4A8G0[48]5WB")) return "samsung-b";
            if (Regex.IsMatch(p, @"K4A[48]G0[48]5W[DE]")) return "samsung-de";
            if (Regex.IsMatch(p, @"K4A[48]G0[48]5WS|K4A4G085WE")) return "samsung-s";
        }
        if (p.Contains("MT40A"))
        {
            var rev = Regex.Match(p, @":([A-Z])\b");
            return rev.Success ? rev.Groups[1].Value switch { "B" => "micron-b", "A" => "micron-a", "D" => "micron-d", "E" or "H" or "R" => "micron-e", _ => null } : null;
        }
        return null;
    }

    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"<\s*(br|/tr|/p|/div)[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex Breaks();
    [GeneratedRegex(@"[ \t ]+")] private static partial Regex Spaces();
    [GeneratedRegex(@"([0-9]+(?:[.,][0-9]+)?)\s*ns")] private static partial Regex NsValue();
    [GeneratedRegex(@"Ranks:\s*(\d)")] private static partial Regex Ranks();
}
