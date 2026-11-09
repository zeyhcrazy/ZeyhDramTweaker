using System.Text;
using ZeyhDramTweaker.Core.Localization;
using ZeyhDramTweaker.Core.Model;

namespace ZeyhDramTweaker.Core.Export;

/// <summary>Plain-text dump of a result, column-aligned, for pasting into Discord/WhatsApp/notes.</summary>
public static class TextExport
{
    public static string Build(CalcResult r)
    {
        var L = Loc.Instance;
        var i = r.Input;
        var plat = Catalog.Get(i.Platform);
        var ic = Catalog.Ic(i.IcId);
        var sb = new StringBuilder();
        sb.AppendLine($"{L["app.title"]} — {L["ui.preset." + i.Preset]}");
        sb.AppendLine($"{L[plat.NameKey]} | {(ic.Name.StartsWith("ic.") ? L[ic.Name] : ic.Name)} | {L.Profile(i.Profile)} | {i.MTs} MT/s | {i.Ranks}R × {i.Dimms}");
        sb.AppendLine();
        bool anyCurrent = r.Sections.Any(s => s.Rows.Any(x => x.Current != null));
        foreach (var s in r.Sections)
        {
            sb.AppendLine($"[{L[s.TitleKey]}]  ({L["prov." + s.Provenance]})");
            var cols = s.ColumnKeys.Select(k => L[k]).ToList();
            bool cur = anyCurrent && s.Rows.Any(x => x.Current != null);
            if (cur) cols.Add(L["col.current"]);
            int nameW = Math.Max(10, s.Rows.Max(x => x.Name.Length)) + 2;
            var cells = s.Rows.Select(x => x.Values.Select(v => L.Value(v)).Concat(cur ? new[] { x.Current is { } c ? L.Value(c) : "" } : Array.Empty<string>()).ToArray()).ToList();
            var widths = cols.Select((c, ci) => Math.Max(c.Length, cells.Max(row => row.ElementAtOrDefault(ci)?.Length ?? 0)) + 2).ToArray();
            if (cols.Count > 1)
                sb.AppendLine(new string(' ', nameW) + string.Concat(cols.Select((c, ci) => c.PadRight(widths[ci]))));
            for (int ri = 0; ri < s.Rows.Count; ri++)
                sb.AppendLine(s.Rows[ri].Name.PadRight(nameW) + string.Concat(cells[ri].Select((c, ci) => c.PadRight(widths[ci]))).TrimEnd());
            sb.AppendLine();
        }
        if (r.Notices.Count > 0)
        {
            sb.AppendLine($"[{L["ui.notices"]}]");
            foreach (var n in r.Notices) sb.AppendLine($"- {L.Notice(n)}");
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }
}
