using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Hive;

// Each bee is also a Claude Code subagent, ~/.claude/agents/<name>.md. The
// card in the hive is the truth; the subagent tells Claude to load it.
static class Agents
{
    const string Marker = "<!-- written by hive";
    const string ReadOnlyTools = "Read, Grep, Glob, WebSearch, WebFetch, mcp__hive__hive_brief, mcp__hive__hive_recall, mcp__hive__hive_read, mcp__hive__hive_deposit";

    static string Dir =>
        Environment.GetEnvironmentVariable("HIVE_AGENTS_DIR") is { Length: > 0 } dir ? dir : Path.Combine(Text.Home, ".claude", "agents");

    public static string Name(string bee) => bee.ToLowerInvariant();
    static string PathOf(string bee) => Path.Combine(Dir, Name(bee) + ".md");
    static bool Ours(string path) => File.Exists(path) && File.ReadAllText(path).Contains(Marker);
    public static bool Exists(string bee) => Ours(PathOf(bee));

    public static void CheckFree(string bee)
    {
        var path = PathOf(bee);
        if (File.Exists(path) && !Ours(path))
            throw new HiveException($"{Text.Tilde(path)} is a subagent hive didn't write; give the bee another name.");
    }

    public static void Write(Bee bee)
    {
        CheckFree(bee.Name);
        var projects = bee.Projects.Count > 0 ? $" Projects: {string.Join(", ", bee.Projects)}." : "";
        var doc = new Doc();
        doc.Set("name", Name(bee.Name));
        doc.Set("description", $"Hive worker bee {bee.Name}. Specialty: {bee.Specialty}.{projects} Hand it tasks in that specialty; it reads and writes the hive on its own.");
        if (bee.Doc.GetList("skills") is { Count: > 0 } skills) doc.SetList("skills", skills);
        // Reviewers and the overseer only read, also when a session spawns them
        // as subagents (without a tools line a subagent gets every tool). A
        // tools line can't narrow Bash: Claude Code 2.1.284 ignores the
        // pattern in an entry such as Bash(git diff *) and gives the whole
        // Bash tool, and a disallowedTools pattern drops Bash altogether. The
        // hive's PreToolUse hook narrows it instead (Hooks.ReadOnlyRefusal,
        // in every permission mode), so the file gets plain Bash only while
        // that hook is in settings.json; without it, none, and a read task
        // gets Bash back through ReadTaskAgent.
        if (ReadsOnly(bee)) doc.Set("tools", ReadOnlyTools + (Install.BashHookInstalled() ? ", Bash" : ""));
        doc.Body = $"""
            {Marker}: the card at {Text.Tilde(bee.Path)} is the source; this file is rewritten from it -->

            You are **{bee.Name}**, a bee in the user's Hive, their memory on this computer, which you reach through the hive tools.
            Specialty: {bee.Specialty}.{projects}

            Your briefing (your card, your projects, the hive's lessons and recent work) is handed to you as you start; if you
            don't see it, call `hive_brief` with bee "{bee.Name}". Your card is how you work.

            You save to the hive with `hive_deposit` (bee: "{bee.Name}"). Changes to your card go through `bee_molt` (bee and by:
            "{bee.Name}"); {(bee.MoltsFreely ? "they apply at once." : "they wait for the user's approval.")}
            """;
        doc.Save(PathOf(bee.Name));
    }

    // The reviewers and the overseer: their files have a read-only tools line
    // and the PreToolUse hook narrows their Bash.
    public static bool ReadsOnly(Bee bee) => bee.Doc.Get("role") is "reviewer" or "overseer";

    // A read task runs in dontAsk, where only Tasks' read-only rules let a
    // Bash command through (git log, diff and show, not their --output), so
    // there a bee whose subagent file leaves Bash out may have it: this
    // redefines the agent for that one session (`--agents`, which wins over
    // ~/.claude/agents), with the file's description, prompt and tools plus
    // Bash. Null for a bee whose file has no tools line, or has Bash already
    // (a reviewer's, written while the hive's PreToolUse hook is installed).
    public static string? ReadTaskAgent(string bee)
    {
        if (!Ours(PathOf(bee))) return null;
        var file = Doc.Load(PathOf(bee));
        var tools = file.Get("tools").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tools.Length == 0 || tools.Contains("Bash")) return null;
        var agent = new JsonObject
        {
            ["description"] = file.Get("description"),
            ["prompt"] = file.Body,
            ["tools"] = new JsonArray([.. tools.Append("Bash").Select(t => (JsonNode)t)]),
        };
        if (file.GetList("skills") is { Count: > 0 } skills) agent["skills"] = new JsonArray([.. skills.Select(s => (JsonNode)s)]);
        return new JsonObject { [Name(bee)] = agent }.ToJsonString();
    }

    public static void Remove(string bee)
    {
        if (Ours(PathOf(bee))) File.Delete(PathOf(bee));
    }

    // A session with you, run as the bee: `claude --agent <bee>` in the
    // project's folder, taking over this terminal. (The window talks to a bee
    // in its own talk terminal, TalkView, so it starts no terminal program.)
    public static int Talk(Store store, Bee bee, string? project)
    {
        var folder = Text.Home;
        var p = project is { Length: > 0 } ? store.FindProject(project) : bee.Projects.Select(store.FindProject).FirstOrDefault(x => x != null);
        if (p?.Paths.FirstOrDefault() is { } path && Directory.Exists(Text.Expand(path))) folder = Text.Expand(path);
        var info = new ProcessStartInfo(Text.Which("claude")) { WorkingDirectory = folder, UseShellExecute = false };
        foreach (var a in new[] { "--agent", Name(bee.Name), "-n", bee.Name }) info.ArgumentList.Add(a);
        using var proc = Process.Start(info) ?? throw new HiveException("Couldn't start the session.");
        proc.WaitForExit();
        return proc.ExitCode;
    }

    public static List<string> RemoveAll()
    {
        if (!Directory.Exists(Dir)) return [];
        var removed = Directory.EnumerateFiles(Dir, "*.md").Where(Ours).ToList();
        foreach (var file in removed) File.Delete(file);
        return removed;
    }
}

// Connects the hive to Claude Code: the MCP server, the hooks (briefings for
// sessions and bees, and the deposit check when a bee whose card asks for it
// stops) and the bees' subagents. Claude Code's own auto memory is turned off
// only when the user says so (AutoMemory).
static class Install
{
    static string Settings => Path.Combine(Text.Home, ".claude", "settings.json");
    internal static string Data => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d && Path.IsPathRooted(d) ? d : Path.Combine(Text.Home, ".local", "share");
    static string Launcher => Path.Combine(Data, "applications", Hive.Gui.App.Id + ".desktop");
    static string Icon => Path.Combine(Data, "icons", "hicolor", "scalable", "apps", Hive.Gui.App.Id + ".svg");
    // Seven cells: the Queen in gold, her bees around her.
    const string IconSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
          <defs>
            <linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#f6c453"/><stop offset="1" stop-color="#d9901c"/></linearGradient>
            <polygon id="h" points="0,-19 16.45,-9.5 16.45,9.5 0,19 -16.45,9.5 -16.45,-9.5"/>
          </defs>
          <rect x="4" y="4" width="120" height="120" rx="28" fill="#1c1a17"/>
          <g fill="#3a352d" stroke="#5c5346" stroke-width="2">
            <use href="#h" x="47.5" y="35.5"/><use href="#h" x="80.5" y="35.5"/>
            <use href="#h" x="31" y="64"/><use href="#h" x="97" y="64"/>
            <use href="#h" x="47.5" y="92.5"/><use href="#h" x="80.5" y="92.5"/>
          </g>
          <use href="#h" x="64" y="64" fill="url(#g)" stroke="#ffe3a1" stroke-width="2"/>
        </svg>
        """;

    // Where install notes that it turned auto memory off, so uninstall only
    // turns back on what install turned off (outside the hive, like the launcher).
    static string AutoMemoryMark => Path.Combine(Data, "hive", "auto-memory-off");

    // autoMemory: "off" turns Claude Code's auto memory off, "keep" leaves
    // it; null asks on a terminal (no by default) and leaves it otherwise.
    public static void Run(Store store, Action<string> say, string? autoMemory = null)
    {
        if (autoMemory is not (null or "off" or "keep")) throw new HiveException("--auto-memory is off or keep.");
        var settings = ReadSettings(); // a settings.json that can't be read stops it before anything changes
        var alreadyOff = settings["autoMemoryEnabled"] is JsonValue am && am.TryGetValue<bool>(out var on) && !on;
        var turnOff = !alreadyOff && (autoMemory ?? AskAutoMemory()) == "off";
        if (!store.Exists) { store.Init(); say($"Made the hive at {Text.Tilde(store.Root)}."); }
        var bin = Environment.ProcessPath ?? throw new HiveException("Can't tell where the hive binary is.");
        var home = Environment.GetEnvironmentVariable("HIVE_HOME");

        Claude("mcp", "remove", "--scope", "user", "hive");
        var add = new List<string> { "mcp", "add" };
        if (home is { Length: > 0 }) add.AddRange(["-e", "HIVE_HOME=" + store.Root]);
        add.AddRange(["--scope", "user", "hive", "--", bin, "mcp"]);
        if (Claude([.. add]) != 0) throw new HiveException("`claude mcp add` failed.");
        say("Added the hive MCP server to Claude Code (user scope).");

        var prefix = (home is { Length: > 0 } ? $"HIVE_HOME={Shell(store.Root)} " : "") + Shell(bin) + " hook ";
        EditSettings(root =>
        {
            var hooks = root["hooks"] as JsonObject ?? [];
            root["hooks"] = hooks;
            foreach (var (ev, arg) in Hooks.Installed)
            {
                if (hooks[ev] is not JsonArray list) hooks[ev] = list = [];
                var entry = new JsonObject();
                if (ev == "PreToolUse") entry["matcher"] = "Bash"; // only Bash calls start it
                entry["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = prefix + arg, ["timeout"] = 10 });
                list.Add((JsonNode)entry);
            }
            if (turnOff) root["autoMemoryEnabled"] = false;
            if (AddStatusLine(root, prefix.Replace(" hook ", " statusline")))
                say("Set Claude Code's status line to `hive statusline`, so the Hive window can show your usage limits.");
            else say("You have a status line of your own, so it stays; the Hive window shows no usage limits without `hive statusline`.");
        });
        say($"Added the hive's hooks to {Text.Tilde(Settings)}: briefings for every session and bee, a reminder to save for bees whose card asks for one (deposit_before_stop: yes), and read-only Bash for reviewers and the overseer.");
        if (turnOff)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AutoMemoryMark)!);
            File.WriteAllText(AutoMemoryMark, "hive install set autoMemoryEnabled: false in Claude Code's settings.json\n");
            say("Turned off Claude Code's own auto memory, so \"remember this\" goes to the hive only. `hive uninstall` turns it back on.");
        }
        else say(alreadyOff ? "Claude Code's own auto memory is already off; left as it is."
                            : "Left Claude Code's own auto memory as it is (`hive install --auto-memory off` turns it off).");

        var bees = store.Bees();
        foreach (var bee in bees) Agents.Write(bee);
        say(bees.Count > 0 ? $"Wrote {bees.Count} bee subagents." : "No bees yet: `hive new-bee <Name> --specialty \"...\"`, or + new in the window (`hive app`).");

        var exec = (home is { Length: > 0 } ? $"env HIVE_HOME={Shell(store.Root)} " : "") + Shell(bin) + " app";
        Directory.CreateDirectory(Path.GetDirectoryName(Launcher)!);
        File.WriteAllText(Launcher, $"""
            [Desktop Entry]
            Type=Application
            Name=Hive
            Comment=Claude's long-term memory: the Queen, the bees and what they learned
            Exec={exec}
            Icon={Hive.Gui.App.Id}
            Categories=Utility;Development;
            StartupWMClass={Hive.Gui.App.Id}
            Terminal=false

            """);
        Directory.CreateDirectory(Path.GetDirectoryName(Icon)!);
        File.WriteAllText(Icon, IconSvg);
        say("Added Hive to your app launcher.");

    }

    public static void Remove(Action<string> say)
    {
        ReadSettings();
        // Without claude on PATH the rest is still taken out.
        var server = "the MCP server, ";
        try { Claude("mcp", "remove", "--scope", "user", "hive"); }
        catch (HiveException e)
        {
            say($"{e.Message} The hive's MCP server stays in Claude Code's config until you run `claude mcp remove --scope user hive`.");
            server = "";
        }
        // Auto memory comes back on only if install turned it off (its mark);
        // one the user turned off stays off.
        var ours = File.Exists(AutoMemoryMark);
        EditSettings(root =>
        {
            if (ours) root.Remove("autoMemoryEnabled");
            if ((root["statusLine"] as JsonObject)?["command"] is JsonValue v && v.TryGetValue<string>(out var c) && IsOurStatusLine(c)) root.Remove("statusLine");
        });
        var removed = Agents.RemoveAll();
        if (File.Exists(Launcher)) File.Delete(Launcher);
        if (File.Exists(Icon)) File.Delete(Icon);
        if (ours) File.Delete(AutoMemoryMark);
        say($"Removed {server}the hooks, the launcher entry and {removed.Count} bee subagents{(ours ? ", and turned Claude Code's auto memory back on" : "")}. The hive's files are untouched.");
    }

    // On a terminal: one question, no by default. Otherwise "keep".
    static string AskAutoMemory()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected) return "keep";
        Console.Write("Turn off Claude Code's own auto memory, so \"remember this\" goes only to the hive? [y/N] ");
        return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes" ? "off" : "keep";
    }

    // Claude Code's settings.json; one it can't read stops install and
    // uninstall with a message that names it.
    static JsonObject ReadSettings()
    {
        if (!File.Exists(Settings)) return [];
        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(Settings)); }
        catch (System.Text.Json.JsonException e)
        {
            throw new HiveException($"{Text.Tilde(Settings)} isn't valid JSON ({e.Message}). Fix it, then run this again.");
        }
        return root as JsonObject ?? (root == null ? [] : throw new HiveException($"{Text.Tilde(Settings)} isn't a JSON object. Fix it, then run this again."));
    }

    // Drops every hook of ours, then lets `change` add them back (or not).
    static void EditSettings(Action<JsonObject> change)
    {
        var root = ReadSettings();
        if (File.Exists(Settings) && !File.Exists(Settings + ".before-hive")) File.Copy(Settings, Settings + ".before-hive");
        if (root["hooks"] is JsonObject hooks)
            foreach (var ev in hooks.Select(h => h.Key).ToList())
                if (hooks[ev] is JsonArray list)
                {
                    for (int i = list.Count - 1; i >= 0; i--)
                        if ((list[i] as JsonObject)?["hooks"] is JsonArray inner && inner.Any(IsOurs)) list.RemoveAt(i);
                    if (list.Count == 0) hooks.Remove(ev);
                }
        change(root);
        if (root["hooks"] is JsonObject { Count: 0 }) root.Remove("hooks");

        Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
        File.WriteAllBytes(Settings, [.. Json.Bytes(root, indented: true), (byte)'\n']);
    }

    // Whether settings.json has `hive hook pre-tool-use`, which narrows the
    // reviewers' Bash; only then do their subagent files get Bash. A hook
    // whose binary is gone counts as none: Claude Code takes a hook that
    // can't start as a non-blocking error and runs the command (checked
    // 2026-10-21). A settings.json that can't be read counts as no.
    public static bool BashHookInstalled()
    {
        try
        {
            return ReadSettings()["hooks"] is JsonObject hooks && hooks["PreToolUse"] is JsonArray list
                && list.Any(e => (e as JsonObject)?["hooks"] is JsonArray inner && inner.Any(h =>
                       IsOurs(h) && Hooks.Words(Json.Str((h as JsonObject)?["command"])) is [.., var bin, "hook", "pre-tool-use"] && File.Exists(bin)));
        }
        catch (HiveException) { return false; }
    }

    // The status line goes in only where there's none: one the user set stays.
    // An older one of ours is replaced (the binary may have moved).
    public static bool AddStatusLine(JsonObject root, string command)
    {
        if (root["statusLine"] is { } old && !(old is JsonObject o && o["command"] is JsonValue v && v.TryGetValue<string>(out var c) && IsOurStatusLine(c))) return false;
        root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = command };
        return true;
    }

    static bool IsOurStatusLine(string command) => command.Contains("hive") && command.EndsWith(" statusline");

    // Ours: `…hive brief --hook` (the first version) or `…hive hook <event>`.
    static bool IsOurs(JsonNode? hook) =>
        (hook as JsonObject)?["command"] is JsonValue v && v.TryGetValue<string>(out var c) && c.Contains("hive")
        && (c.EndsWith(" brief --hook") || Hooks.Installed.Any(h => c.EndsWith(" hook " + h.Arg)));

    static string Shell(string s) => s.All(c => char.IsAsciiLetterOrDigit(c) || "/._-+~".Contains(c)) ? s : "'" + s.Replace("'", "'\\''") + "'";

    static int Claude(params string[] args)
    {
        var info = new ProcessStartInfo(Text.Which("claude")) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(info)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new HiveException("The claude CLI isn't on PATH.");
        }
    }
}
