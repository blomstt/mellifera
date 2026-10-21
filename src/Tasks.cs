using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Hive;

record BeeTask(string Id, string Path, Doc Doc)
{
    public string Bee => Doc.Get("bee");
    public string Status => Doc.Get("status");
    public bool Running => Status is "queued" or "waiting" or "running";
    public bool Rewarded => Doc.Get("reward") == "yes";
    public string Prompt => Doc.Body.Trim();
    public string Title => Prompt.Split('\n')[0] is var line && line.Length > 90 ? line[..90] + "…" : line;
    public string ResultPath => System.IO.Path.ChangeExtension(Path, ".result.md");
    public string StepsPath => System.IO.Path.ChangeExtension(Path, ".steps");
    public string Result => File.Exists(ResultPath) ? File.ReadAllText(ResultPath) : "";
}

// A task the user hands a bee: a headless Claude Code session running as that
// bee's subagent (`claude -p --agent <bee>`), in the project's folder. The
// runner is `hive run-task <id>`, started in its own session so it outlives
// the window. Tasks live in .tasks/, outside the search index.
static partial class Tasks
{
    public static readonly string[] Access = ["read", "edit", "all"];
    public static readonly string[] AccessLabels = ["Read and research", "Can edit files and build", "Anything (no permission checks)"];

    // read: files, the web, reading the hive, saving to it and proposing card
    // changes, read-only git and a few read-only commands. edit: also every
    // hive tool, skills (Skill; a skill's allowed-tools front matter allows
    // more while it runs, so edit trusts the installed skills too), edits in
    // its folder, ps, and the commands the hive's settings allow
    // (Store.EditCommands: make and ./build.sh unless they say otherwise).
    // all: no permission checks at all. git's --output writes a file and
    // --ext-diff runs a program, so they're denied wherever git is allowed.
    // These rules narrow Bash only as the session's --allowedTools: as an
    // entry in a subagent file's tools line, Bash(git diff *) is plain Bash.
    // bee_molt is safe in a read task: Run tells the hive server which bee the
    // task runs as, and a molt always counts as that bee's (Mcp, bee_molt), so a
    // change to another bee's card waits for the user, and its own applies at
    // once only if the user lets it molt freely.
    const string HiveReadTools = "mcp__hive__hive_brief,mcp__hive__hive_recall,mcp__hive__hive_read,mcp__hive__hive_deposit,mcp__hive__bee_molt";
    const string ReadTools = "Read,Grep,Glob,WebSearch,WebFetch," + HiveReadTools + ",Bash(git log *),Bash(git diff *),Bash(git show *),Bash(git status *),Bash(git blame *),Bash(ls *),Bash(wc *),Bash(du *)";
    const string DeniedTools = "Bash(git *--output*),Bash(git *--ext-diff*)";
    const string EditTools = ReadTools + ",mcp__hive,Skill,Bash(ps *)";

    // The commands an edit task may run, as saved on the task when it was
    // made (from the hive's settings); a task from before that has the default.
    static List<string> EditCommands(BeeTask task) =>
        task.Doc.Get("edit_commands").Length > 0 ? Store.CleanCommands(task.Doc.GetList("edit_commands")) : [.. Store.DefaultEditCommands];

    static string Dir(Store s) => System.IO.Path.Combine(s.Root, ".tasks");

    public static BeeTask Create(Store store, string beeName, string prompt, string project, string access,
                                 string run = "", string role = "", IEnumerable<string>? waitsFor = null,
                                 string follows = "", string resume = "")
    {
        // The Queen isn't a bee: her turns from the window's work pane (Talk.cs) run as no agent.
        var bee = beeName == Queen ? null : store.RequireBee(beeName);
        prompt = prompt.Trim();
        if (prompt.Length == 0) throw new HiveException("The task is empty.");
        if (access.Length == 0) access = bee?.Doc.Get("access", "read") ?? "edit";
        if (!Access.Contains(access)) throw new HiveException("access is read, edit or all.");
        project = project.Trim().ToLowerInvariant();
        var folder = Text.Home;
        if (project is not ("" or "global"))
        {
            var p = store.FindProject(project) ?? throw new HiveException($"No project is named {project}.");
            if (p.Paths.Count > 0 && Directory.Exists(Text.Expand(p.Paths[0]))) folder = Text.Expand(p.Paths[0]);
        }

        Directory.CreateDirectory(Dir(store));
        var stem = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Agents.Name(bee?.Name ?? Queen)}";
        var doc = new Doc();
        doc.Set("bee", bee?.Name ?? Queen);
        doc.Set("project", project);
        doc.Set("folder", Text.Tilde(folder));
        doc.Set("access", access);
        if (access == "edit") doc.SetList("edit_commands", store.EditCommands()); // what Args lets it run
        if (bee?.Doc.Get("role") is { Length: > 0 } beeRole) doc.Set("bee_role", beeRole); // what Args allows it
        doc.Set("status", "queued");
        doc.Set("created", Text.Now);
        if (run.Length > 0) doc.Set("run", run);
        if (role.Length > 0) doc.Set("role", role);
        if (waitsFor != null) doc.SetList("waits_for", waitsFor);
        if (follows.Length > 0) doc.Set("follows", follows); // Follow up or Hand to: the task it carries on from
        if (resume.Length > 0) doc.Set("resume", resume);    // a Claude session to continue
        doc.Body = prompt + "\n";
        // The id is taken by creating its file (CreateNew), so two processes
        // in the same second can't both take it.
        for (int n = 1; ; n++)
        {
            var id = n == 1 ? stem : $"{stem}-{n}";
            var path = System.IO.Path.Combine(Dir(store), id + ".md");
            try
            {
                using (var f = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write))) f.Write(doc.ToString());
                return new BeeTask(id, path, doc);
            }
            catch (IOException) when (File.Exists(path)) { }
        }
    }

    // Starts the runner in a new session (setsid), so closing the window or
    // the terminal doesn't stop the bee.
    public static void Start(BeeTask task)
    {
        var info = new ProcessStartInfo(Text.Which("setsid")) { UseShellExecute = false };
        foreach (var a in new[] { "-f", Environment.ProcessPath!, "run-task", task.Id }) info.ArgumentList.Add(a);
        using var p = Process.Start(info) ?? throw new HiveException("Couldn't start the task.");
    }

    public static BeeTask Load(Store store, string id)
    {
        var path = System.IO.Path.Combine(Dir(store), id.Trim() + ".md");
        if (!File.Exists(path)) throw new HiveException($"No task {id}. `hive tasks` lists them.");
        return new BeeTask(id.Trim(), path, Doc.Load(path));
    }

    // Newest first. A task whose runner died without saying so is marked stopped.
    public static List<BeeTask> List(Store store, string? bee = null)
    {
        if (!Directory.Exists(Dir(store))) return [];
        var tasks = Directory.EnumerateFiles(Dir(store), "*.md")
            .Where(f => !f.EndsWith(".result.md"))
            .Select(f => new BeeTask(System.IO.Path.GetFileNameWithoutExtension(f), f, Doc.Load(f)))
            .Where(t => bee == null || t.Bee.Equals(bee, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.Id)
            .ToList();
        for (int i = 0; i < tasks.Count; i++)
        {
            var t = tasks[i];
            if (t.Status is not ("running" or "waiting")) continue;
            // A session leads its runs through the hive's MCP server, which can
            // restart midway; those time out instead of following a process.
            var gone = t.Doc.Get("led_by") == "session"
                ? File.GetLastWriteTimeUtc(t.Path) < DateTime.UtcNow.AddHours(-6)
                : int.TryParse(t.Doc.Get("pid"), out var pid) && !IsRunner(pid, t.Id);
            // The runner may have saved done and exited since the file was
            // read: only a task that still says it's running is stopped.
            if (gone && File.Exists(t.Path) && Load(t) is var fresh)
            {
                if (fresh.Running) Finish(fresh, "stopped");
                tasks[i] = fresh;
            }
        }
        return tasks;
    }

    // Whether pid is still this task's runner. After a reboot or a crash the
    // pid can belong to another program, which must be neither trusted nor stopped.
    static bool IsRunner(int pid, string id)
    {
        try
        {
            var args = File.ReadAllText($"/proc/{pid}/cmdline").Split('\0');
            return Array.IndexOf(args, "run-task") is var at and >= 0 && at + 1 < args.Length && args[at + 1] == id;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // Changes whenever a task file is written (Doc.Save renames into place).
    public static long Signature(Store store) =>
        Directory.Exists(Dir(store)) ? Directory.GetLastWriteTimeUtc(Dir(store)).Ticks : 0;

    [LibraryImport("libc.so.6", EntryPoint = "kill")] private static partial int Kill(int pid, int signal);

    public static void Stop(Store store, string id)
    {
        var task = Load(store, id);
        if (!task.Running) return;
        Finish(task, "stopped");
        // The runner leads its own process group; this stops it and its claude.
        // A run a Claude session leads has no runner of its own to stop.
        if (task.Doc.Get("led_by") != "session" && int.TryParse(task.Doc.Get("pid"), out var pid) && IsRunner(pid, task.Id)) Kill(-pid, 15);
    }

    // For runs a session leads: the bee ran as a subagent, and this is its reply.
    public static void Record(Store store, BeeTask task, string result, string status)
    {
        File.WriteAllText(task.ResultPath, result.Trim() + "\n");
        if (task.Doc.Get("started").Length == 0) task.Doc.Set("started", Text.Now);
        Finish(task, status);
    }

    static void Finish(BeeTask task, string status)
    {
        task.Doc.Set("status", status);
        task.Doc.Set("finished", Text.Now);
        task.Doc.Save(task.Path);
    }

    public static int Run(Store store, string id)
    {
        var task = Load(store, id);
        var folder = Text.Expand(task.Doc.Get("folder", "~"));
        var waitsFor = task.Doc.GetList("waits_for");
        task.Doc.Set("pid", Environment.ProcessId.ToString());
        if (waitsFor.Count > 0)
        {
            // The overseer waits for its reviewers to finish, however that goes.
            task.Doc.Set("status", "waiting");
            task.Doc.Save(task.Path);
            while (waitsFor.Select(w => Load(store, w)).Any(w => w.Running))
            {
                Thread.Sleep(3000);
                if (Load(store, id).Status == "stopped") return 1;
            }
            if (waitsFor.Select(w => Load(store, w)).All(w => w.Status != "done"))
            {
                File.WriteAllText(task.ResultPath, "None of the reviewers finished, so there was nothing to oversee.\n");
                Finish(task, "stopped");
                return 1;
            }
        }
        task.Doc.Set("status", "running");
        task.Doc.Set("started", Text.Now);
        task.Doc.Save(task.Path);

        var args = Args(task);
        // The session's hive server: this hive (which may not be ~/Hive), and
        // the bee the task runs as, so a card change it proposes counts as that
        // bee's (Mcp, bee_molt); the Queen runs as no bee, as in a terminal. A
        // --mcp-config server named hive takes the place of the user's own
        // (checked with Claude Code 2.1.284), and the user's other MCP servers stay.
        var env = new JsonObject { ["HIVE_HOME"] = store.Root };
        if (task.Bee != Queen) env[Mcp.TaskBeeVariable] = task.Bee;
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["hive"] = new JsonObject
                {
                    ["command"] = Environment.ProcessPath,
                    ["args"] = new JsonArray("mcp"),
                    ["env"] = env,
                },
            },
        };
        args.AddRange(["--mcp-config", config.ToJsonString()]);

        var where = $"You are working in {Text.Tilde(folder)}{(task.Doc.Get("project") is { Length: > 0 } p ? $", project {p}" : "")}";
        where += task.Doc.Get("access", "read") switch
        {
            "read" => ". You can read files (Read, Grep, Glob), use the web and the hive, and run read-only commands: git log, diff, show, status or blame, ls, wc and du. Run one command per call, with no && or pipes. Nothing else runs, so don't try to build or run code",
            "edit" => $". You can edit files in this folder, use skills, and run: {string.Concat(EditCommands(task).Select(c => c + "; "))}read-only git; ls, wc, du and ps. Run one command per call, with no && or pipes. Nothing else runs",
            _ => "",
        };
        var prompt = task.Doc.Get("role") switch
        {
            "queen" => QueenPrompt(task, where),
            "reviewer" => $"{task.Prompt}\n\n({where}. Task id {task.Id}. Nobody is watching this session live, so questions go unanswered. Your reply is your report.)",
            "overseer" => $"{task.Prompt}\n\n({where}. Task id {task.Id}. Nobody is watching this session live, so questions go unanswered.)\n\n{Reviews.Reports(store, task)}",
            _ => $"{task.Prompt}\n\n(A task sent from the Hive. Task id {task.Id}. {where}. Nobody is watching this session live, so questions go unanswered; your reply is the task's result.)",
        };

        string result, status = "failed";
        try
        {
            var info = new ProcessStartInfo(Text.Which("claude"))
            {
                WorkingDirectory = folder,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) info.ArgumentList.Add(a);
            using var claude = Process.Start(info)!;
            var stderr = claude.StandardError.ReadToEndAsync();
            claude.StandardInput.Write(prompt);
            claude.StandardInput.Close();
            // One JSON message per line as the session goes: each step goes
            // to the task's step log, which the window follows; the last
            // line is the result.
            var (json, other) = Follow(claude.StandardOutput, task, folder);
            claude.WaitForExit();

            result = stderr.Result.Trim();
            if (json != null && json["result"] != null)
            {
                result = Json.Str(json["result"]);
                if (json["is_error"] is JsonValue e && e.TryGetValue<bool>(out var isError) && !isError && claude.ExitCode == 0) status = "done";
                if (Json.Str(json["session_id"]) is { Length: > 0 } session) task.Doc.Set("session", session);
                if (json["duration_ms"] is JsonValue d && d.TryGetValue<double>(out var ms)) task.Doc.Set("took", Took(ms));
            }
            else if (result.Length == 0) result = other.Trim();
        }
        catch (Exception e)
        {
            result = "The task couldn't run: " + e.Message;
        }

        // Stop may have marked it while it ran; keep that.
        if (Load(store, id).Status == "stopped") status = "stopped";
        File.WriteAllText(task.ResultPath, result.Trim() + "\n");
        Finish(task, status);
        // A team review's report is saved: its reviewers' honey or misses.
        if (status == "done" && task.Doc.Get("role") == "overseer")
            try { Reviews.Award(store, Reviews.Find(store, task.Doc.Get("run"))); }
            catch (HiveException) { }
        return status == "done" ? 0 : 1;
    }

    // The claude command line for a task: its bee, stream-json, its access,
    // and the session it continues (a Follow up). The Queen's turns run as
    // no agent (so the session gets the Queen's briefing) and may always use
    // the Agent tool, to hand work to the bees.
    public static List<string> Args(BeeTask task)
    {
        // A talk isn't a task: its title doesn't start "Hive: ", so the hooks
        // count it as a chat with you (no worklog asked for at each turn).
        var queen = task.Doc.Get("role") == "queen";
        var title = task.Doc.Get("role") is "talk" or "queen" ? $"Hive talk: {task.Bee}" : $"Hive: {task.Bee} · {task.Title}";
        var args = new List<string> { "-p" };
        if (!queen) args.AddRange(["--agent", Agents.Name(task.Bee)]);
        args.AddRange(["--output-format", "stream-json", "--verbose", "-n", title]);
        if (task.Doc.Get("resume") is { Length: > 0 } session) args.AddRange(["--resume", session]);
        if (task.Doc.Get("model") is { Length: > 0 } model) args.AddRange(["--model", model]); // the pane's pick for a Queen turn
        var delegates = queen ? ",Agent" : "";
        switch (task.Doc.Get("access", "read"))
        {
            case "all": args.Add("--dangerously-skip-permissions"); break;
            case "edit":
                var allowed = EditTools + string.Concat(EditCommands(task).Select(c => $",Bash({c} *)"));
                args.AddRange(["--permission-mode", "acceptEdits", "--permission-prompts", "none", "--allowedTools", allowed + delegates, "--disallowedTools", DeniedTools]);
                break;
            default:
                // Roles that need more of the hive (the bee's role when the task
                // was made): evolver reads a bee's record; keeper tidies and sets
                // project briefs.
                var tools = task.Doc.Get("bee_role") switch
                {
                    "evolver" => ReadTools + ",mcp__hive__bee_review",
                    "keeper" => ReadTools + ",mcp__hive__hive_tidy,mcp__hive__project_set",
                    _ => ReadTools,
                };
                // Reviewers and the overseer: their subagent files leave Bash
                // out, so their read-only git comes back for this session only.
                if (!queen && Agents.ReadTaskAgent(task.Bee) is { } agent) args.AddRange(["--agents", agent]);
                args.AddRange(["--permission-mode", "dontAsk", "--allowedTools", tools + delegates, "--disallowedTools", DeniedTools]);
                break;
        }
        return args;
    }

    // Follow up: the same bee carries on in the same Claude session, as a
    // new task linked to the first (Start it after).
    public static BeeTask FollowUp(Store store, BeeTask from, string prompt)
    {
        if (from.Running) throw new HiveException("It's still working on it; follow up once it's done.");
        if (from.Doc.Get("session") is not { Length: > 0 } session) throw new HiveException("That task has no session to continue.");
        if (List(store, from.Bee).Exists(t => t.Running && (t.Doc.Get("resume") == session || t.Doc.Get("session") == session)))
            throw new HiveException("It's already carrying on in that session; follow up once that's done.");
        return Create(store, from.Bee, prompt, from.Doc.Get("project"), from.Doc.Get("access"), follows: from.Id, resume: session);
    }

    // Hand to: another bee takes it from here, with the first one's result attached.
    public static BeeTask HandTo(Store store, BeeTask from, string beeName, string prompt)
    {
        if (from.Running) throw new HiveException("It's still working on it; hand it on once it's done.");
        var result = from.Result.Trim();
        var text = (prompt.Trim().Length > 0 ? prompt.Trim() : "Carry on from here.")
            + $"\n\n(Handed to you from {from.Bee}'s task {from.Id}: \"{from.Title}\". Its result:)\n\n"
            + (result.Length > 0 ? result : "(no result was recorded)");
        return Create(store, beeName, text, from.Doc.Get("project"), "", follows: from.Id);
    }

    // Reads the session's stream-json: appends each step to the task's step
    // log, notes the session id as soon as it's known (so the task can be
    // resumed while it runs), and returns the result message and any lines
    // that weren't JSON.
    public static (JsonObject? Result, string Other) Follow(TextReader stdout, BeeTask task, string folder)
    {
        JsonObject? final = null;
        var other = new System.Text.StringBuilder();
        var tools = new Dictionary<string, string>();
        string? line;
        while ((line = stdout.ReadLine()) != null)
        {
            if (line.Trim().Length == 0) continue;
            JsonObject? message;
            try { message = JsonNode.Parse(line) as JsonObject; }
            catch (System.Text.Json.JsonException) { other.AppendLine(line); continue; }
            if (message == null) continue;
            switch (Json.Str(message["type"]))
            {
                case "result": final = message; break;
                case "system" when Json.Str(message["subtype"]) == "init" && Json.Str(message["session_id"]) is { Length: > 0 } session && task.Doc.Get("session") != session:
                    // The task file may have been marked stopped meanwhile; keep that.
                    var now = Load(task);
                    now.Doc.Set("session", session);
                    now.Doc.Save(now.Path);
                    task.Doc.Set("session", session);
                    break;
                default:
                    var steps = Steps.Lines(message, folder, tools, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    if (steps.Count > 0) File.AppendAllText(task.StepsPath, string.Join("\n", steps) + "\n");
                    break;
            }
        }
        return (final, other.ToString());
    }

    static BeeTask Load(BeeTask task) => new(task.Id, task.Path, Doc.Load(task.Path));

    // The bee with the evolver role reviews a bee as a task; what it proposes
    // waits in the inbox (or, for a small rule, applies at once: Store.Molt).
    // auto: the hive started it (Store.AutoEvolve), not the user.
    public static BeeTask Evolve(Store store, string beeName, bool auto = false)
    {
        var bee = store.RequireBee(beeName);
        var evolver = store.RoleBee("evolver") ?? throw new HiveException("No bee has the evolver role yet (`hive bee <Name> --role evolver`).");
        var task = Tasks.Create(store, evolver.Name,
            $"Review the bee {bee.Name}. bee_review (bee: \"{bee.Name}\") has its record. Card changes go through bee_molt (bee: \"{bee.Name}\", by: \"{evolver.Name}\")."
            + (auto ? $"\n\n(The hive started this review: {bee.Name} has {Store.EvolveAfterFor(evolver)} new honey or miss outcomes since its last one.)" : ""),
            "", "read");
        task.Doc.Set("reviews", bee.Name);
        if (auto) task.Doc.Set("started_by", "hive");
        task.Doc.Save(task.Path);
        Tasks.Start(task);
        return task;
    }

    // Whether an evolver's task reviews this bee (one from before "reviews:" by its prompt).
    public static bool IsReviewOf(BeeTask t, string bee) =>
        t.Doc.Get("reviews") is { Length: > 0 } of ? of.Equals(bee, StringComparison.OrdinalIgnoreCase) : t.Prompt.StartsWith($"Review the bee {bee}.", StringComparison.OrdinalIgnoreCase);

    // Honey for a finished task, or a miss for one that's over however it
    // went (Store.RewardEntry).
    public static void Reward(Store store, string id, string note, bool give, string by = "user", bool miss = false)
    {
        var task = Load(store, id);
        if (give && !miss && task.Status != "done") throw new HiveException("Only a finished task can earn honey.");
        if (give && task.Running) throw new HiveException("It's still working on it; judge it once it's done.");
        Store.SetReward(task.Doc, note, give, by, miss);
        task.Doc.Save(task.Path);
        if (give) store.AutoEvolve(task.Bee);
    }

    // For hive_read task:<id>: what was asked, how it went, and the result.
    public static string Describe(Store store, string id)
    {
        var t = Load(store, id);
        return $"""
            [task:{t.Id}] {t.Bee} · {t.Status}{(t.Doc.Get("took") is { Length: > 0 } took ? " in " + took : "")} · {t.Doc.Get("created")}{t.Doc.Get("reward") switch { "yes" => " · earned honey", "no" => " · a miss", _ => "" }}{(t.Doc.Get("reward").Length > 0 ? $" from {t.Doc.Get("reward_by", "user")}" + (t.Doc.Get("reward_note") is { Length: > 0 } n ? $": \"{n}\"" : "") : "")}

            ## Task

            {t.Prompt}

            ## Result

            {(t.Running ? "Still working on it." : t.Result.Trim())}
            """;
    }

    static string Took(double ms) =>
        ms < 60_000 ? $"{ms / 1000:0} s" : ms < 3_600_000 ? $"{ms / 60_000:0} min" : $"{ms / 3_600_000:0.0} h";
}
