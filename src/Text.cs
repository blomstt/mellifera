using System.Diagnostics;
using System.Text;

namespace Hive;

sealed class HiveException(string message) : Exception(message);

static class Text
{
    static readonly HashSet<string> Stop =
        ["the", "a", "an", "and", "or", "of", "to", "in", "on", "for", "is", "it", "with", "as", "at", "by",
         "be", "this", "that", "from", "are", "was", "we", "you", "do", "how", "what"];

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string Today => DateTime.Now.ToString("yyyy-MM-dd");
    public static string Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm");

    // Lower-case runs of letters and digits.
    public static List<string> Words(string s)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0) { words.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) words.Add(sb.ToString());
        return words;
    }

    // Words worth searching for.
    public static IEnumerable<string> Tokens(string s) => Words(s).Where(w => w.Length > 1 && !Stop.Contains(w));

    public static string Slug(string s)
    {
        var slug = string.Join('-', Words(s).Take(7));
        if (slug.Length > 60) slug = slug[..60].TrimEnd('-');
        return slug.Length == 0 ? "entry" : slug;
    }

    public static string OneLine(string? s) => (s ?? "").ReplaceLineEndings(" ").Trim();

    public static string Expand(string path)
    {
        path = path.Trim();
        if (path == "~" || path.StartsWith("~/")) path = Home + path[1..];
        var full = Path.GetFullPath(path);
        return full.Length > 1 ? full.TrimEnd('/') : full;
    }

    public static string Tilde(string path)
    {
        var full = Expand(path);
        return full == Home ? "~" : full.StartsWith(Home + "/") ? "~" + full[Home.Length..] : full;
    }

    public static string StripComments(string s)
    {
        int start;
        while ((start = s.IndexOf("<!--")) >= 0 && s.IndexOf("-->", start) is var end and >= 0)
            s = s[..start] + s[(end + 3)..];
        return s;
    }

    // A program's full path, from PATH only. Process.Start with a bare name
    // also looks in the current directory, so a repository holding an
    // executable called `claude` or `setsid` would get it run.
    public static string Which(string program)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Path.IsPathRooted(dir)) continue;
            var path = Path.Combine(dir, program);
            if (!OperatingSystem.IsWindows() && File.Exists(path) && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                return path;
        }
        throw new HiveException($"{program} isn't on PATH.");
    }

    public static string Cap(string s, int max) =>
        s.Length <= max ? s : s[..max].TrimEnd() + "\n…(cut short; hive_read has the rest)";

    // A unified diff from diff(1), or just the new text if there is no diff.
    public static string Diff(string before, string after)
    {
        var dir = Directory.CreateTempSubdirectory("hive-");
        try
        {
            var (a, b) = (Path.Combine(dir.FullName, "a"), Path.Combine(dir.FullName, "b"));
            File.WriteAllText(a, before);
            File.WriteAllText(b, after);
            var info = new ProcessStartInfo(Which("diff")) { RedirectStandardOutput = true };
            foreach (var x in new[] { "-u", "--label", "card now", "--label", "proposed", a, b }) info.ArgumentList.Add(x);
            using var p = Process.Start(info)!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return text.TrimEnd();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or HiveException)
        {
            return after.Trim();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
