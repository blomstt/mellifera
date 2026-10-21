using System.Text.Json.Nodes;

namespace Hive;

// Claude Code hooks: `hive hook <event>`, the event's JSON on stdin. They are
// quick and never fail a session.
//
//   session-start   the Queen's briefing, or a bee's when the session runs as
//                   that bee (`claude --agent <bee>`, as every task does)
//   subagent-start  a bee's briefing, handed to the bee as the Queen spawns it
//   subagent-stop   a bee whose card says deposit_before_stop: yes and that
//   stop            saved nothing is sent back, once, to save (stop: only for
//                   task sessions, not your chats)
//   session-end     a chat you had open with a bee is over
//   pre-tool-use    a reviewer's or the overseer's Bash call runs only if it
//                   reads (ReadOnlyRefusal); every other call goes through
//
// What bees are doing for the window: .state/<id> holds a task session or
// subagent at work (the bee, when it started, and for a subagent the session
// that spawned it and its folder); .talk/<session id> is a chat you have open
// with a bee (Talk to…), kept apart from .state so the Stop check never
// mistakes a chat for a task.
static class Hooks
{
    public static readonly (string Event, string Arg)[] Installed =
        [("SessionStart", "session-start"), ("SubagentStart", "subagent-start"), ("SubagentStop", "subagent-stop"), ("Stop", "stop"), ("SessionEnd", "session-end"),
         ("PreToolUse", "pre-tool-use")];

    public static int Run(Store store, string hook)
    {
        try
        {
            if (!store.Exists) return 0;
            var input = Console.IsInputRedirected && JsonNode.Parse(Console.In.ReadToEnd()) is JsonObject o ? o : [];
            if (Handle(store, hook, input) is { } reply)
            {
                using var stdout = Console.OpenStandardOutput();
                stdout.Write(Json.Bytes(reply));
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("hive: " + e.Message);
        }
        return 0;
    }

    // One hook event: what it says back to Claude Code, if anything.
    public static JsonObject? Handle(Store store, string hook, JsonObject input) =>
        hook switch
        {
            "session-start" => SessionStart(store, input),
            "subagent-start" => SubagentStart(store, input),
            "subagent-stop" => Stop(store, Json.Str(input["agent_id"]), input, subagent: true),
            "stop" => Stop(store, Json.Str(input["session_id"]), input),
            "session-end" => EndTalk(store, Json.Str(input["session_id"])),
            "pre-tool-use" => PreToolUse(store, input),
            _ => null,
        };

    static Bee? BeeFor(Store store, JsonObject input) =>
        Json.Str(input["agent_type"]) is { Length: > 0 } agent && !agent.Contains(':')
            ? store.Bees().FirstOrDefault(b => Agents.Name(b.Name) == agent)
            : null;

    static string Cwd(JsonObject input) => Json.Str(input["cwd"]) is { Length: > 0 } cwd ? cwd : Environment.CurrentDirectory;

    static JsonObject Context(string hookEvent, string text) => new()
    {
        ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = hookEvent, ["additionalContext"] = text },
    };

    static JsonObject SessionStart(Store store, JsonObject input)
    {
        if (BeeFor(store, input) is not { } bee) return Context("SessionStart", store.Brief(null, null, Cwd(input)));
        // Only sessions the Hive started for a task are held to saving a
        // worklog; a chat you open as a bee is yours to steer.
        if (Json.Str(input["session_title"]).StartsWith("Hive: ")) Track(store, Json.Str(input["session_id"]), bee);
        else Talking(store, Json.Str(input["session_id"]), bee);
        return Context("SessionStart", store.Brief(null, bee.Name, Cwd(input)));
    }

    static JsonObject? SubagentStart(Store store, JsonObject input)
    {
        if (BeeFor(store, input) is not { } bee) return null;
        // Claude Code starts a subagent again each time it wakes from its own
        // background work: it keeps its first start, so what it saved before
        // still counts.
        var key = Json.Str(input["agent_id"]);
        Track(store, key, bee, Ended(store, key)?.Since, parent: Json.Str(input["session_id"]), folder: Cwd(input));
        return Context("SubagentStart", store.Brief(null, bee.Name, Cwd(input)));
    }

    static JsonObject? Stop(Store store, string key, JsonObject input, bool subagent = false)
    {
        StillTalking(store, key);
        if (Untrack(store, key) is not { } tracked) return null;
        var (beeName, since, _) = tracked;
        var bee = store.FindBee(beeName);
        // Asked once in a subagent's life, however often it wakes up again.
        var asked = input["stop_hook_active"] is JsonValue v && v.TryGetValue<bool>(out var active) && active
                    || subagent && Ended(store, key) is { Asked: true };
        var block = bee != null && bee.Doc.Get("deposit_before_stop") == "yes" && bee.Doc.Get("role") != "reviewer" && !asked && !store.SavedSince(bee.Name, since);
        if (subagent) End(store, key, since, asked || block);
        if (!block || bee == null) return null;
        Track(store, key, bee, since, tracked.More); // asked once; the next stop goes through
        return new JsonObject
        {
            ["decision"] = "block",
            ["reason"] = $"Nothing was saved to the hive this session, and {bee.Name}'s card has deposit_before_stop: yes. hive_deposit (bee: \"{bee.Name}\") before you stop.",
        };
    }

    // Runs before every Bash call in every session, so the main session and
    // other agents (no agent_type, or one that isn't a reviewer) leave at
    // once. A deny holds in every permission mode, bypassPermissions too.
    static JsonObject? PreToolUse(Store store, JsonObject input)
    {
        if (Json.Str(input["tool_name"]) != "Bash" || Json.Str(input["agent_type"]).Length == 0
            || BeeFor(store, input) is not { } bee || !Agents.ReadsOnly(bee))
            return null;
        if (ReadOnlyRefusal(Json.Str((input["tool_input"] as JsonObject)?["command"])) is not { } why) return null;
        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = "deny",
                ["permissionDecisionReason"] = why,
            },
        };
    }

    // The Bash a reviewer may run: git log, diff, show, status and blame, ls,
    // wc and du, one command at a time. No shell operators, so nothing is
    // chained, piped, redirected or substituted, and no git option that
    // writes a file (--output) or runs a program (--ext-diff), however it is
    // quoted or shortened. Null when it may run; else why not, for the bee.
    public static string? ReadOnlyRefusal(string command)
    {
        const string Only = "As a reviewer you have read-only Bash: ";
        if (command.IndexOfAny([';', '&', '|', '<', '>', '`', '$', '\n', '\r']) >= 0)
            return Only + "one command at a time, with no ;, &&, ||, pipes, redirects, $ or backticks. Run it on its own: its whole output comes back to you.";
        if (Words(command) is not { } words)
            return Only + "this command has an unclosed quote.";
        if (words is not (["git", "log" or "diff" or "show" or "status" or "blame", ..] or ["ls" or "wc" or "du", ..]))
            return Only + "only git log, diff, show, status and blame, ls, wc and du. Use Read, Grep and Glob for anything else.";
        if (words.Any(w => w.Split('=')[0] is { Length: > 2 } name && ("--output".StartsWith(name) || "--ext-diff".StartsWith(name))))
            return Only + "no --output or --ext-diff. Leave it out: the output comes back to you.";
        return null;
    }

    // A command's words as the shell reads them: quotes and backslashes taken
    // out. Null for an unclosed quote. (The reviewers' $ and backticks never
    // get this far; Install's hook commands have none.)
    public static List<string>? Words(string command)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        bool inWord = false;
        char quote = '\0';
        for (int i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote == '\'') { if (c == '\'') quote = '\0'; else word.Append(c); }
            else if (quote == '"')
            {
                if (c == '"') quote = '\0';
                else if (c == '\\' && i + 1 < command.Length && command[i + 1] is '"' or '\\') word.Append(command[++i]);
                else word.Append(c);
            }
            else if (c is ' ' or '\t')
            {
                if (inWord) words.Add(word.ToString());
                word.Clear();
                inWord = false;
            }
            else
            {
                inWord = true;
                if (c is '\'' or '"') quote = c;
                else if (c == '\\' && i + 1 < command.Length) word.Append(command[++i]);
                else word.Append(c);
            }
        }
        if (quote != '\0') return null;
        if (inWord) words.Add(word.ToString());
        return words;
    }

    // Which bee a session or subagent is, and when it started, for the stop check.
    static string StateDir(Store store) => Path.Combine(store.Root, ".state");

    static string StatePath(Store store, string key) =>
        Path.Combine(StateDir(store), new string(key.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_').ToArray()));

    // The bee, when it started, then (a subagent's) the session that spawned it and its folder.
    static void Track(Store store, string key, Bee bee, long? since = null, string rest = "", string parent = "", string folder = "")
    {
        if (key.Length == 0) return;
        Directory.CreateDirectory(StateDir(store));
        // Old bees' files go; the usage the status line saved stays (the window shows it with its date).
        foreach (var old in Directory.EnumerateFiles(StateDir(store)).Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-2)
                                                                             && f != Usage.FilePath(store)))
            File.Delete(old);
        if (parent.Length > 0) rest = $"{parent}\n{folder}\n";
        File.WriteAllText(StatePath(store, key), $"{bee.Name}\n{since ?? DateTime.UtcNow.Ticks}\n{rest}");
    }

    static (string Bee, long Since, string More)? Untrack(Store store, string key)
    {
        if (key.Length == 0 || !File.Exists(StatePath(store, key))) return null;
        var lines = File.ReadAllLines(StatePath(store, key));
        File.Delete(StatePath(store, key));
        var rest = string.Concat(lines.Skip(2).Select(l => l + "\n"));
        return lines.Length >= 2 && long.TryParse(lines[1], out var since) ? (lines[0], since, rest) : null;
    }

    // A subagent that stopped: when it first started, and whether the stop
    // check asked it to save, for when it wakes up again. One line, so the
    // window (which takes a .state file of two lines or more for a bee at
    // work) doesn't count it; Track's cleanup removes it after 2 days.
    static string EndedPath(Store store, string key) => StatePath(store, key) + ".ended";

    static void End(Store store, string key, long since, bool asked)
    {
        if (key.Length > 0) File.WriteAllText(EndedPath(store, key), $"{since}{(asked ? " asked" : "")}\n");
    }

    static (long Since, bool Asked)? Ended(Store store, string key)
    {
        if (key.Length == 0 || !File.Exists(EndedPath(store, key))) return null;
        var line = File.ReadAllText(EndedPath(store, key)).Trim().Split(' ');
        return long.TryParse(line[0], out var since) ? (since, line.Length > 1 && line[1] == "asked") : null;
    }

    // A chat with a bee: started, still going (each turn touches it), over.
    static string TalkPath(Store store, string session) => Path.Combine(store.Root, ".talk", Path.GetFileName(StatePath(store, session)));

    static void Talking(Store store, string session, Bee bee)
    {
        if (session.Length == 0) return;
        Directory.CreateDirectory(Path.Combine(store.Root, ".talk"));
        foreach (var old in Directory.EnumerateFiles(Path.Combine(store.Root, ".talk")).Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-2)))
            File.Delete(old);
        File.WriteAllText(TalkPath(store, session), $"{bee.Name}\n{DateTime.UtcNow.Ticks}\n");
    }

    static void StillTalking(Store store, string session)
    {
        if (session.Length > 0 && File.Exists(TalkPath(store, session))) File.SetLastWriteTimeUtc(TalkPath(store, session), DateTime.UtcNow);
    }

    static JsonObject? EndTalk(Store store, string session)
    {
        if (session.Length > 0 && File.Exists(TalkPath(store, session))) File.Delete(TalkPath(store, session));
        return null;
    }
}
