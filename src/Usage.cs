using System.Text.Json.Nodes;

namespace Hive;

// Claude's usage limits, which reach the hive only through Claude Code's
// status line: `hive statusline` is the status-line command. Claude Code
// runs it with the session's JSON on stdin (as a session starts, after each
// answer); for claude.ai plans that JSON has rate_limits, the 5-hour and the
// 7-day window, each with used_percentage and resets_at (Unix seconds). It
// saves them to .state/usage.json, which the window watches, and prints a
// short line. Without rate_limits nothing is saved: the window says it has
// no usage data rather than show numbers it doesn't have.
static class Usage
{
    public sealed class Window(double used, long resets)
    {
        public readonly double Used = used;   // percent, 0 to 100
        public readonly long Resets = resets; // Unix seconds
        public DateTime ResetsAt => DateTimeOffset.FromUnixTimeSeconds(Resets).LocalDateTime;
        public bool Over => Resets > 0 && ResetsAt <= DateTime.Now; // reset since: its number is old
    }

    public sealed class Limits(Window? session, Window? week, DateTime updated)
    {
        public readonly Window? Session = session, Week = week;
        public readonly DateTime Updated = updated;
    }

    public static string FilePath(Store store) => Path.Combine(store.Root, ".state", "usage.json");

    // The status-line command: saves what it was given, prints a short line.
    public static string StatusLine(Store store, string input)
    {
        JsonObject? o = null;
        try { o = JsonNode.Parse(input) as JsonObject; }
        catch (System.Text.Json.JsonException) { }
        var model = Json.Str((o?["model"] as JsonObject)?["display_name"]);
        if (o?["rate_limits"] is JsonObject limits && Parse(limits, DateTime.Now) is { } parsed && (parsed.Session != null || parsed.Week != null) && store.Exists)
        {
            var saved = new JsonObject { ["updated"] = DateTimeOffset.Now.ToUnixTimeSeconds() };
            if (limits["five_hour"] is JsonObject five) saved["five_hour"] = five.DeepClone();
            if (limits["seven_day"] is JsonObject seven) saved["seven_day"] = seven.DeepClone();
            var path = FilePath(store);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllBytes(tmp, Json.Bytes(saved));
            File.Move(tmp, path, overwrite: true);
            return Line(parsed, model);
        }
        return Line(null, model);
    }

    // A resets_at that isn't a time in seconds DateTimeOffset can hold (one
    // in milliseconds, say) counts as unknown, like a missing one.
    static Window? WindowOf(JsonNode? node) =>
        node is JsonObject w && w["used_percentage"] is JsonValue u && u.TryGetValue<double>(out var used)
            ? new Window(used, w["resets_at"] is JsonValue r && r.TryGetValue<double>(out var at) && at is >= 0 and <= MaxSeconds ? (long)at : 0)
            : null;

    const double MaxSeconds = 253402300799; // 9999-12-31, DateTimeOffset's last second

    static Limits Parse(JsonObject o, DateTime updated) => new(WindowOf(o["five_hour"]), WindowOf(o["seven_day"]), updated);

    // What the status line last saved, or null (never saved, or unreadable).
    public static Limits? Load(Store store)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(FilePath(store))) is not JsonObject o) return null;
            var updated = o["updated"] is JsonValue u && u.TryGetValue<double>(out var at) ? DateTimeOffset.FromUnixTimeSeconds((long)at).LocalDateTime : DateTime.MinValue;
            var limits = Parse(o, updated);
            return limits.Session == null && limits.Week == null ? null : limits;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    // "ʚ●ɞ Opus · 5h 42% · 7d 23%", or the model alone without usage data.
    public static string Line(Limits? limits, string model)
    {
        var parts = new List<string> { "ʚ●ɞ" + (model.Length > 0 ? " " + model : "") };
        if (limits?.Session is { Over: false } s) parts.Add($"5h {s.Used:0}%");
        if (limits?.Week is { Over: false } w) parts.Add($"7d {w.Used:0}%");
        return string.Join(" · ", parts);
    }

    // "20:00" today, "Thu" or "Thu 09:00" later.
    public static string When(DateTime at, bool hour = false) =>
        at.Date == DateTime.Today ? at.ToString("HH:mm") : hour ? at.ToString("ddd HH:mm") : at.ToString("ddd");

    // "1 h 36 min", "2 days 15 h".
    public static string Until(DateTime at)
    {
        var d = at - DateTime.Now;
        if (d.TotalMinutes < 1) return "now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h {d.Minutes} min";
        return $"{(int)d.TotalDays} day{((int)d.TotalDays == 1 ? "" : "s")} {d.Hours} h";
    }
}
