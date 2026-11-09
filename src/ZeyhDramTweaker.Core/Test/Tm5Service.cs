using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using ZeyhDramTweaker.Core.Model;

namespace ZeyhDramTweaker.Core.Test;

public enum Tm5Outcome { Running, Passed, Errors, Stopped, Interrupted }

public sealed record Tm5Session(DateTime Started, string Config, TimeSpan Duration, Tm5Outcome Outcome, List<string> Errors, string Size)
{
    /// <summary>Folder of the TestMem5 that wrote it: "" = the app's own copy.</summary>
    public string Source { get; init; } = "";
}

/// <summary>
/// TestMem5 (Serj) bundled with the anta777 / 1usmus configs from the Zeyh pack. The app extracts it to
/// %LocalAppData%, selects the config through TM5.ini, starts it (TM5 asks for elevation) and reads the
/// outcome from the Log.txt it writes: "Testing completed", "Error in test N", "stopped by user/Windows".
/// </summary>
public static partial class Tm5Service
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeyhDramTweaker", "TestMem5");
    static string LogPath => Path.Combine(Root, "Log.txt");

    public static void EnsureExtracted()
    {
        if (File.Exists(Path.Combine(Root, "TM5.exe")) && File.Exists(Path.Combine(Root, "bin", "MT0.dll"))) return;
        Directory.CreateDirectory(Root);
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("tm5.zip") ?? throw new InvalidOperationException("tm5.zip missing");
        using var zip = new ZipArchive(s);
        foreach (var e in zip.Entries)
        {
            var dest = Path.GetFullPath(Path.Combine(Root, e.FullName.Replace('\\', '/')));
            if (!dest.StartsWith(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase)) continue;
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\') || e.Length == 0 && e.Name.Length == 0) { Directory.CreateDirectory(dest); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, overwrite: true);
        }
    }

    /// <summary>Config names as TM5 shows them ("Extreme @ anta777").</summary>
    public static List<string> Configs()
    {
        EnsureExtracted();
        return Directory.GetFiles(Path.Combine(Root, "bin"), "*.cfg").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .OrderBy(n => n.Contains("anta777") ? 0 : 1).ThenBy(n => n).ToList();
    }

    /// <summary>anta777's own advice: Extreme for DDR4, the Ryzen DDR5 config on AM5.</summary>
    public static string DefaultConfig(MemGen mem) => mem == MemGen.DDR5 ? "DDR5 Ryzen3D @ anta777" : "Extreme @ anta777";

    public static Process? Start(string config)
    {
        EnsureExtracted();
        File.WriteAllText(Path.Combine(Root, "TM5.ini"), $"[Settings]\r\nConfig File={config}.cfg\r\nShow Log=1\r\n");
        return Process.Start(new ProcessStartInfo(Path.Combine(Root, "TM5.exe")) { UseShellExecute = true, WorkingDirectory = Root });
    }

    /// <summary>All sessions in the app's Log.txt and in the Log.txt of the <paramref name="others"/> TestMem5 folders, newest first.</summary>
    public static List<Tm5Session> History(IEnumerable<string>? others = null)
    {
        var all = ReadLog(LogPath, "");
        foreach (var dir in others ?? Enumerable.Empty<string>())
            all.AddRange(ReadLog(Path.Combine(dir, "Log.txt"), dir));
        return all.OrderByDescending(s => s.Started).ToList();
    }

    static List<Tm5Session> ReadLog(string path, string source)
    {
        try
        {
            if (!File.Exists(path)) return new();
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs)) text = r.ReadToEnd();
            return Parse(text).Select(s => s with { Source = source }).ToList();
        }
        catch (IOException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
    }

    // ------------------------------------------------------------------ other TestMem5 copies (the Zeyh pack in Downloads...)
    static string FoldersFile => Path.Combine(Path.GetDirectoryName(Root)!, "tm5-folders.txt");

    static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// TestMem5 folders besides the app's own: the ones the user added, plus any TM5.exe found a few levels under
    /// Downloads, Desktop and Documents (the Zeyh pack lands in Downloads\Zeyh Tweaks\PROGRAMAS\TestMem5). Slow on a
    /// big Downloads folder: call it off the UI thread.
    /// </summary>
    public static List<string> OtherFolders()
    {
        var list = new List<string>();
        void Add(string? d)
        {
            if (string.IsNullOrWhiteSpace(d) || !File.Exists(Path.Combine(d, "TM5.exe")) || Same(d, Root) || list.Any(x => Same(x, d))) return;
            list.Add(Path.GetFullPath(d));
        }
        try { if (File.Exists(FoldersFile)) foreach (var l in File.ReadAllLines(FoldersFile)) Add(l.Trim()); } catch (IOException) { }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        foreach (var dir in new[] { Path.Combine(home, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) }.Distinct())
        {
            if (!Directory.Exists(dir)) continue;
            try { foreach (var exe in Directory.EnumerateFiles(dir, "TM5.exe", opts)) Add(Path.GetDirectoryName(exe)); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return list;
    }

    /// <summary>Remembers a TestMem5 folder the user picked. False when it has no TM5.exe.</summary>
    public static bool AddFolder(string dir)
    {
        if (!File.Exists(Path.Combine(dir, "TM5.exe"))) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(FoldersFile)!);
        var lines = File.Exists(FoldersFile) ? File.ReadAllLines(FoldersFile).ToList() : new List<string>();
        if (!lines.Any(l => l.Trim().Length > 0 && Same(l.Trim(), dir))) { lines.Add(dir); File.WriteAllLines(FoldersFile, lines); }
        return true;
    }

    /// <summary>"…\PROGRAMAS\TestMem5": short enough for a table cell, long enough to recognise.</summary>
    public static string ShortName(string dir)
    {
        var parts = dir.TrimEnd('\\').Split('\\');
        return parts.Length <= 2 ? dir : "…\\" + string.Join("\\", parts[^2..]);
    }

    public static List<Tm5Session> Parse(string text)
    {
        var list = new List<Tm5Session>();
        foreach (Match m in Header().Matches(text))
        {
            int start = m.Index + m.Length;
            int next = text.IndexOf("--------", start, StringComparison.Ordinal);
            var body = text[start..(next < 0 ? text.Length : next)];
            DateTime.TryParseExact(m.Groups[1].Value, "yyyy-M-d H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var when);   // TM5 writes "2026-06-17 8:09"
            string config = "", size = "";
            var errors = new List<string>();
            var outcome = Tm5Outcome.Running;
            TimeSpan last = TimeSpan.Zero;
            foreach (var line in body.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var lm = Line().Match(line);
                if (!lm.Success) continue;
                TimeSpan.TryParse(lm.Groups[1].Value, CultureInfo.InvariantCulture, out last);
                var msg = lm.Groups[2].Value;
                if (msg.StartsWith("Configuration:")) config = msg["Configuration:".Length..].Trim();
                else if (msg.StartsWith("Testing ") && msg.Contains(" MB")) size = msg["Testing ".Length..];
                else if (msg.StartsWith("Error")) { errors.Add($"{lm.Groups[1].Value}  {msg}"); outcome = Tm5Outcome.Errors; }
                else if (msg.Contains("completed")) outcome = errors.Count > 0 ? Tm5Outcome.Errors : Tm5Outcome.Passed;
                else if (msg.Contains("stopped by Windows")) outcome = Tm5Outcome.Interrupted;
                else if (msg.Contains("stopped")) { if (outcome != Tm5Outcome.Errors) outcome = Tm5Outcome.Stopped; }
            }
            list.Add(new Tm5Session(when, config, last, outcome, errors, size));
        }
        list.Reverse();
        return list;
    }

    /// <summary>Notifies when TM5 appends to its log (live status in the app).</summary>
    public static FileSystemWatcher Watch(Action changed, string? dir = null)
    {
        dir ??= Root;
        Directory.CreateDirectory(dir);
        var w = new FileSystemWatcher(dir, "Log.txt") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName, EnableRaisingEvents = true };
        w.Changed += (_, _) => changed();
        w.Created += (_, _) => changed();
        return w;
    }

    [GeneratedRegex(@"-------- TestMem5 [\d.]+ (\d{4}-\d{1,2}-\d{1,2} \d{1,2}:\d{2}) --------")] private static partial Regex Header();
    [GeneratedRegex(@"^(\d{2}:\d{2}:\d{2})\s+(.+)$")] private static partial Regex Line();
}
