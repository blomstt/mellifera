using System.Text.Json.Nodes;

namespace Hive;

// Talking with the Queen in the Hive window's work pane. Each message is a
// task (role "queen", bee Queen) that resumes the window's Claude session
// with her, `claude -p --resume <session> --output-format stream-json`,
// through the same runner as any task; with no session it starts a fresh
// one, as no agent, so it gets the Queen's briefing like the user's own
// sessions. It resumes across restarts until the user starts a new one.
// What was said is read back from the session's own transcript,
// ~/.claude/projects/<folder>/<session>.jsonl, which Claude Code writes as
// the session goes. (Task files of role "talk", a bee's talk turns from
// 0.1.02's window, are still read: kept out of the inbox, titled "talk:".)
static partial class Tasks
{
    // The Queen's turns are tasks of their own form: the bee they name is
    // Queen, which no bee is.
    public const string Queen = "Queen";

    // A turn with the Queen: what the user said, in the conversation it
    // continues (or a new one), with the access the pane is set to.
    public static BeeTask QueenTalk(Store store, string said, string project, string access, BeeTask? from, string model = "")
    {
        var session = from?.Doc.Get("session") ?? "";
        if (session.Length > 0 && List(store, Queen).Exists(t => t.Running && (t.Doc.Get("resume") == session || t.Doc.Get("session") == session)))
            throw new HiveException("The Queen is still answering; send it once she's done.");
        var task = Create(store, Queen, said, project, access, role: "queen", follows: session.Length > 0 ? from!.Id : "", resume: session);
        // Which conversation it's in: the mark of the last "new" (none before the first).
        if (QueenSince(store) is { Length: > 0 } since) task.Doc.Set("conversation", since);
        if (model.Length > 0) task.Doc.Set("model", model); // Args passes it as --model
        // A resumed claude stays on its last turn's model, so default after a
        // pick needs --model default; after a default turn nothing is needed.
        else if (from?.Doc.Get("model") is { Length: > 0 } last && last != "default") task.Doc.Set("model", "default");
        task.Doc.Save(task.Path);
        return task;
    }

    // The models the pane's status line offers, by the names it shows them
    // under. Picking none ("default") passes no --model: the user's own default
    // (or --model default, to leave a picked model in the same conversation).
    public static readonly string[] QueenModels = ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-4-5-20251001", "claude-fable-5-1"];
    public static readonly string[] QueenModelNames = ["Opus 5.5", "Sonnet 5.5", "Haiku 4.5", "Fable 5.1"];

    // The model picked for this conversation ("" for the default); "new" forgets it.
    public static string QueenModel(Store store) =>
        File.Exists(ModelPath(store)) && File.ReadAllText(ModelPath(store)).Trim() is var m && QueenModels.Contains(m) ? m : "";

    public static void SetQueenModel(Store store, string model)
    {
        Directory.CreateDirectory(Dir(store));
        if (model.Length == 0) File.Delete(ModelPath(store));
        else File.WriteAllText(ModelPath(store), model + "\n");
    }

    static string ModelPath(Store store) => System.IO.Path.Combine(Dir(store), ".queen-model");

    // Whether a task is one of the Queen's turns in the conversation since
    // this "new" mark. By the mark each turn carries, not by its id: ids are
    // local time, which can go back (the repeated autumn hour).
    public static bool InConversation(BeeTask t, string since) =>
        t.Bee == Queen && t.Doc.Get("role") == "queen" && t.Doc.Get("conversation") == since;

    // The conversation the pane continues: the Queen's newest turn with a
    // session since the user last started a new one, however old (null: a
    // fresh one). tasks: newest first, as List gives them.
    public static BeeTask? QueenSession(List<BeeTask> tasks, string since)
    {
        foreach (var t in tasks)
            if (InConversation(t, since) && t.Doc.Get("session").Length > 0) return t;
        return null;
    }

    // The mark of the user's last "new" ("" for never), carried by each turn
    // sent after it.
    public static string QueenSince(Store store) =>
        File.Exists(QueenPath(store)) ? File.ReadAllText(QueenPath(store)).Trim() : "";

    // "new": the next turn starts a fresh conversation, after a restart too.
    // The mark only has to be new: her newest turn's id and the UTC time.
    public static string NewQueenSession(Store store)
    {
        Directory.CreateDirectory(Dir(store));
        var since = $"{List(store, Queen).FirstOrDefault()?.Id}~{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        File.WriteAllText(QueenPath(store), since + "\n");
        File.Delete(ClosedPath(store));
        File.Delete(ModelPath(store));
        return since;
    }

    static string QueenPath(Store store) => System.IO.Path.Combine(Dir(store), ".queen");

    // The bee tabs (agent ids) the user closed in this conversation, so a
    // restart doesn't bring them back; "new" forgets them.
    public static string[] QueenClosed(Store store) => File.Exists(ClosedPath(store)) ? File.ReadAllLines(ClosedPath(store)) : [];

    public static void CloseQueenTab(Store store, string agent)
    {
        Directory.CreateDirectory(Dir(store));
        File.AppendAllText(ClosedPath(store), agent + "\n");
    }

    static string ClosedPath(Store store) => System.IO.Path.Combine(Dir(store), ".queen-closed");

    static string QueenPrompt(BeeTask task, string where) =>
        $"{task.Prompt}\n\n(Sent from the Hive window's work pane, where the user reads your reply and can answer it. {where}.)";
}

// One line of a talk's transcript, as Claude Code shows it: what you said
// (❯), what the bee said (●), a tool call (● in green, its name bold), its
// result (⎿), the lines an edit took out (-) and put in (+), more of a
// message (plain), or an error.
sealed class TalkLine(string kind, string bold, string text)
{
    public readonly string Kind = kind, Bold = bold, Text = text;
}

static class Transcript
{
    // The session's transcript file, if Claude Code wrote one under ~/.claude/projects.
    public static string? Find(string session)
    {
        if (session.Length == 0) return null;
        var root = Path.Combine(Text.Home, ".claude", "projects");
        if (!Directory.Exists(root)) return null;
        foreach (var dir in Directory.EnumerateDirectories(root))
            if (Path.Combine(dir, session + ".jsonl") is var path && File.Exists(path)) return path;
        return null;
    }

    // The lines one transcript record adds. tools remembers each tool call
    // (by id) until its result comes, so an edit's result can show its diff.
    // usage adds up the bee's output tokens; model is what it runs on.
    // folder: paths in it are shown relative to it. subagent: the
    // transcript is a subagent's own, whose records are all its session's
    // side chain (in a session's own transcript they're skipped). agents
    // gets the ids of its Agent calls; earlier finds a tool call from before
    // the part of the transcript that was read.
    public static void Read(string json, List<TalkLine> lines, Dictionary<string, JsonObject> tools, ref long tokens, ref string model, string folder = "", bool subagent = false, HashSet<string>? agents = null, Func<string, JsonObject?>? earlier = null)
    {
        JsonObject? o;
        try { o = JsonNode.Parse(json) as JsonObject; }
        catch (System.Text.Json.JsonException) { return; }
        if (o == null || !subagent && o["isSidechain"] is JsonValue side && side.TryGetValue<bool>(out var sidechain) && sidechain) return;
        var type = Json.Str(o["type"]);
        if (type is not ("user" or "assistant") || o["message"] is not JsonObject message) return;
        if (o["isMeta"] is JsonValue meta && meta.TryGetValue<bool>(out var isMeta) && isMeta) return;
        if (type == "assistant")
        {
            if (Json.Str(message["model"]) is { Length: > 0 } m && !m.StartsWith('<')) model = m;
            if ((message["usage"] as JsonObject)?["output_tokens"] is JsonValue u && u.TryGetValue<double>(out var n)) tokens += (long)n; // (double: long would compile another number parser in)
        }
        var content = message["content"];
        if (content is JsonValue v && v.TryGetValue<string>(out var said))
        {
            if (type == "user" && !Injected(said)) lines.Add(new TalkLine(subagent ? "queen" : "you", "", UserText(said))); // a subagent's prompts are the Queen's
            return;
        }
        if (content is not JsonArray blocks) return;
        foreach (var node in blocks)
        {
            if (node is not JsonObject b) continue;
            switch (Json.Str(b["type"]))
            {
                case "text" when type == "assistant":
                    Say(lines, Json.Str(b["text"]));
                    break;
                case "text" when type == "user":
                    var text = Json.Str(b["text"]);
                    if (!Injected(text) && !text.StartsWith("Caveat:")) lines.Add(new TalkLine(subagent ? "queen" : "you", "", UserText(text)));
                    break;
                case "tool_use":
                    var name = Json.Str(b["name"]);
                    var input = b["input"] as JsonObject ?? [];
                    tools[Json.Str(b["id"])] = b;
                    if (name is "Agent" or "Task") agents?.Add(Json.Str(b["id"]));
                    var (tool, what) = Steps.Describe(name, input, folder);
                    var shown = tool.StartsWith("mcp__") ? tool[(tool.LastIndexOf("__", StringComparison.Ordinal) + 2)..] : tool;
                    lines.Add(new TalkLine("tool", shown, what.Length > 0 ? $"({Cut(what, 90)})" : ""));
                    break;
                case "tool_result":
                    // Only calls still waiting are kept; one from before what was read is looked up.
                    if (!tools.Remove(Json.Str(b["tool_use_id"]), out var use)) use = earlier?.Invoke(Json.Str(b["tool_use_id"]));
                    Result(lines, b, use);
                    break;
            }
        }
    }

    // How the subagents a session started ended, from one record of its
    // transcript: the Agent call's result ("completed", "async_launched" for
    // one sent to the background, "error"), or the <task-notification> a
    // background one sends when it's over ("completed", "failed", "killed").
    // ends: agent id -> the newest of those.
    public static void AgentEnd(string json, Dictionary<string, string> ends)
    {
        if (!json.Contains("\"agentId\"") && !json.Contains("task-notification")) return;
        JsonObject? o;
        try { o = JsonNode.Parse(json) as JsonObject; }
        catch (System.Text.Json.JsonException) { return; }
        if (o == null) return;
        // A notification that comes while the session is busy is written as a queued_command attachment.
        var note = Json.Str(o["type"]) switch
        {
            "user" => (o["message"] as JsonObject)?["content"],
            "attachment" when o["attachment"] is JsonObject a && Json.Str(a["type"]) == "queued_command" => a["prompt"],
            _ => null,
        };
        if (note == null) return;
        if (o["toolUseResult"] is JsonObject result && Json.Str(result["agentId"]) is { Length: > 0 } id)
        {
            var error = false;
            if ((o["message"] as JsonObject)?["content"] is JsonArray blocks)
                foreach (var b in blocks) error |= (b as JsonObject)?["is_error"] is JsonValue e && e.TryGetValue<bool>(out var isError) && isError;
            ends[id] = error ? "error" : Json.Str(result["status"]) is { Length: > 0 } status ? status : "completed";
        }
        else if (note is JsonValue v && v.TryGetValue<string>(out var said) && said.StartsWith("<task-notification>"))
        {
            var task = Between(said, "<task-id>", "</task-id>");
            var how = Between(said, "<status>", "</status>");
            if (task.Length > 0 && how.Length > 0) ends[task] = how;
        }
    }

    // What Claude Code puts in as a user message itself: <tag>…</tag>
    // (<task-notification>, <command-name>, <system-reminder>…), not a message
    // of the user's that starts with <.
    static bool Injected(string text)
    {
        if (!text.StartsWith('<')) return false;
        var close = text.IndexOf('>');
        return close > 1 && text.AsSpan(1, close - 1) is var tag && tag.IndexOfAny(" =\"'/") < 0 && text.Contains($"</{tag}>", StringComparison.Ordinal);
    }

    static string Between(string s, string start, string end)
    {
        var a = s.IndexOf(start, StringComparison.Ordinal);
        if (a < 0) return "";
        a += start.Length;
        var b = s.IndexOf(end, a, StringComparison.Ordinal);
        return b < 0 ? "" : s[a..b].Trim();
    }

    // A user message as the window shows it: without the note the window
    // added for the bee ("(The user is talking with you…)").
    static string UserText(string text)
    {
        var note = text.LastIndexOf("\n\n(", StringComparison.Ordinal);
        return (note > 0 && text.EndsWith(')') ? text[..note] : text).Trim();
    }

    // A message: its first line after ●, the rest under it.
    static void Say(List<TalkLine> lines, string text)
    {
        var parts = text.Trim().Split('\n');
        if (parts.Length == 0 || parts[0].Length == 0) return;
        lines.Add(new TalkLine("say", "", parts[0]));
        for (int i = 1; i < parts.Length && i < 40; i++)
            if (parts[i].Trim().Length > 0) lines.Add(new TalkLine("plain", "", parts[i].TrimEnd()));
    }

    // A tool's result: what came back in a line (and an edit's lines out and in).
    static void Result(List<TalkLine> lines, JsonObject b, JsonObject? use)
    {
        var text = ResultText(b["content"]);
        var error = b["is_error"] is JsonValue e && e.TryGetValue<bool>(out var isError) && isError;
        if (error) { lines.Add(new TalkLine("err", "", "Error: " + Cut(FirstLine(text), 120))); return; }
        var name = use == null ? "" : Json.Str(use["name"]);
        var input = use?["input"] as JsonObject;
        switch (name)
        {
            case "Read":
                lines.Add(new TalkLine("out", "", $"Read {text.Split('\n').Length} lines"));
                return;
            case "Edit" or "MultiEdit":
                var old = Json.Str(input?["old_string"]).Split('\n');
                var @new = Json.Str(input?["new_string"]).Split('\n');
                lines.Add(new TalkLine("out", "", $"Updated with {@new.Length} addition{(@new.Length == 1 ? "" : "s")} and {old.Length} removal{(old.Length == 1 ? "" : "s")}"));
                for (int i = 0; i < old.Length && i < 6; i++) lines.Add(new TalkLine("del", "", old[i]));
                for (int i = 0; i < @new.Length && i < 6; i++) lines.Add(new TalkLine("add", "", @new[i]));
                return;
            case "Write":
                lines.Add(new TalkLine("out", "", $"Wrote {Json.Str(input?["content"]).Split('\n').Length} lines"));
                return;
        }
        lines.Add(new TalkLine("out", "", text.Trim().Length == 0 ? "(No content)" : Cut(FirstLine(text), 120)));
    }

    static string ResultText(JsonNode? content)
    {
        if (content is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (content is not JsonArray parts) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var p in parts) if (p is JsonObject o && Json.Str(o["type"]) == "text") sb.Append(Json.Str(o["text"]));
        return sb.ToString();
    }

    // The first line, without terminal colours.
    static string FirstLine(string text)
    {
        var line = text.TrimStart().Split('\n')[0];
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '\u001b') { while (i < line.Length && !char.IsAsciiLetter(line[i])) i++; continue; }
            sb.Append(line[i]);
        }
        return sb.ToString().Trim();
    }

    static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // "claude-opus-5-5" → "Opus 5.5".
    public static string ModelName(string model)
    {
        var parts = model.Replace("claude-", "").Split('-');
        if (parts.Length == 0 || parts[0].Length == 0) return model;
        var name = char.ToUpperInvariant(parts[0][0]) + parts[0][1..];
        var version = "";
        for (int i = 1; i < parts.Length && parts[i].Length is > 0 and <= 2 && char.IsAsciiDigit(parts[i][0]); i++) version += (version.Length > 0 ? "." : "") + parts[i];
        return version.Length > 0 ? $"{name} {version}" : name;
    }
}
