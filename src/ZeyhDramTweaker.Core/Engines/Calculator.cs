using ZeyhDramTweaker.Core.Hardware;
using ZeyhDramTweaker.Core.Model;

namespace ZeyhDramTweaker.Core.Engines;

public static class Calculator
{
    public static CalcResult Calculate(CalcInput input) =>
        Catalog.Get(input.Platform).Mem == MemGen.DDR5 ? Ddr5Engine.Calculate(input) : Ddr4Engine.Calculate(input);

    /// <summary>Puts the live register values next to the recommendations (row names match).</summary>
    public static void AttachCurrent(CalcResult result, CurrentMemory? current)
    {
        result.CurrentMTs = current?.MTs;
        foreach (var s in result.Sections)
            foreach (var r in s.Rows)
                r.Current = current != null && current.Values.TryGetValue(r.Name, out var v) ? v : null;
        // where each "this PC" value comes from, and why VDIMM can be missing
        if (current != null && !result.Notices.Any(n => n.Key == "info.current.sources"))
            result.Notices.Add(new Notice(NoticeLevel.Info, "info.current.sources"));
    }
}
