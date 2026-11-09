using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ZeyhDramTweaker.Core.Test;

/// <summary>
/// One memory latency measurement, with what the memory was running at the time.
/// <paramref name="Ns"/> = block-random (the figure AIDA64 shows by default); <paramref name="FullRandomNs"/> = every
/// read in a different page (AIDA64's "Full Random"), 0 in records saved before it existed.
/// </summary>
public sealed record LatencyResult(DateTime When, double Ns, double Spread, string Setup, double FullRandomNs = 0);

/// <summary>
/// Memory latency in ns: the load-to-use time of a read that misses every cache and goes to DRAM, measured with a
/// pointer chase (one dependent load at a time).
///
/// Two access patterns:
///  - Block-random (the main figure, AIDA64's default method): 64 blocks of 16 KB, 32 random lines read in each
///    block before jumping to another random block. Reads inside a block often find their DRAM row still open,
///    which is what real programs see. On a 5700X DDR4-3800 this gives 56.2 ns where AIDA64 8.40 shows 56 ns.
///    The figure is flat from 4 KB/8 lines to 64 KB/64 lines; only at half the lines of a block or more does the
///    prefetcher start serving reads (39 ns at 32 of 64 lines, 21 ns at all 64), so 1 line in 8 stays clear of it.
///  - Full random (worst case, AIDA64's "Full Random"): 512 lines, one per 4 KB page, every read opens a new row.
///    61.5 ns on the same PC.
/// Both keep the pages inside the CPU's TLB reach (a plain random walk over 1 GB with 4 KB pages adds ~10 ns of page
/// walks, 72 ns there) and evict the lines with CLFLUSH before every pass, so each read really comes from DRAM.
/// Many passes per page set, several page sets, median set. Cache sanity on the same PC: 16 KB 0.84 ns (L1),
/// 256 KB 2.55 ns (L2), 8 MB 11.1 ns (L3), the figures AIDA64 shows for Zen 3.
/// </summary>
public static unsafe class LatencyTest
{
    const int PageSize = 4096, LineSize = 64;
    const long Reserve = 256L << 20;

    /// <summary>A chase layout: <paramref name="Blocks"/> random blocks of <paramref name="BlockBytes"/>, <paramref name="LinesPerBlock"/> random lines in each.</summary>
    record Pattern(int Blocks, int BlockBytes, int LinesPerBlock, int Passes)
    {
        public int Loads => Blocks * LinesPerBlock;
    }
    static readonly Pattern BlockRandom = new(64, 16 * 1024, 32, 600);
    static readonly Pattern FullRandom = new(512, PageSize, 1, 2500);

    [DllImport("kernel32.dll", SetLastError = true)] static extern nint VirtualAlloc(nint addr, nuint size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualFree(nint addr, nuint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualProtect(nint addr, nuint size, uint protect, out uint old);
    [DllImport("kernel32.dll")] static extern nint GetCurrentThread();
    [DllImport("kernel32.dll")] static extern nuint SetThreadAffinityMask(nint thread, nuint mask);
    const uint MemCommit = 0x1000, MemReserve = 0x2000, MemDecommit = 0x4000, MemRelease = 0x8000, ReadWrite = 0x04, ExecuteRead = 0x20;

    /// <summary>
    /// CLFLUSH has no .NET intrinsic, so a 19-byte x64 routine does it:
    /// void flush(nint* lines /*rcx*/, long count /*rdx*/) { do { clflush [*lines++]; } while (--count); mfence; }
    /// </summary>
    static readonly byte[] FlushCode =
    {
        0x48, 0x8B, 0x01,        // mov rax, [rcx]
        0x0F, 0xAE, 0x38,        // clflush [rax]
        0x48, 0x83, 0xC1, 0x08,  // add rcx, 8
        0x48, 0xFF, 0xCA,        // dec rdx
        0x75, 0xF1,              // jnz -15
        0x0F, 0xAE, 0xF0,        // mfence
        0xC3,                    // ret
    };

    /// <summary>Runs both patterns on one core at high priority (about 3 s). Throws when the CPU is not x64.</summary>
    public static LatencyResult Run(string setup, IProgress<double>? progress = null, int sets = 10, double passScale = 1.0)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new PlatformNotSupportedException("x64 only");
        (double med, double spread) block = default, full = default;
        Exception? error = null;
        // a thread of our own: pinned to one core and raised, so the scheduler does not move it mid-pass
        var t = new Thread(() =>
        {
            try
            {
                block = Measure(BlockRandom, sets, passScale, p => progress?.Report(p * 0.5));
                full = Measure(FullRandom, sets, passScale, p => progress?.Report(0.5 + p * 0.5));
            }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "latency" };
        var proc = Process.GetCurrentProcess();
        var keep = proc.PriorityClass;
        try
        {
            try { proc.PriorityClass = ProcessPriorityClass.High; } catch { /* not allowed: run at normal priority */ }
            t.Start();
            t.Join();
        }
        finally { try { proc.PriorityClass = keep; } catch { } }
        if (error != null) throw error;
        return new LatencyResult(DateTime.Now, block.med, block.spread, setup, full.med);
    }

    /// <summary>Median ns per load over <paramref name="sets"/> random layouts, and half the spread of the middle sets.</summary>
    static (double, double) Measure(Pattern pat, int sets, double passScale, Action<double> progress)
    {
        // a core other than 0, which takes most interrupts
        int cpu = Environment.ProcessorCount > 2 ? 2 : 0;
        SetThreadAffinityMask(GetCurrentThread(), (nuint)1 << cpu);

        nint code = VirtualAlloc(0, PageSize, MemCommit | MemReserve, ReadWrite);
        nint region = VirtualAlloc(0, (nuint)Reserve, MemReserve, ReadWrite);
        if (code == 0 || region == 0) throw new OutOfMemoryException("latency buffers");
        try
        {
            Marshal.Copy(FlushCode, 0, code, FlushCode.Length);
            if (!VirtualProtect(code, PageSize, ExecuteRead, out _)) throw new InvalidOperationException("VirtualProtect");
            var flush = (delegate* unmanaged<nint*, long, void>)code;

            var rng = new Random();
            var slots = new int[(int)(Reserve / pat.BlockBytes)];
            var inBlock = new int[pat.BlockBytes / LineSize];
            var lines = new nint[pat.Loads];
            var res = new double[sets];
            int passes = Math.Max(50, (int)(pat.Passes * passScale));
            for (int set = -1; set < sets; set++)   // set -1 = warm-up, discarded
            {
                // pick the blocks, commit them, pick random distinct lines inside each
                for (int i = 0; i < slots.Length; i++) slots[i] = i;
                for (int i = 0; i < pat.Blocks; i++) { int j = rng.Next(i, slots.Length); (slots[i], slots[j]) = (slots[j], slots[i]); }
                for (int b = 0; b < pat.Blocks; b++)
                {
                    nint block = region + (nint)((long)slots[b] * pat.BlockBytes);
                    if (VirtualAlloc(block, (nuint)pat.BlockBytes, MemCommit, ReadWrite) == 0) throw new OutOfMemoryException("latency block");
                    for (int i = 0; i < inBlock.Length; i++) inBlock[i] = i;
                    for (int i = 0; i < pat.LinesPerBlock; i++) { int j = rng.Next(i, inBlock.Length); (inBlock[i], inBlock[j]) = (inBlock[j], inBlock[i]); }
                    for (int i = 0; i < pat.LinesPerBlock; i++) lines[b * pat.LinesPerBlock + i] = block + inBlock[i] * LineSize;
                }
                for (int i = 0; i < lines.Length; i++) *(nint*)lines[i] = lines[(i + 1) % lines.Length];   // one cycle through all lines

                long ticks = 0;
                int n = set < 0 ? passes / 5 : passes;
                fixed (nint* pl = lines)
                {
                    nint* p = (nint*)lines[0];
                    for (int pass = 0; pass < n; pass++)
                    {
                        flush(pl, lines.Length);
                        long t0 = Stopwatch.GetTimestamp();
                        for (int i = 0; i < lines.Length; i += 8)
                        {
                            p = (nint*)*p; p = (nint*)*p; p = (nint*)*p; p = (nint*)*p;
                            p = (nint*)*p; p = (nint*)*p; p = (nint*)*p; p = (nint*)*p;
                        }
                        ticks += Stopwatch.GetTimestamp() - t0;
                    }
                    if (p == null) throw new InvalidOperationException("chain");   // keeps the loads alive
                }
                if (set >= 0) res[set] = ticks * 1e9 / Stopwatch.Frequency / ((double)lines.Length * n);
                // give the blocks back so the next set lands on other physical memory
                for (int b = 0; b < pat.Blocks; b++) VirtualFree(region + (nint)((long)slots[b] * pat.BlockBytes), (nuint)pat.BlockBytes, MemDecommit);
                progress((set + 2) / (double)(sets + 1));
            }
            Array.Sort(res);
            return (res[res.Length / 2], (res[res.Length * 3 / 4] - res[res.Length / 4]) / 2);
        }
        finally
        {
            VirtualFree(region, 0, MemRelease);
            VirtualFree(code, 0, MemRelease);
        }
    }

    // ------------------------------------------------------------------ history (%LocalAppData%\ZeyhDramTweaker\latency.json)
    static string HistoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeyhDramTweaker", "latency.json");

    /// <summary>Saved measurements, newest first.</summary>
    public static List<LatencyResult> History()
    {
        try { return File.Exists(HistoryPath) ? JsonSerializer.Deserialize<List<LatencyResult>>(File.ReadAllText(HistoryPath)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public static List<LatencyResult> Save(LatencyResult r)
    {
        var list = History();
        list.Insert(0, r);
        if (list.Count > 40) list.RemoveRange(40, list.Count - 40);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(list));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>TestMem5 (or anything hammering the RAM) running at the same time makes the number meaningless.</summary>
    public static bool Tm5Running() => Process.GetProcessesByName("TM5").Length > 0;
}
