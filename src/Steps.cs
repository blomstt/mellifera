using System.Text.Json.Nodes;

namespace Hive;

// What a bee is doing, step by step. A task's session runs with
// `--output-format stream-json --verbose`, and the runner keeps one line per
// step in .tasks/<id>.steps: "<unix ms>\t<tool>\t<what>", or
// "<unix ms>\t=<tool>\tok|error" when a tool's result comes back. The window
// reads the newest to say what the bee does and where it goes in the garden.
static class Steps
{
    // Where a bee goes for a step: the flower bed (edits and builds), the
    // test comb, the library (reading), the far field (the web), the
    // workbench (any other command) or the hive (the hive's own tools). A bee
    // that thinks or writes stays where it is.
    public const string Thinking = "thinking", Flowers = "flowers", Comb = "comb", Library = "library", Field = "field", Workbench = "workbench", Hive = "hive";

    public static string Station(string tool, string what)
    {
        switch (tool)
        {
            case "think": return Thinking;
            case "Edit" or "Write" or "MultiEdit" or "NotebookEdit": return Flowers;
            case "Read" or "Grep" or "Glob" or "LS" or "NotebookRead": return Library;
            case "WebSearch" or "WebFetch": return Field;
            case "Bash": return Command(what);
        }
        return tool.StartsWith("hive_") ? Hive : Workbench;
    }

    // A shell command: build, test, read, fetch, or anything else.
    public static string Command(string command)
    {
        var c = command.Trim();
        foreach (var lead in new[] { "time ", "timeout ", "nice " })
            if (c.StartsWith(lead))
            {
                c = c[lead.Length..].TrimStart();
                if (lead == "timeout ") c = c.IndexOf(' ') is var sp and > 0 ? c[(sp + 1)..].TrimStart() : c; // the duration
            }
        // Each a whole word: "ls" is ls and ls -la, not lsof.
        bool Is(params string[] starts) => starts.Any(s =>
        {
            var word = s.TrimEnd();
            return c.StartsWith(word) && (c.Length == word.Length || word[^1] == '/' || c[word.Length] is ' ' or '/');
        });
        if (Is("dotnet test", "cargo test", "pytest", "npm test", "go test", "make test", "make check", "ctest", "dotnet run --project tests", "./tests/", "tests/")
            || c.Contains("tests/run.sh") || c.Contains("--test")) return Comb;
        if (Is("dotnet build", "dotnet publish", "dotnet restore", "./build.sh", "make", "cargo build", "cargo check", "godot --headless", "npm run build", "cmake", "ninja", "msbuild")) return Flowers;
        if (Is("git log", "git diff", "git show", "git blame", "git status", "ls", "wc", "du", "cat ", "head ", "tail ", "grep ", "rg ", "find ", "tree", "file ", "stat ")) return Library;
        if (Is("curl ", "wget ")) return Field;
        return Workbench;
    }

    // What a step says, for the work list and a bee's label: "editing src/Tasks.cs".
    public static string Say(string tool, string what) => tool switch
    {
        "think" => what.Length > 0 ? "thinking: " + what : "thinking",
        "Edit" or "MultiEdit" or "NotebookEdit" => "editing " + what,
        "Write" => "writing " + what,
        "Read" or "NotebookRead" => "reading " + what,
        "Grep" => "searching for " + what,
        "Glob" or "LS" => "looking for " + what,
        "WebSearch" => "searching the web: " + what,
        "WebFetch" => "reading " + what,
        "Bash" => Command(what) switch { Comb => "testing: ", Flowers => "building: ", Library => "reading: ", _ => "running " } + what,
        _ when tool.StartsWith("hive_") => tool switch
        {
            "hive_deposit" => "saving " + what + " to the hive",
            "hive_recall" => "recalling " + what,
            "hive_read" => "reading " + what + " in the hive",
            "hive_tidy" => "tidying the hive",
            _ => tool.Replace('_', ' '),
        },
        "Task" or "Agent" => "asking a helper: " + what,
        "TodoWrite" => "planning",
        _ => tool + (what.Length > 0 ? " " + what : ""),
    };

    // The step lines one stream-json message adds. tools maps a tool call's
    // id to its tool, so its result can say which tool it was.
    public static List<string> Lines(JsonObject message, string folder, Dictionary<string, string> tools, long now)
    {
        var lines = new List<string>();
        if ((message["message"] as JsonObject)?["content"] is not JsonArray content) return lines; // a system message's "message" can be plain text (permission_denied)
        var type = Json.Str(message["type"]);
        foreach (var block in content)
        {
            if (block is not JsonObject b) continue;
            switch (Json.Str(b["type"]))
            {
                case "tool_use" when type == "assistant":
                    var (tool, what) = Describe(Json.Str(b["name"]), b["input"] as JsonObject ?? [], folder);
                    tools[Json.Str(b["id"])] = tool;
                    lines.Add(Line(now, tool, what));
                    break;
                case "thinking" or "text" when type == "assistant":
                    var said = Json.Str(b["thinking"] ?? b["text"]);
                    lines.Add(Line(now, "think", Text.OneLine(said)));
                    break;
                case "tool_result":
                    var error = b["is_error"] is JsonValue v && v.TryGetValue<bool>(out var e) && e;
                    lines.Add(Line(now, "=" + tools.GetValueOrDefault(Json.Str(b["tool_use_id"]), "?"), error ? "error" : "ok"));
                    break;
            }
        }
        return lines;
    }

    static string Line(long now, string tool, string what) => $"{now}\t{tool}\t{Cap(what)}";

    static string Cap(string s)
    {
        s = Text.OneLine(s).Replace('\t', ' ');
        return s.Length > 160 ? s[..159] + "…" : s;
    }

    // A tool call as (tool, what it works on): a path relative to the
    // folder, a pattern, a command, a query or a URL.
    public static (string Tool, string What) Describe(string name, JsonObject input, string folder)
    {
        string Get(string key) => Json.Str(input[key]);
        string PathOf(string key)
        {
            var p = Get(key);
            if (folder.Length > 0 && p.StartsWith(folder.TrimEnd('/') + "/")) return p[(folder.TrimEnd('/').Length + 1)..];
            return Text.Tilde(p);
        }
        if (name.StartsWith("mcp__hive__"))
        {
            var tool = name["mcp__hive__".Length..];
            return (tool, tool switch
            {
                "hive_deposit" => "a " + Get("type"),
                "hive_recall" => Get("query"),
                "hive_read" => Get("id"),
                _ => "",
            });
        }
        return (name, name switch
        {
            "Read" or "Edit" or "Write" or "MultiEdit" or "NotebookEdit" or "NotebookRead" => PathOf(input.ContainsKey("notebook_path") ? "notebook_path" : "file_path"),
            "Grep" => Get("pattern"),
            "Glob" => Get("pattern"),
            "LS" => PathOf("path"),
            "Bash" => Text.OneLine(Get("command")),
            "WebSearch" => Get("query"),
            "WebFetch" => Get("url"),
            "Task" or "Agent" => Get("description") is { Length: > 0 } d ? d : Get("subagent_type"),
            _ => "",
        });
    }

    // One parsed step line: when, which tool ("think", "Edit", "=Bash" for a
    // result), and what.
    public readonly record struct Step(long At, string Tool, string What)
    {
        public bool IsResult => Tool.StartsWith('=');
        public string Station => IsResult ? Steps.Station(Tool[1..], "") : Steps.Station(Tool, What);
        public string Say => Steps.Say(Tool, What);
    }

    public static Step? Parse(string line)
    {
        var parts = line.Split('\t', 3);
        if (parts.Length < 3 || !long.TryParse(parts[0], out var at)) return null;
        return new Step(at, parts[1], parts[2]);
    }
}
