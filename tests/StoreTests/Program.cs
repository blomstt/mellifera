// Checks for the Store (and other code under src/ that runs without GTK).
// Run: dotnet run --project tests/StoreTests
// Each check prints PASS or FAIL; the exit code is 1 if any check fails.
// "#n" in a name is the finding number in the team review review rv-c
// (the Hive window, stage 2); "bloat n" is its bloat item n. A "control" check
// is the same setup without the bug's trigger, so a failure is known to come
// from the trigger and not from the setup.
// Every hive here is a fresh one in a temp folder; nothing touches ~/Hive or
// ~/.claude (HIVE_AGENTS_DIR points the bees' subagent files at the temp folder).
// A check that prints ERROR (not FAIL) didn't reach what it tests: its setup
// failed, which says nothing about the bug.
using System.Diagnostics;
using System.Text.RegularExpressions;
using Hive;
using JObj = System.Text.Json.Nodes.JsonObject;
using JArr = System.Text.Json.Nodes.JsonArray;

// The rv-a #21 check runs this program as `hive mcp` would run the server.
if (args is ["mcp"]) return Mcp.Serve(new Store(Environment.GetEnvironmentVariable("HIVE_HOME")!));
// Tasks.Start runs `<this program> run-task <id>` in a new session (an Sage
// review the hive starts by itself after 5 outcomes, say); here that does
// nothing, so no claude ever starts and the checks don't run again.
if (args is ["run-task", ..])
{
    // A check that counts what got started sets HIVE_TEST_RUNTASK_LOG.
    if (Environment.GetEnvironmentVariable("HIVE_TEST_RUNTASK_LOG") is { Length: > 0 } started) File.AppendAllText(started, string.Join(' ', args[1..]) + "\n");
    return 0;
}

// The auto-memory checks run `hive install` as this program in a process of
// its own (on a pty or not), so its question sees a terminal or doesn't.
if (args is ["install-child", ..])
{
    // Only ever inside a check's sandbox: HOME and XDG_DATA_HOME in its temp folder.
    var sandbox = Environment.GetEnvironmentVariable("HIVE_TEST_SANDBOX");
    if (sandbox is not { Length: > 0 } || !Path.GetFullPath(Text.Home).StartsWith(sandbox + "/") || !Path.GetFullPath(Install.Data).StartsWith(sandbox + "/"))
    {
        Console.WriteLine($"REFUSED not in a test sandbox (HOME {Text.Home}, data {Install.Data})");
        return 3;
    }
    try { Install.Run(new Store(Environment.GetEnvironmentVariable("HIVE_HOME")!), Console.WriteLine, args.Length > 1 ? args[1] : null); Console.WriteLine("DONE"); return 0; }
    catch (HiveException e) { Console.WriteLine("ERROR " + e.Message); return 1; }
}

var scratch = Path.Combine(Path.GetTempPath(), $"hive-storetests-{Environment.ProcessId}");
Directory.CreateDirectory(scratch);
Environment.SetEnvironmentVariable("HIVE_AGENTS_DIR", Path.Combine(scratch, "agents"));
Environment.SetEnvironmentVariable("HIVE_HOME", Path.Combine(scratch, "unused-home"));
// hive install and uninstall edit ~/.claude and ~/.local/share: keep them in here.
Environment.SetEnvironmentVariable("HOME", Path.Combine(scratch, "home"));
Directory.CreateDirectory(Path.Combine(scratch, "home"));
// So do the XDG folders: install writes the launcher, its icon and its
// auto-memory mark under XDG_DATA_HOME, which may point at the real one.
foreach (var (xdg, folder) in new[] { ("XDG_DATA_HOME", "xdg-data"), ("XDG_CONFIG_HOME", "xdg-config"), ("XDG_STATE_HOME", "xdg-state"), ("XDG_CACHE_HOME", "xdg-cache"), ("XDG_RUNTIME_DIR", "xdg-run") })
{
    Directory.CreateDirectory(Path.Combine(scratch, folder));
    Environment.SetEnvironmentVariable(xdg, Path.Combine(scratch, folder));
}
Environment.SetEnvironmentVariable("HIVE_TEST_SANDBOX", scratch);

var checks = new List<(string Name, Action Run)>();
void Check(string name, Action run) => checks.Add((name, run));
int hives = 0;

// A new, empty hive in its own folder.
Store NewHive()
{
    var root = Path.Combine(scratch, $"hive{++hives}");
    var s = new Store(root);
    s.Init();
    return s;
}

// An entry written straight to the comb, the way a file sync, git pull or an
// editor would: no index update, so only a Sync can notice it.
static string WriteEntry(Store s, string project, string id, string title)
{
    var path = Path.Combine(s.Root, "comb", project, id + ".md");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, $"---\ntitle: {title}\ntype: note\nbee: queen\nproject: {project}\ncreated: 2026-10-18 12:00\nsummary: {title}\n---\n\nbody\n");
    return path;
}

// ---- #2 Changed() misses a change another read already synced ----
// src/Store.cs Changed(): Sync() only says what *this* call indexed, so a
// Search (typing in Memory), BeeActivity, RequireBee or Locate that syncs
// first leaves the window's 250 ms check with nothing to see.

Check("#2 control: Changed() sees a new file when nothing read the index first", () =>
{
    using var s = NewHive();
    s.Changed();
    WriteEntry(s, "global", "outside-entry", "Written by another program");
    True(s.Changed(), "Changed() to be true after a new entry file");
});

Check("#2 Changed() sees a new file even after a Search indexed it", () =>
{
    using var s = NewHive();
    s.Changed();
    WriteEntry(s, "global", "outside-entry", "Written by another program");
    True(s.Search("").Any(h => h.Id == "outside-entry"), "(setup) the Search finds the new entry");
    True(s.Changed(), "Changed() to be true: the window hasn't shown the new entry yet");
});

Check("#2 Changed() sees a new file even after BeeActivity indexed it (clicking a bee)", () =>
{
    using var s = NewHive();
    s.Changed();
    WriteEntry(s, "global", "outside-entry", "Written by another program");
    s.BeeActivity();
    True(s.Changed(), "Changed() to be true: the window hasn't shown the new entry yet");
});

Check("#2 Changed() sees a deleted file even after a Search forgot it", () =>
{
    using var s = NewHive();
    var path = WriteEntry(s, "global", "outside-entry", "Written by another program");
    s.Changed();
    File.Delete(path);
    True(!s.Search("").Any(h => h.Id == "outside-entry"), "(setup) the Search no longer finds the entry");
    True(s.Changed(), "Changed() to be true: the window still shows the deleted entry");
});

// ---- undo (the window's undo toasts, stage 3) ----

string AgentFile(string bee) => Path.Combine(scratch, "agents", bee.ToLowerInvariant() + ".md");

Check("undo: RestoreBee brings a retired bee back with its card and subagent", () =>
{
    using var s = NewHive();
    s.CreateBee("Worker", "Does the work", [], "Works carefully.", "approve");
    s.RetireBee("Worker");
    True(s.FindBee("Worker") == null && !File.Exists(AgentFile("Worker")), "(setup) Worker retired, its subagent gone");
    s.RestoreBee("Worker");
    var bee = s.FindBee("Worker");
    True(bee != null && bee.Doc.Body.Contains("Works carefully.") && bee.Specialty == "Does the work", "Worker back with its card and specialty");
    True(File.Exists(AgentFile("Worker")), "Worker's subagent file written again");
    True(!Directory.EnumerateFiles(Path.Combine(s.Root, "bees", ".history"), "Worker-retired-*").Any(), "the retired copy moved back, not copied");
});

Check("undo: ReopenMolt puts a rejected molt back, card unchanged", () =>
{
    using var s = NewHive();
    s.CreateBee("Worker", "Does the work", [], "Old way.", "approve");
    s.Molt("Worker", "a sharper card", "# Worker\n\nNew way.\n");
    var id = s.PendingMolts()[0].Id;
    s.DecideMolt(id, false);
    s.ReopenMolt(id);
    True(s.PendingMolts() is [var m] && m.Id == id, "the molt waiting again");
    True(s.RequireBee("Worker").Doc.Body.Contains("Old way."), "the card still the old one");
});

Check("undo: ReopenMolt puts an approved molt back and the card back as it was", () =>
{
    using var s = NewHive();
    s.CreateBee("Worker", "Does the work", [], "Old way.", "approve");
    s.Molt("Worker", "a sharper card", "# Worker\n\nNew way.\n");
    var id = s.PendingMolts()[0].Id;
    s.DecideMolt(id, true);
    True(s.RequireBee("Worker").Doc.Body.Contains("New way."), "(setup) the approved card in place");
    s.ReopenMolt(id);
    True(s.PendingMolts() is [var m] && m.Id == id, "the molt waiting again");
    var card = s.RequireBee("Worker").Doc.Body;
    True(card.Contains("Old way.") && !card.Contains("New way."), "the card back to the old one; it is: " + card.Trim());
    True(File.ReadAllText(AgentFile("Worker")).Contains("Worker"), "the subagent still there");
});

// ---- the command line: --help ----
// `hive --help` crashed with "--help needs a value": the parser took every
// --word as an option that needs a value, before the switch saw "--help".

Check("cli: hive --help, hive -h and hive <cmd> --help ask for the help", () =>
{
    foreach (var argv in new[] { new[] { "--help" }, ["-h"], ["help"], [], ["bees", "--help"], ["task", "Mason", "--help"] })
    {
        var (pos, opt) = Args.Parse(argv);
        True(Args.Command(pos, opt) == "help", $"`hive {string.Join(' ', argv)}` to be help, not {Args.Command(pos, opt)}");
    }
});

Check("cli control: options still take their values", () =>
{
    var (pos, opt) = Args.Parse(["search", "x", "--project", "hive", "--all", "--limit=3"]);
    True(Args.Command(pos, opt) == "search" && pos.Count == 2, "search with one word");
    True(opt["project"] is ["hive"] && opt["limit"] is ["3"] && opt.ContainsKey("all"), "--project hive, --limit=3 and the --all flag");
    True(Throws(() => Args.Parse(["search", "--project"])), "--project with nothing after it to be an error");
});

// ---- a proposed card that carries its own front matter ----
// Bees read their card with hive_read (the whole file, front matter and all)
// and propose it back through bee_molt. ApplyCard made that whole text the
// body, so the card got a second front-matter block under its own and the
// fields the molt changed (projects) never applied: Mason.md, 2026-10-18.

static int FrontMatterBlocks(string path) =>
    File.ReadAllLines(path).Count(l => l.TrimEnd() == "---") / 2;

Check("molt: approving a card with front matter leaves one block and applies its projects", () =>
{
    using var s = NewHive();
    s.SetProject("alpha", "Alpha", null, null);
    s.SetProject("beta", "Beta", null, null);
    s.CreateBee("Worker", "Does the work", ["alpha", "beta"], "Old way.", "approve", access: "edit");
    var file = File.ReadAllText(s.RequireBee("Worker").Path);
    var proposed = file.Replace("projects: [alpha, beta]", "projects: [alpha]").Replace("Old way.", "New way.");
    True(proposed != file && proposed.StartsWith("---"), "(setup) the whole file with beta dropped");
    s.Molt("Worker", "drop beta", proposed);
    s.DecideMolt(s.PendingMolts()[0].Id, true);
    var bee = s.RequireBee("Worker");
    True(FrontMatterBlocks(bee.Path) == 1, "one front-matter block; the file is:\n" + File.ReadAllText(bee.Path));
    True(bee.Doc.Body.TrimStart().StartsWith("# Worker") && bee.Doc.Body.Contains("New way."), "the body to be the card: " + bee.Doc.Body);
    True(bee.Projects is ["alpha"], "projects to be [alpha], not " + string.Join(", ", bee.Projects));
});

Check("molt: a card's front matter can't change whether a bee may molt, or its access", () =>
{
    using var s = NewHive();
    s.CreateBee("Worker", "Does the work", [], "Old way.", "approve", access: "read");
    s.Molt("Worker", "more rights", "---\nname: Worker\nmolt: free\naccess: all\n---\n\n# Worker\n\nNew way.\n");
    s.DecideMolt(s.PendingMolts()[0].Id, true);
    var bee = s.RequireBee("Worker");
    True(!bee.MoltsFreely && bee.Doc.Get("access") == "read", $"molt approve and access read, not {bee.Molt} and {bee.Doc.Get("access")}");
    True(bee.Doc.Body.Contains("New way.") && FrontMatterBlocks(bee.Path) == 1, "the new card under one block");
});

Check("molt: a free bee's own card with front matter (and a stray block under it) comes out clean", () =>
{
    using var s = NewHive();
    s.SetProject("alpha", "Alpha", null, null);
    s.CreateBee("Worker", "Does the work", ["alpha"], "Old way.", "free");
    s.Molt("Worker", "sharper", "---\nname: Worker\nprojects: []\n---\n\n---\nname: Worker\nprojects: [alpha]\n---\n\n# Worker\n\nNew way.\n");
    var bee = s.RequireBee("Worker");
    True(FrontMatterBlocks(bee.Path) == 1 && bee.Doc.Body.TrimStart().StartsWith("# Worker"), "one block; the file is:\n" + File.ReadAllText(bee.Path));
    True(bee.Projects.Count == 0, "the first block's projects ([]) to apply, not " + string.Join(", ", bee.Projects));
});

Check("molt: undoing an approved card with front matter puts the projects back", () =>
{
    using var s = NewHive();
    s.SetProject("alpha", "Alpha", null, null);
    s.SetProject("beta", "Beta", null, null);
    s.CreateBee("Worker", "Does the work", ["alpha", "beta"], "Old way.", "approve");
    s.Molt("Worker", "drop beta", "---\nprojects: [alpha]\n---\n\n# Worker\n\nNew way.\n");
    var id = s.PendingMolts()[0].Id;
    s.DecideMolt(id, true);
    True(s.RequireBee("Worker").Projects is ["alpha"], "(setup) beta dropped");
    s.ReopenMolt(id);
    var bee = s.RequireBee("Worker");
    True(bee.Projects is ["alpha", "beta"] && bee.Doc.Body.Contains("Old way."), "projects alpha, beta and the old card back; projects: " + string.Join(", ", bee.Projects));
});

Check("molt: the diff shows the projects a card's front matter changes, not the front matter as card text", () =>
{
    using var s = NewHive();
    s.SetProject("alpha", "Alpha", null, null);
    s.SetProject("beta", "Beta", null, null);
    s.CreateBee("Worker", "Does the work", ["alpha", "beta"], "Old way.", "approve");
    var proposed = File.ReadAllText(s.RequireBee("Worker").Path).Replace("projects: [alpha, beta]", "projects: [alpha]");
    s.Molt("Worker", "drop beta", proposed);
    var diff = s.MoltDiff(s.PendingMolts()[0].Doc);
    True(diff.Contains("-projects: alpha, beta") && diff.Contains("+projects: alpha"), "the projects change in the diff:\n" + diff);
    True(!diff.Contains("+name:") && !diff.Contains("+---"), "no front matter as added card text:\n" + diff);
});

Check("molt: a second molt on the same day doesn't overwrite the first one's decided record", () =>
{
    using var s = NewHive();
    s.CreateBee("Worker", "Does the work", [], "Old way.", "approve");
    s.Molt("Worker", "first", "# Worker\n\nOne.\n");
    s.DecideMolt(s.PendingMolts()[0].Id, true);
    s.Molt("Worker", "second", "# Worker\n\nTwo.\n");
    s.DecideMolt(s.PendingMolts()[0].Id, true);
    var decided = Directory.EnumerateFiles(Path.Combine(s.Root, "bees", ".history"), "*.approved.md").Select(f => Doc.Load(f).Get("reason")).Order().ToList();
    True(decided is ["first", "second"], "both approved molts kept, not: " + string.Join(", ", decided));
});

// ---- reading reports (src/Gui/Md.cs) ----

Check("md: **bold** around `code` pairs up; no Markdown marks are left", () =>
{
    var m = Hive.Gui.Md.Inline("**`sky_sh.comp`:** no visible difference, *really*.");
    True(!m.Contains("**") && !m.Contains('`') && !m.Contains('*'), "no ** ` or * in " + m);
    True(m.Contains("weight=\"bold\"") && m.Contains("sky_sh.comp") && m.Contains("<i>really</i>"), "bold, the code and italics in " + m);
});

Check("md: a report's findings, their severities and its verdict", () =>
{
    const string report = "# Review: x\n\n**Verdict: don't merge yet.** One crash.\n\n## Confirmed\n\n### Bugs\n\n1. **[bug high]** `a.cs:1`: crash.\n2. **[bug medium, pre-existing]** `b.cs:2`: slow.\n\n### Bloat\n\n1. **[bloat low]** `c.cs`: unused.\n\n## Rejected\n\n1. **[bug high]** not counted.\n";
    True(Hive.Gui.Md.Findings(report) == (3, 1), "3 findings, 1 high; got " + Hive.Gui.Md.Findings(report));
    True(Hive.Gui.Md.Severity("**[bug medium, pre-existing]** `b.cs:2`: slow.") == ("bug", "medium", "pre-existing", "`b.cs:2`: slow."), "bug, medium, pre-existing");
    True(Hive.Gui.Md.Verdict(report) == "don't merge yet" && Hive.Gui.Md.Title(report) == "x", "the verdict and the title");
});

// ---- talking with a bee (src/Talk.cs) ----

Check("talk: the transcript reads as Claude Code shows it, sidechains left out", () =>
{
    string[] records =
    [
        """{"type":"user","message":{"role":"user","content":"fix it\n\n(The user is talking with you in the Hive window's terminal: answer them.)"}}""",
        """{"type":"assistant","message":{"model":"claude-opus-5-5","usage":{"output_tokens":1200},"content":[{"type":"text","text":"On it.\nFirst the cause."}]}}""",
        """{"type":"assistant","message":{"model":"claude-opus-5-5","usage":{"output_tokens":300},"content":[{"type":"tool_use","id":"t1","name":"Edit","input":{"file_path":"/p/src/A.cs","old_string":"a();","new_string":"b();\nc();"}}]}}""",
        """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""",
        """{"type":"assistant","isSidechain":true,"message":{"content":[{"type":"text","text":"a helper's own words"}]}}""",
        """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Bash","input":{"command":"rm -rf x"}}]}}""",
        """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t2","content":"permission denied","is_error":true}]}}""",
    ];
    var lines = new List<TalkLine>();
    var tools = new Dictionary<string, System.Text.Json.Nodes.JsonObject>();
    long tokens = 0;
    var model = "";
    foreach (var r in records) Transcript.Read(r, lines, tools, ref tokens, ref model, "/p");
    var shown = string.Join(" | ", lines.Select(l => $"{l.Kind}:{l.Bold}{l.Text}"));
    True(shown == "you:fix it | say:On it. | plain:First the cause. | tool:Edit(src/A.cs) | out:Updated with 2 additions and 1 removal | del:a(); | add:b(); | add:c(); | tool:Bash(rm -rf x) | err:Error: permission denied", "the lines; got " + shown);
    True(tokens == 1500 && Transcript.ModelName(model) == "Opus 5.5", $"1500 tokens on Opus 5.5; got {tokens} on {model}");
});

// ---- work pane wp2: the Queen's turns (src/Talk.cs) ----
// The window talks only with the Queen now. Tasks.Talk / TalkSession (a
// bee's talk turns) are no longer called from anywhere in src, so the old
// "talk: a turn resumes the bee's session" check is rewritten for the Queen.

Check("queen: a conversation resumes the Queen's newest session, also in a new Store (a restart), until \"new\"", () =>
{
    using var s = NewHive();
    var first = Tasks.QueenTalk(s, "hello", "", "edit", null);
    True(first.Bee == Tasks.Queen && first.Doc.Get("role") == "queen" && first.Doc.Get("resume") == "", "a fresh Queen turn");
    first.Doc.Set("session", "abc-123");
    first.Doc.Save(first.Path);
    Tasks.Record(s, Tasks.Load(s, first.Id), "Hi.", "done");
    var restarted = new Store(s.Root);
    var from = Tasks.QueenSession(Tasks.List(restarted, Tasks.Queen), Tasks.QueenSince(restarted));
    True(from?.Id == first.Id, "the conversation to carry on after a restart; got " + (from?.Id ?? "none"));
    var turn = Tasks.QueenTalk(restarted, "and now?", "", "edit", from);
    True(turn.Doc.Get("resume") == "abc-123" && turn.Doc.Get("follows") == first.Id, "the next turn resumes abc-123; it resumes \"" + turn.Doc.Get("resume") + "\"");
    Thread.Sleep(1100); // "new" a second later than the turns
    Tasks.NewQueenSession(s);
    var again = new Store(s.Root);
    True(Tasks.QueenSession(Tasks.List(again, Tasks.Queen), Tasks.QueenSince(again)) == null, "after \"new\", nothing to carry on, after a restart too");
});

Check("queen: a second turn is refused while she answers", () =>
{
    using var s = NewHive();
    var first = Tasks.QueenTalk(s, "hello", "", "edit", null);
    first.Doc.Set("session", "abc-123");
    first.Doc.Save(first.Path);
    Tasks.Record(s, Tasks.Load(s, first.Id), "Hi.", "done");
    var turn = Tasks.QueenTalk(s, "go on", "", "edit", Tasks.Load(s, first.Id));
    using var runner = Runner(turn.Id);
    try
    {
        turn.Doc.Set("status", "running");
        turn.Doc.Set("pid", runner.Id.ToString());
        turn.Doc.Save(turn.Path);
        True(Throws(() => Tasks.QueenTalk(s, "one more", "", "edit", Tasks.Load(s, first.Id))), "no second turn in the conversation while one answers");
    }
    finally { runner.Kill(entireProcessTree: true); }
});

// "new" marks the conversation's end with her newest turn's id. (Rewritten
// after review rv-d #10: the old version ran in a fresh hive, where
// the mark is "" and passed with any mark; Birch's mutant, the oldest turn
// as the mark, must fail it.)
Check("queen: after \"new\", turns from before it aren't carried on, and a turn in the same second as \"new\" is the new conversation's", () =>
{
    using var s = NewHive();
    BeeTask Turn(string said, string session)
    {
        var t = Tasks.QueenTalk(s, said, "", "edit", null);
        t.Doc.Set("session", session);
        t.Doc.Save(t.Path);
        Tasks.Record(s, Tasks.Load(s, t.Id), "Hi.", "done");
        return Tasks.Load(s, t.Id);
    }
    Turn("first", "old-1");
    var last = Turn("second", "old-2");
    Tasks.NewQueenSession(s);
    True(Tasks.QueenSession(Tasks.List(s, Tasks.Queen), Tasks.QueenSince(s)) == null, "after \"new\", nothing of old-1 or old-2 to carry on; got " + Tasks.QueenSession(Tasks.List(s, Tasks.Queen), Tasks.QueenSince(s))?.Doc.Get("session"));
    var turn = Turn("hello", "new-1");
    Pre(turn.Id[..15] == last.Id[..15], $"the new turn ({turn.Id}) in the same second as the last old one ({last.Id}); a slow machine, run again");
    True(Tasks.QueenSession(Tasks.List(s, Tasks.Queen), Tasks.QueenSince(s))?.Id == turn.Id, $"new-1 carried on; the mark is {Tasks.QueenSince(s)}");
});

// review rv-d #9 (Ash): task ids are local time. Across the
// autumn repeated hour a turn made after "new" can get an id that sorts
// before the mark. The clock can't be set here, so the ids are: the last turn
// before "new" in the hour's first pass (02:30, +02:00), the next one in its
// second pass (02:10, +01:00), 40 minutes later.
Check("queen: a turn after \"new\" whose local-time id sorts before the mark (the repeated autumn hour) is the new conversation's", () =>
{
    using var s = NewHive();
    BeeTask Renamed(BeeTask t, string id, string created)
    {
        var path = Path.Combine(Path.GetDirectoryName(t.Path)!, id + ".md");
        t.Doc.Set("created", created);
        t.Doc.Save(path);
        File.Delete(t.Path);
        if (File.Exists(t.ResultPath)) File.Move(t.ResultPath, Path.ChangeExtension(path, ".result.md"));
        return Tasks.Load(s, id);
    }
    var before = Renamed(Tasks.QueenTalk(s, "before", "", "edit", null), "20261025-023000-queen", "2026-10-25 02:30");
    before.Doc.Set("session", "old-1");
    before.Doc.Save(before.Path);
    Tasks.Record(s, before, "Hi.", "done");
    Tasks.NewQueenSession(s);
    var after = Renamed(Tasks.QueenTalk(s, "after", "", "edit", null), "20261025-021000-queen", "2026-10-25 02:10");
    after.Doc.Set("session", "new-1");
    after.Doc.Save(after.Path);
    Tasks.Record(s, after, "Hi.", "done");
    Pre(string.CompareOrdinal(after.Id, Tasks.QueenSince(s)) < 0, $"the later turn's id ({after.Id}) sorting before the mark ({Tasks.QueenSince(s)})");
    True(Tasks.QueenSession(Tasks.List(s, Tasks.Queen), Tasks.QueenSince(s))?.Id == after.Id, $"new-1 carried on; got {Tasks.QueenSession(Tasks.List(s, Tasks.Queen), Tasks.QueenSince(s))?.Doc.Get("session") ?? "nothing"}");
});

// ---- stage 1: the Queen's model picker ----
// The pane passes its pick to Tasks.QueenTalk; the turn's task file carries
// it and Tasks.Args turns it into --model. The pick lives in
// .tasks/.queen-model (Tasks.QueenModel / SetQueenModel); "new" deletes it.
// These call QueenTalk, Args, QueenModel, SetQueenModel and NewQueenSession
// by name: a fix that renames them updates these checks.

// The value after flag in args, or null when the flag isn't there.
static string? FlagValue(List<string> args, string flag) => args.IndexOf(flag) is var i and >= 0 && i + 1 < args.Count ? args[i + 1] : null;

Check("model: a Queen turn runs with --model <id> for each pick, fresh and resumed, with --model before the permission flags", () =>
{
    using var s = NewHive();
    Pre(Tasks.QueenModels.Length == 4 && Tasks.QueenModelNames.Length == 4, "four models to pick from besides default");
    foreach (var model in Tasks.QueenModels)
        foreach (var resumed in new[] { false, true })
        {
            BeeTask? from = null;
            if (resumed)
            {
                from = Tasks.QueenTalk(s, "earlier", "", "edit", null);
                from.Doc.Set("session", "sess-" + model);
                from.Doc.Save(from.Path);
                Tasks.Record(s, Tasks.Load(s, from.Id), "Hi.", "done");
                from = Tasks.Load(s, from.Id);
            }
            var turn = Tasks.Load(s, Tasks.QueenTalk(s, "go", "", "edit", from, model).Id); // as read back from its file
            var args = Tasks.Args(turn);
            True(args.Count(a => a == "--model") == 1 && FlagValue(args, "--model") == model, $"one --model {model} ({(resumed ? "resumed" : "fresh")}); args: {string.Join(' ', args)}");
            var m = args.IndexOf("--model");
            var permission = args.FindIndex(a => a is "--permission-mode" or "--dangerously-skip-permissions");
            True(permission > m, $"--model before the permission flags; args: {string.Join(' ', args)}");
            if (resumed) True(FlagValue(args, "--resume") == "sess-" + model && args.IndexOf("--resume") < m, $"--resume sess-{model} before --model; args: {string.Join(' ', args)}");
        }
});

Check("model: default (no pick) passes no --model, and the turn's file has no model", () =>
{
    using var s = NewHive();
    foreach (var access in Tasks.Access)
    {
        var turn = Tasks.Load(s, Tasks.QueenTalk(s, "go " + access, "", access, null, "").Id);
        True(!Tasks.Args(turn).Contains("--model"), $"no --model with access {access}; args: {string.Join(' ', Tasks.Args(turn))}");
        True(turn.Doc.Get("model") == "", $"no model: in the task file; it has \"{turn.Doc.Get("model")}\"");
    }
    // The old call (no model argument) is default too.
    var old = Tasks.Load(s, Tasks.QueenTalk(s, "old call", "", "edit", null).Id);
    True(!Tasks.Args(old).Contains("--model"), "no --model when QueenTalk is called without a model");
});

// Checked against claude 2.1.284 on 2026-10-21 (Wren, throwaway session):
// `claude -p --resume <s>` with no --model runs on the model the session's
// last turn used (Haiku stayed Haiku), not the user's default (Opus 5.5 for
// a fresh session); `--resume <s> --model default` went back to Opus 5.5. So
// a resumed turn that passes nothing after a picked turn keeps the old pick.
// What the user sees: picking "default" says "your default model from your
// next message", and the next turn still runs on the old pick.
static BeeTask Resumable(Store s, string session, string model)
{
    var t = Tasks.QueenTalk(s, "earlier", "", "edit", null, model);
    t.Doc.Set("session", session);
    t.Doc.Save(t.Path);
    Tasks.Record(s, Tasks.Load(s, t.Id), "Hi.", "done");
    return Tasks.Load(s, t.Id);
}

Check("model: default picked after Haiku, in the same conversation: the resumed turn doesn't run on with Haiku", () =>
{
    using var s = NewHive();
    var from = Resumable(s, "sess-h", "claude-haiku-4-5-20251001");
    var args = Tasks.Args(Tasks.Load(s, Tasks.QueenTalk(s, "back to default", "", "edit", from, "").Id));
    Pre(FlagValue(args, "--resume") == "sess-h", "the turn resumes sess-h");
    // A resumed claude keeps its last model unless --model says otherwise.
    True(FlagValue(args, "--model") is { } m && m != "claude-haiku-4-5-20251001",
         $"a --model that leaves Haiku (e.g. --model default); it has {(FlagValue(args, "--model") is { } v ? "--model " + v : "no --model, so claude resumes on Haiku")}; args: {string.Join(' ', args)}");
});

Check("model: control: Sonnet picked after Haiku, in the same conversation: the resumed turn runs --model Sonnet", () =>
{
    using var s = NewHive();
    var from = Resumable(s, "sess-h", "claude-haiku-4-5-20251001");
    var args = Tasks.Args(Tasks.Load(s, Tasks.QueenTalk(s, "now Sonnet", "", "edit", from, "claude-sonnet-5-5").Id));
    True(FlagValue(args, "--resume") == "sess-h" && FlagValue(args, "--model") == "claude-sonnet-5-5", $"--resume sess-h --model claude-sonnet-5-5; args: {string.Join(' ', args)}");
});

Check("model: control: a bee's task (not the Queen's) has no --model", () =>
{
    using var s = NewHive();
    s.CreateBee("Worker", "Does the work", [], "", "approve");
    foreach (var access in Tasks.Access)
        True(!Tasks.Args(Tasks.Create(s, "Worker", "Look into it", "", access)).Contains("--model"), $"no --model on a bee task with access {access}");
});

Check("model: the pick survives a restart (a new Store reads it back), for each model", () =>
{
    using var s = NewHive();
    True(Tasks.QueenModel(s) == "", $"default in a fresh hive; got \"{Tasks.QueenModel(s)}\"");
    foreach (var model in Tasks.QueenModels)
    {
        Tasks.SetQueenModel(s, model);
        var restarted = new Store(s.Root);
        True(Tasks.QueenModel(restarted) == model, $"{model} after a restart; got \"{Tasks.QueenModel(restarted)}\"");
    }
    Tasks.SetQueenModel(s, "");
    True(Tasks.QueenModel(new Store(s.Root)) == "" && !File.Exists(Path.Combine(s.Root, ".tasks", ".queen-model")), "picking default removes .tasks/.queen-model");
    Tasks.SetQueenModel(s, ""); // again, with no file: no error
});

Check("model: \"new\" forgets the pick, after a restart too", () =>
{
    using var s = NewHive();
    Tasks.SetQueenModel(s, "claude-sonnet-5-5");
    Pre(Tasks.QueenModel(s) == "claude-sonnet-5-5", "Sonnet picked");
    Tasks.NewQueenSession(s);
    True(Tasks.QueenModel(new Store(s.Root)) == "", $"default after \"new\"; got \"{Tasks.QueenModel(new Store(s.Root))}\"");
    True(!File.Exists(Path.Combine(s.Root, ".tasks", ".queen-model")), "no .tasks/.queen-model left");
});

Check("model: an unknown or hand-edited .queen-model reads as default; a known id with spaces or CRLF around it still reads", () =>
{
    using var s = NewHive();
    Tasks.SetQueenModel(s, "claude-haiku-4-5-20251001");
    var file = Path.Combine(s.Root, ".tasks", ".queen-model");
    Pre(File.Exists(file), "the pick written to .tasks/.queen-model");
    foreach (var bad in new[] { "", "\n", "claude-gpt-9", "CLAUDE-OPUS-5-5", "Opus 5.5", "default", "claude-opus-5-5 --dangerously-skip-permissions",
                                "claude-opus-5-5\nclaude-sonnet-5-5", "claude-opus", "../../etc/passwd", "\0claude-opus-5-5", "claude-opus-5-5-x", new string('x', 100_000) })
    {
        File.WriteAllText(file, bad);
        True(Tasks.QueenModel(new Store(s.Root)) == "", $"default for \"{(bad.Length > 60 ? bad[..20] + "…" : bad).Replace("\n", "\\n").Replace("\0", "\\0")}\"; got \"{Tasks.QueenModel(s)}\"");
    }
    foreach (var (good, want) in new[] { ("  claude-opus-5-5  ", "claude-opus-5-5"), ("claude-sonnet-5-5\r\n", "claude-sonnet-5-5"), ("\tclaude-fable-5-1\n\n", "claude-fable-5-1") })
    {
        File.WriteAllText(file, good);
        True(Tasks.QueenModel(new Store(s.Root)) == want, $"{want} for \"{good.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t")}\"; got \"{Tasks.QueenModel(s)}\"");
    }
    File.Delete(file);
    Directory.CreateDirectory(file); // a folder where the file goes
    True(Tasks.QueenModel(new Store(s.Root)) == "", "default when .queen-model is a folder");
});

Check("model: QueenModels and QueenModelNames line up: each id's name is what ModelName makes of the id", () =>
{
    Pre(Tasks.QueenModels.Length == Tasks.QueenModelNames.Length, "as many names as ids");
    for (int i = 0; i < Tasks.QueenModels.Length; i++)
        True(Transcript.ModelName(Tasks.QueenModels[i]) == Tasks.QueenModelNames[i],
             $"{Tasks.QueenModels[i]} named \"{Tasks.QueenModelNames[i]}\", as the transcript's model reads (\"{Transcript.ModelName(Tasks.QueenModels[i])}\")");
    True(Tasks.QueenModels.Distinct().Count() == Tasks.QueenModels.Length && Tasks.QueenModelNames.Distinct().Count() == Tasks.QueenModelNames.Length, "no id or name twice");
});

// ---- Claude usage (src/Usage.cs) ----

Check("usage: the status line saves rate_limits and prints a short line; without them it saves nothing", () =>
{
    using var s = NewHive();
    var at = DateTimeOffset.Now.ToUnixTimeSeconds() + 3600;
    var line = Usage.StatusLine(s, "{\"model\":{\"display_name\":\"Opus\"},\"rate_limits\":{\"five_hour\":{\"used_percentage\":42.3,\"resets_at\":" + (at) + "},\"seven_day\":{\"used_percentage\":23,\"resets_at\":" + (at + 86400) + "}}}");
    True(line == "ʚ●ɞ Opus · 5h 42% · 7d 23%", "the line; got " + line);
    var saved = Usage.Load(s);
    True(saved?.Session is { Used: 42.3 } && saved.Session.Resets == at && saved.Week is { Used: 23 }, "42.3% and 23% saved, with when they reset");
    File.Delete(Usage.FilePath(s));
    True(Usage.StatusLine(s, """{"model":{"display_name":"Opus"}}""") == "ʚ●ɞ Opus" && !File.Exists(Usage.FilePath(s)), "no rate_limits: nothing saved, no numbers printed");
    True(Usage.StatusLine(s, "not json") == "ʚ●ɞ" && Usage.Load(s) == null, "garbage in: still a line, nothing saved");
});

Check("usage: hive install sets the status line only where there's none", () =>
{
    var none = new System.Text.Json.Nodes.JsonObject();
    True(Install.AddStatusLine(none, "/bin/hive statusline") && none["statusLine"]?["command"]?.ToString() == "/bin/hive statusline", "set when there was none");
    var mine = System.Text.Json.Nodes.JsonNode.Parse("""{"statusLine":{"type":"command","command":"~/.claude/my-line.sh"}}""")!.AsObject();
    True(!Install.AddStatusLine(mine, "/bin/hive statusline") && mine["statusLine"]?["command"]?.ToString() == "~/.claude/my-line.sh", "the user's own left alone");
    var old = System.Text.Json.Nodes.JsonNode.Parse("""{"statusLine":{"type":"command","command":"/old/hive statusline"}}""")!.AsObject();
    True(Install.AddStatusLine(old, "/bin/hive statusline") && old["statusLine"]?["command"]?.ToString() == "/bin/hive statusline", "an older one of ours replaced");
});

// ---- bloat 5: bindings nothing calls ----

var repo = FindRepo();

Check("bloat 5 every binding in src/Gui/Native.cs is called", () =>
{
    var native = File.ReadAllText(Path.Combine(repo, "src", "Gui", "Native.cs"));
    var others = string.Join("\n", Directory.EnumerateFiles(Path.Combine(repo, "src"), "*.cs", SearchOption.AllDirectories)
        .Where(f => Path.GetFileName(f) != "Native.cs").Select(File.ReadAllText));
    var unused = Regex.Matches(native, @"partial\s+[\w*]+\s+(\w+)\(").Select(m => m.Groups[1].Value)
        .Where(name => !Regex.IsMatch(others, $@"\bN\.{name}\(") && Regex.Matches(native, $@"\b{name}\(").Count < 2)
        .ToList();
    True(unused.Count == 0, "no unused bindings; unused: " + string.Join(", ", unused));
});

Check("bloat 5 no gtk_list_box_select_row on lists made with selection mode NONE", () =>
{
    var ui = File.ReadAllText(Path.Combine(repo, "src", "Gui", "Ui.cs"));
    var app = File.ReadAllText(Path.Combine(repo, "src", "Gui", "App.cs"));
    var noneLists = ui.Contains("gtk_list_box_set_selection_mode(lb, 0)");
    True(!(noneLists && app.Contains("gtk_list_box_select_row")),
         "App.cs not to select rows in Ui.List lists (selection mode 0 = NONE makes it a no-op)");
});

// ---- terminal redesign, step 3: live steps, parent sessions, Talk presence ----

Check("steps: each tool call goes to its station in the garden", () =>
{
    (string Tool, string What, string Station)[] cases =
    [
        ("Edit", "src/Tasks.cs", Steps.Flowers), ("Write", "a.md", Steps.Flowers),
        ("Bash", "dotnet build -c Release", Steps.Flowers), ("Bash", "timeout 300 ./build.sh", Steps.Flowers),
        ("Bash", "dotnet test", Steps.Comb), ("Bash", "dotnet run --project tests/StoreTests", Steps.Comb), ("Bash", "./tests/run.sh", Steps.Comb),
        ("Read", "src/App.cs", Steps.Library), ("Grep", "Tick", Steps.Library), ("Glob", "**/*.cs", Steps.Library),
        ("Bash", "git log --oneline", Steps.Library), ("Bash", "git diff", Steps.Library),
        ("WebSearch", "gtk tick callback", Steps.Field), ("WebFetch", "https://docs.gtk.org", Steps.Field),
        ("Bash", "echo hi", Steps.Workbench), ("Bash", "dotnet run", Steps.Workbench), ("TodoWrite", "", Steps.Workbench),
        ("hive_deposit", "a lesson", Steps.Hive), ("hive_tidy", "", Steps.Hive), ("think", "", Steps.Thinking),
        // A command is a whole word (review rv-b #10): ls and make, not lsof or makepkg.
        ("Bash", "ls -la", Steps.Library), ("Bash", "ls", Steps.Library), ("Bash", "make", Steps.Flowers), ("Bash", "make -j4", Steps.Flowers),
        ("Bash", "lsof -i :8080", Steps.Workbench), ("Bash", "lsblk", Steps.Workbench), ("Bash", "dune build", Steps.Workbench),
        ("Bash", "duckdb a.db", Steps.Workbench), ("Bash", "makepkg -si", Steps.Workbench), ("Bash", "treefmt", Steps.Workbench),
        ("Bash", "category.sh", Steps.Workbench), ("Bash", "cat a.md", Steps.Library),
    ];
    foreach (var (tool, what, station) in cases)
        True(Steps.Station(tool, what) == station, $"{tool} {what} at {station}, got {Steps.Station(tool, what)}");
});

Check("steps: stream-json messages become step lines, and a result names its tool", () =>
{
    var tools = new Dictionary<string, string>();
    var use = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""
        {"type":"assistant","message":{"content":[
          {"type":"thinking","thinking":"Let me look."},
          {"type":"tool_use","id":"t1","name":"Edit","input":{"file_path":"/home/u/p/src/Tasks.cs","old_string":"a","new_string":"b"}},
          {"type":"tool_use","id":"t2","name":"mcp__hive__hive_deposit","input":{"type":"lesson","title":"x"}}]}}
        """)!;
    var lines = Steps.Lines(use, "/home/u/p", tools, 1000);
    True(lines.SequenceEqual(["1000\tthink\tLet me look.", "1000\tEdit\tsrc/Tasks.cs", "1000\thive_deposit\ta lesson"]), "three step lines; got " + string.Join(" | ", lines));
    var result = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""
        {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"ok","is_error":true}]}}
        """)!;
    var back = Steps.Lines(result, "/home/u/p", tools, 2000);
    True(back is ["2000\t=Edit\terror"], "the result line names Edit and says error; got " + string.Join(" | ", back));
    var step = Steps.Parse(lines[1])!.Value;
    True(step.Station == Steps.Flowers && step.Say == "editing src/Tasks.cs", $"the parsed step at the flowers, \"editing src/Tasks.cs\"; got {step.Station}, \"{step.Say}\"");
});

Check("steps: a denied tool's system permission_denied message (its \"message\" a string) adds no steps and doesn't throw", () =>
{
    // claude -p 2.1.284 emits this when a tool is denied; it used to fail the whole task with "NodeWrongType, JsonObject".
    var denied = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""
        {"type":"system","subtype":"permission_denied","message":"Permission to use Skill has been denied."}
        """)!;
    var lines = Steps.Lines(denied, "/home/u/p", new Dictionary<string, string>(), 1000);
    True(lines.Count == 0, "no step lines; got " + string.Join(" | ", lines));
});

Check("steps: a task's stream keeps its steps, its session as soon as it starts, and the same result as before", () =>
{
    using var s = NewHive();
    s.CreateBee("Worker", "Does the work", [], "", "approve");
    var task = Tasks.Create(s, "Worker", "Look into it", "", "read");
    var stream = string.Join("\n",
        """{"type":"system","subtype":"init","session_id":"abc-123"}""",
        """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Read","input":{"file_path":"/w/src/A.cs"}}]}}""",
        """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"..."}]}}""",
        "not json",
        """{"type":"result","subtype":"success","is_error":false,"duration_ms":61000,"result":"Found it.","session_id":"abc-123"}""");
    var (json, other) = Tasks.Follow(new StringReader(stream), task, "/w");
    True(json != null && Json.Str(json["result"]) == "Found it.", "the result message");
    True(other.Trim() == "not json", "the line that wasn't JSON kept aside; got " + other);
    True(Tasks.Load(s, task.Id).Doc.Get("session") == "abc-123", "the session id in the task file before the run ends");
    var steps = File.ReadAllLines(task.StepsPath);
    True(steps.Length == 2 && steps[0].EndsWith("\tRead\tsrc/A.cs") && steps[1].EndsWith("\t=Read\tok"), "two step lines (the read and its result); got " + string.Join(" | ", steps));
});

Check("hooks: a subagent's .state names the session that spawned it and its folder; the stop check still reads it", () =>
{
    using var s = NewHive();
    s.CreateBee("Worker", "Does the work", [], "", "approve");
    s.SetSetting("Worker", "deposit_before_stop", "yes"); // the stop check is opt-in since stage 6
    var input = new System.Text.Json.Nodes.JsonObject { ["agent_type"] = "worker", ["agent_id"] = "agent-7", ["session_id"] = "parent-1", ["cwd"] = "/home/u/p" };
    Hooks.Handle(s, "subagent-start", input);
    var lines = File.ReadAllLines(Path.Combine(s.Root, ".state", "agent-7"));
    True(lines.Length == 4 && lines[0] == "Worker" && lines[2] == "parent-1" && lines[3] == "/home/u/p", "bee, start, parent session, folder; got " + string.Join(" | ", lines));
    var reply = Hooks.Handle(s, "subagent-stop", new System.Text.Json.Nodes.JsonObject { ["agent_id"] = "agent-7" });
    True(reply != null && Json.Str(reply["decision"]) == "block", "the stop check sends a bee that saved nothing back once");
    var again = File.ReadAllLines(Path.Combine(s.Root, ".state", "agent-7"));
    True(again.Length == 4 && again[2] == "parent-1", "the parent session kept when it's tracked again; got " + string.Join(" | ", again));
});

Check("hooks: a chat with a bee is present in .talk until its session ends, and never in .state", () =>
{
    using var s = NewHive();
    s.CreateBee("Worker", "Does the work", [], "", "approve");
    Hooks.Handle(s, "session-start", new System.Text.Json.Nodes.JsonObject { ["agent_type"] = "worker", ["session_id"] = "chat-1", ["session_title"] = "Worker" });
    True(File.Exists(Path.Combine(s.Root, ".talk", "chat-1")), "a presence file for the chat");
    True(!Directory.Exists(Path.Combine(s.Root, ".state")) || !File.Exists(Path.Combine(s.Root, ".state", "chat-1")), "no .state file (the Stop check must not nag a chat)");
    True(Hooks.Handle(s, "stop", new System.Text.Json.Nodes.JsonObject { ["session_id"] = "chat-1" }) == null, "a chat's turn ending isn't held up");
    Hooks.Handle(s, "session-end", new System.Text.Json.Nodes.JsonObject { ["session_id"] = "chat-1" });
    True(!File.Exists(Path.Combine(s.Root, ".talk", "chat-1")), "the presence file gone when the chat ends");
});

// ---- a bee woken from its own background work is told it saved nothing ----
// Claude Code runs SubagentStart again each time a subagent wakes from its
// background work:
// the wake-up's start must not hide what it saved before, nor ask again.
// The stop check is opt-in (deposit_before_stop: yes), so these turn it on.

System.Text.Json.Nodes.JsonObject? SubagentHook(Store s, string hook, string agent) =>
    Hooks.Handle(s, hook, new System.Text.Json.Nodes.JsonObject { ["agent_type"] = "worker", ["agent_id"] = agent, ["session_id"] = "parent-1", ["cwd"] = "/home/u/p" });

Check("hooks control: a subagent that saved nothing is sent back once", () =>
{
    using var s = NewHive();
    s.CreateBee("Worker", "Does the work", [], "", "approve");
    s.SetSetting("Worker", "deposit_before_stop", "yes"); // the stop check is opt-in
    SubagentHook(s, "subagent-start", "agent-w0");
    var reply = SubagentHook(s, "subagent-stop", "agent-w0");
    True(reply != null && Json.Str(reply["decision"]) == "block", "a block; got " + reply?.ToJsonString());
});

Check("hooks: a subagent that saved, then woke from its background work, isn't told it saved nothing", () =>
{
    using var s = NewHive();
    s.CreateBee("Worker", "Does the work", [], "", "approve");
    s.SetSetting("Worker", "deposit_before_stop", "yes"); // the stop check is opt-in
    SubagentHook(s, "subagent-start", "agent-w1");
    s.Deposit("Worker", "worklog", "Did it", "Did the work", "", "", [], []);
    Pre(SubagentHook(s, "subagent-stop", "agent-w1") == null, "the first stop goes through (it saved a worklog)");
    Thread.Sleep(20); // the wake-up comes later than the deposit
    SubagentHook(s, "subagent-start", "agent-w1");
    var lines = File.ReadAllLines(Path.Combine(s.Root, ".state", "agent-w1"));
    True(lines.Length == 4 && lines[0] == "Worker", "the woken subagent shows as at work again; .state: " + string.Join(" | ", lines));
    var reply = SubagentHook(s, "subagent-stop", "agent-w1");
    True(reply == null, "no block after the wake-up; got " + reply?.ToJsonString());
    True(SubagentHook(s, "subagent-start", "agent-w1") is { } && SubagentHook(s, "subagent-stop", "agent-w1") == null, "nor after a second wake-up");
});

Check("hooks: a subagent that saved nothing is asked once in all, not again on each wake-up", () =>
{
    using var s = NewHive();
    s.CreateBee("Worker", "Does the work", [], "", "approve");
    s.SetSetting("Worker", "deposit_before_stop", "yes"); // the stop check is opt-in
    SubagentHook(s, "subagent-start", "agent-w2");
    Pre(SubagentHook(s, "subagent-stop", "agent-w2") != null, "the first stop blocked");
    Pre(Hooks.Handle(s, "subagent-stop", new System.Text.Json.Nodes.JsonObject { ["agent_id"] = "agent-w2", ["stop_hook_active"] = true }) == null, "the stop after the block goes through");
    SubagentHook(s, "subagent-start", "agent-w2");
    var reply = SubagentHook(s, "subagent-stop", "agent-w2");
    True(reply == null, "no second block after a wake-up; got " + reply?.ToJsonString());
});

Check("hooks: a stopped subagent's marker isn't a bee at work in .state", () =>
{
    using var s = NewHive();
    s.CreateBee("Worker", "Does the work", [], "", "approve");
    s.SetSetting("Worker", "deposit_before_stop", "yes"); // the stop check is opt-in
    SubagentHook(s, "subagent-start", "agent-w3");
    s.Deposit("Worker", "worklog", "Did it", "Did the work", "", "", [], []);
    SubagentHook(s, "subagent-stop", "agent-w3");
    var atWork = Directory.EnumerateFiles(Path.Combine(s.Root, ".state")).Select(File.ReadAllLines).Where(l => l.Length >= 2 && long.TryParse(l[1], out _)).ToList();
    True(atWork.Count == 0, "no .state file the window takes for a bee at work; found " + string.Join(" / ", atWork.Select(l => string.Join(" | ", l))));
});

Check("hooks control: with deposit_before_stop off, a subagent that saved nothing stops, and its wake-up still shows it at work", () =>
{
    using var s = NewHive();
    s.CreateBee("Worker", "Does the work", [], "", "approve");
    SubagentHook(s, "subagent-start", "agent-w4");
    True(SubagentHook(s, "subagent-stop", "agent-w4") == null, "no block with the check off");
    SubagentHook(s, "subagent-start", "agent-w4");
    var lines = File.ReadAllLines(Path.Combine(s.Root, ".state", "agent-w4"));
    True(lines.Length == 4 && lines[0] == "Worker", "the woken subagent at work again; .state: " + string.Join(" | ", lines));
    True(SubagentHook(s, "subagent-stop", "agent-w4") == null, "and no block after the wake-up");
});

// ---- stage 9: Follow up and Hand to ----

Check("tasks: Follow up continues the same bee's session as a new task linked to the first", () =>
{
    using var s = NewHive();
    s.CreateBee("Worker", "Does the work", [], "", "approve");
    var first = Tasks.Create(s, "Worker", "Look into it", "", "edit");
    True(Throws(() => Tasks.FollowUp(s, Tasks.Load(s, first.Id), "And then?")), "no follow-up while it has no session (or still runs)");
    first.Doc.Set("session", "abc-123");
    first.Doc.Set("status", "done");
    first.Doc.Save(first.Path);
    var next = Tasks.FollowUp(s, Tasks.Load(s, first.Id), "Now fix it.");
    True(next.Bee == "Worker" && next.Doc.Get("follows") == first.Id && next.Doc.Get("resume") == "abc-123" && next.Doc.Get("access") == "edit", "same bee, linked, resuming abc-123, same access");
    var args = Tasks.Args(next);
    var at = args.IndexOf("--resume");
    True(at >= 0 && args[at + 1] == "abc-123" && args.Contains("stream-json"), "claude runs with --resume abc-123 and stream-json; args: " + string.Join(" ", args));
    // While that follow-up runs, the session is in use (review rv-b #6).
    next.Doc.Set("status", "running");
    next.Doc.Save(next.Path);
    True(Throws(() => Tasks.FollowUp(s, Tasks.Load(s, first.Id), "And another thing.")), "no second follow-up while the first one runs in that session");
    Tasks.Record(s, Tasks.Load(s, next.Id), "Fixed.", "done");
    True(!Throws(() => Tasks.FollowUp(s, Tasks.Load(s, first.Id), "And another thing.")), "a follow-up again once it's done");
});

Check("tasks: Hand to gives another bee the task with the first one's result attached", () =>
{
    using var s = NewHive();
    s.CreateBee("Worker", "Does the work", [], "", "approve");
    s.CreateBee("Wren", "Tests the work", [], "", "approve");
    var first = Tasks.Create(s, "Worker", "Find the bug", "", "read");
    Tasks.Record(s, first, "The bug is in Doc.cs line 12.", "done");
    var next = Tasks.HandTo(s, Tasks.Load(s, first.Id), "Wren", "Write a test for it.");
    True(next.Bee == "Wren" && next.Doc.Get("follows") == first.Id && next.Doc.Get("resume") == "", "Wren's, linked, a fresh session");
    True(next.Prompt.StartsWith("Write a test for it.") && next.Prompt.Contains("The bug is in Doc.cs line 12.") && next.Prompt.Contains(first.Id), "the prompt carries the result and where it came from");
    True(!Tasks.Args(next).Contains("--resume"), "no --resume");
});

// ---- stage 10: the Briefs meter ----

Check("briefs: a briefing with unsaved text, and uncapped for the meter", () =>
{
    using var s = NewHive();
    s.SetProject("alpha", "Alpha project", null, "# alpha\n\nThe alpha brief.");
    var unsaved = s.Brief(null, null, null, "# Queen\n\nA line typed but not saved.", null, cap: true);
    True(unsaved.Contains("A line typed but not saved.") && !s.Brief(null, null, null).Contains("A line typed"), "the Queen's unsaved text in the preview only");
    var project = s.Brief("alpha", null, null, null, "# alpha\n\nAn unsaved brief.", cap: true);
    True(project.Contains("An unsaved brief.") && !project.Contains("The alpha brief."), "the project's unsaved brief in place of the saved one");
    for (int i = 0; i < 120; i++) s.SetProject($"p{i}", $"Project number {i}, with a summary long enough that a hundred of them run past the cap of the briefing", null, null);
    var full = s.Brief(null, null, null, null, null, cap: false);
    var sent = s.Brief(null, null, null, null, null, cap: true);
    // (The cap keeps 9,000 characters and adds a one-line "cut short" note.)
    True(sent.Contains("cut short") && sent.Length < Store.BriefLimit + 60 && full.Length > Store.BriefLimit + 1000, $"cut short near {Store.BriefLimit} ({sent.Length}) while the uncapped one says how long it would run ({full.Length})");
});

// ---- team review review rv-a (the whole repo, v0.1.01) ----
// "rv-a #n" is the finding number in that review. Findings it leaves to
// other suites: #8, #9 and #15 are in tests/WindowTests.

Bee Worker(Store s, string molt = "approve") => s.CreateBee("Worker", "Does the work", [], "Old way.", molt);

// ---- rv-a #1 a "read" task can write files through git --output ----
// Claude Code's Bash rules as it applies them (checked by the Arbiter with
// claude -p, 2026-10-18): * in a rule matches anything, so Bash(git diff *)
// also allows `git diff --output=<file>`; a --disallowedTools rule beats
// every allowed one.

foreach (var (access, a) in new[] { ("read", "a"), ("edit", "an") })
{
    Check($"rv-a #1 control: {a} {access} task may run git diff, log and show", () =>
    {
        using var s = NewHive();
        Worker(s);
        var args = Tasks.Args(Tasks.Create(s, "Worker", "Look into it", "", access));
        foreach (var cmd in new[] { "git diff HEAD~1", "git log --oneline -5", "git show HEAD" })
            True(Permitted(args, cmd), $"`{cmd}` allowed; args: {string.Join(' ', args.Skip(8))}");
    });

    Check($"rv-a #1 {a} {access} task can't write a file with git --output", () =>
    {
        using var s = NewHive();
        Worker(s);
        var args = Tasks.Args(Tasks.Create(s, "Worker", "Look into it", "", access));
        foreach (var cmd in new[] { "git diff --output=/home/u/.bashrc", "git log --output=/home/u/.bashrc", "git show --output=/home/u/.bashrc HEAD", "git diff HEAD --output=src/A.cs" })
            True(!Permitted(args, cmd), $"`{cmd}` not allowed (it writes the file); allowed by {string.Join(", ", Rules(args, "--allowedTools").Where(r => RuleMatches(r, cmd)))}, denied by nothing");
    });
}

// ---- rv-a #2 a read task gets every hive tool ----

Check("rv-a #2 control: a read task may brief, recall, read and deposit", () =>
{
    using var s = NewHive();
    Worker(s);
    var args = Tasks.Args(Tasks.Create(s, "Worker", "Look into it", "", "read"));
    foreach (var tool in new[] { "hive_brief", "hive_recall", "hive_read", "hive_deposit" })
        True(AllowsTool(args, "mcp__hive__" + tool), tool + " allowed");
});

// bee_molt stays allowed: a task's molt always counts as its own bee's
// (HIVE_TASK_BEE), so a change to another bee's card waits for the user.
Check("rv-a #2 a read task can't replace a project brief or make a bee", () =>
{
    using var s = NewHive();
    Worker(s);
    var args = Tasks.Args(Tasks.Create(s, "Worker", "Look into it", "", "read"));
    var allowed = new[] { "project_set", "bee_create" }.Where(t => AllowsTool(args, "mcp__hive__" + t)).ToList();
    True(allowed.Count == 0, $"neither project_set nor bee_create allowed; allowed: {string.Join(", ", allowed)} (--allowedTools has {string.Join(", ", Rules(args, "--allowedTools").Where(r => r.StartsWith("mcp__")))})");
});

// ---- edit access allows skills ----

Check("access: an edit task and an edit Queen turn may use skills; a read task may not", () =>
{
    using var s = NewHive();
    Worker(s);
    var edit = Tasks.Args(Tasks.Create(s, "Worker", "Look into it", "", "edit"));
    var queen = Tasks.Args(Tasks.QueenTalk(s, "review this", "", "edit", null));
    var read = Tasks.Args(Tasks.Create(s, "Worker", "Look into it", "", "read"));
    True(Rules(edit, "--allowedTools").Contains("Skill") && Rules(queen, "--allowedTools").Contains("Skill"), "Skill allowed in an edit task and an edit Queen turn; --allowedTools: " + string.Join(",", Rules(edit, "--allowedTools")));
    True(!AllowsTool(read, "Skill"), "Skill not allowed in a read task");
    True(!Permitted(edit, "git clone https://example.com/x") && !Rules(edit, "--allowedTools").Any(r => r.StartsWith("mcp__github")), "edit still allows no git clone and no GitHub tools");
});

// bee_molt through this program run as a task's hive server (`mcp`), with
// HIVE_TASK_BEE set to the task's bee: Free's card, "New way.". The reply's text.
string? McpMolt(Store s, string taskBee, string bee, string by)
{
    var info = new ProcessStartInfo(Environment.ProcessPath!, ["mcp"]) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
    info.Environment["HIVE_HOME"] = s.Root;
    info.Environment["HIVE_TASK_BEE"] = taskBee;
    using var server = Process.Start(info)!;
    var call = new System.Text.Json.Nodes.JsonObject
    {
        ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/call",
        ["params"] = new System.Text.Json.Nodes.JsonObject
        {
            ["name"] = "bee_molt",
            ["arguments"] = new System.Text.Json.Nodes.JsonObject { ["bee"] = bee, ["by"] = by, ["reason"] = "sharper", ["card"] = $"# {bee}\n\nNew way.\n" },
        },
    };
    server.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
    server.StandardInput.WriteLine(call.ToJsonString());
    server.StandardInput.Close();
    server.StandardOutput.ReadLine();
    var reply = server.StandardOutput.ReadLine();
    server.WaitForExit(5000);
    return reply == null ? null : Json.Str(System.Text.Json.Nodes.JsonNode.Parse(reply)?["result"]?["content"]?[0]?["text"]) is { Length: > 0 } text ? text : reply;
}

// The `by` half of #2: bee_molt took `by` on the caller's word, so a task
// could pass itself off as a free bee changing its own card, which applies at
// once. A task's hive server now gets its bee in HIVE_TASK_BEE, and a molt
// from it counts as that bee's. These run this program as the task's hive
// server (`mcp`) and call bee_molt through it.

foreach (var by in new[] { "Free", "" })
    Check($"rv-a #2 a task's molt for a free bee waits for the user, with by \"{by}\"", () =>
    {
        using var s = NewHive();
        Worker(s);
        s.CreateBee("Free", "Changes its own card", [], "Old way.", "free");
        var reply = McpMolt(s, "Worker", "Free", by);
        Pre(reply != null, "a reply to bee_molt");
        True(s.RequireBee("Free").Doc.Body.Contains("Old way.") && !s.RequireBee("Free").Doc.Body.Contains("New way."), "Free's card unchanged; the server said: " + reply);
        True(s.PendingMolts() is [var m] && m.Doc.Get("bee") == "Free" && m.Doc.Get("by") == "Worker", $"one molt for Free, by Worker, waiting; waiting: {string.Join(", ", s.PendingMolts().Select(m => $"{m.Doc.Get("bee")} by {m.Doc.Get("by")}"))}");
    });

Check("rv-a #2 control: a free bee's own task changes its card at once", () =>
{
    using var s = NewHive();
    s.CreateBee("Free", "Changes its own card", [], "Old way.", "free");
    var reply = McpMolt(s, "Free", "Free", "Free");
    Pre(reply != null, "a reply to bee_molt");
    True(s.RequireBee("Free").Doc.Body.Contains("New way.") && s.PendingMolts().Count == 0, "Free's card changed, nothing waiting; the server said: " + reply);
});

// ---- rv-a #3 session-led reviewers get every tool ----
// A subagent file without a tools: line gives the subagent every tool, so a
// reviewer the Queen spawns (a session-led review) can edit and run anything.

Check("rv-a #3 the reviewers' and the Arbiter's subagent files limit their tools to reading", () =>
{
    using var s = NewHive();
    Reviews.SetUp(s, new[] { "Cedar", "Birch", "Ash", "Arbiter" });
    foreach (var name in new[] { "Cedar", "Birch", "Ash", "Arbiter" })
    {
        Pre(File.Exists(AgentFile(name)), $"{name}'s subagent file written");
        var tools = Doc.Load(AgentFile(name)).Get("tools");
        True(tools.Length > 0, $"a tools: line in {name}'s subagent file; its front matter: {string.Join(" | ", File.ReadAllLines(AgentFile(name)).Skip(1).TakeWhile(l => l != "---").Select(l => l.Length > 40 ? l[..40] + "…" : l))}");
        var writes = new[] { "Edit", "Write", "MultiEdit", "NotebookEdit" }.Where(t => Regex.IsMatch(tools, $@"\b{t}\b")).ToList();
        True(writes.Count == 0 && !Regex.IsMatch(tools, @"\bBash\b(?!\()"), $"no editing tools and no unrestricted Bash for {name}; tools: {tools}");
    }
});

// A made-up review team, set up by name since the hive has no preset bees:
// every name but the last a reviewer, the last the overseer.
string[] teamNames = ["Cedar", "Birch", "Ash", "Arbiter"];

// ---- reviewers lost Bash (0.1.02): every review ran without git diff or git log ----
// Checked with claude -p on Claude Code 2.1.284 (2026-10-21): a subagent
// file's tools entry Bash(git diff *) gives the whole Bash tool in every
// permission mode (touch, git diff --output and git clone ran under acceptEdits
// and bypassPermissions), and a disallowedTools entry with a pattern drops
// Bash altogether. So the files carry no Bash in any form, and a read task,
// which runs in dontAsk under the read-only rules, gets it back with --agents.

Check("reviewer Bash: the reviewers' and the overseer's subagent files have no Bash entry of any form", () =>
{
    using var s = NewHive();
    Reviews.SetUp(s, teamNames);
    foreach (var name in teamNames)
    {
        var tools = Doc.Load(AgentFile(name)).Get("tools");
        Pre(tools.Length > 0, $"a tools: line in {name}'s subagent file");
        True(!tools.Contains("Bash"), $"no Bash entry for {name} (a pattern there is plain Bash); tools: {tools}");
    }
});

Check("reviewer Bash: a reviewer's or the overseer's read task gets Bash back, narrowed by the read-only rules", () =>
{
    using var s = NewHive();
    Reviews.SetUp(s, teamNames);
    foreach (var name in teamNames)
    {
        var args = Tasks.Args(Tasks.Create(s, name, "Review it", "", "read"));
        var at = args.IndexOf("--agents");
        True(at >= 0 && at < args.IndexOf("--allowedTools"), $"--agents before --allowedTools for {name}; args: {string.Join(' ', args)}");
        var defs = System.Text.Json.Nodes.JsonNode.Parse(args[at + 1]) as System.Text.Json.Nodes.JsonObject;
        var def = defs?[name.ToLowerInvariant()] as System.Text.Json.Nodes.JsonObject;
        var file = Doc.Load(AgentFile(name));
        var tools = (def?["tools"] as System.Text.Json.Nodes.JsonArray)?.Select(t => Json.Str(t)).ToList() ?? [];
        True(def != null && defs!.Count == 1 && Json.Str(def["description"]) == file.Get("description") && Json.Str(def["prompt"]) == file.Body,
             $"{name} redefined with its subagent file's description and prompt; --agents: {args[at + 1][..Math.Min(200, args[at + 1].Length)]}");
        True(tools.SequenceEqual(file.Get("tools").Split(", ").Append("Bash")), $"{name}'s tools are its file's plus Bash; got {string.Join(",", tools)}");
        True(args[args.IndexOf("--permission-mode") + 1] == "dontAsk", "dontAsk, so only the allowed rules run");
        foreach (var cmd in new[] { "git diff HEAD~1", "git log --oneline -5", "git show HEAD" })
            True(Permitted(args, cmd), $"`{cmd}` allowed for {name}");
        foreach (var cmd in new[] { "touch x", "git diff --output=x HEAD~1", "git log --ext-diff", "git clone . x" })
            True(!Permitted(args, cmd), $"`{cmd}` not allowed for {name}");
    }
});

Check("reviewer Bash control: other bees' tasks and a reviewer's edit task aren't redefined", () =>
{
    using var s = NewHive();
    Reviews.SetUp(s, teamNames);
    Worker(s);
    Agents.Write(s.RequireBee("Worker"));
    True(!Tasks.Args(Tasks.Create(s, "Worker", "Look into it", "", "read")).Contains("--agents"), "no --agents for a bee without a tools line");
    True(!Tasks.Args(Tasks.Create(s, "Ash", "Review it", "", "edit")).Contains("--agents"), "no --agents for a reviewer's edit task");
    True(!Tasks.Args(Tasks.QueenTalk(s, "review this", "", "read", null)).Contains("--agents"), "no --agents for the Queen");
});

// ---- rv-a #4 one unreadable .md file breaks the whole hive ----

Check("rv-a #4 control: a search finds an entry written to the comb", () =>
{
    using var s = NewHive();
    WriteEntry(s, "global", "good-entry", "A good entry");
    True(s.Search("").Any(h => h.Id == "good-entry"), "the entry found");
});

Check("rv-a #4 a broken symlink in the comb doesn't break search", () =>
{
    using var s = NewHive();
    WriteEntry(s, "global", "good-entry", "A good entry");
    File.CreateSymbolicLink(Path.Combine(s.Root, "comb", "global", "broken.md"), "nowhere.md");
    True(s.Search("").Any(h => h.Id == "good-entry"), "the good entry still found");
});

Check("rv-a #4 an unreadable .md file in the comb doesn't break search", () =>
{
    using var s = NewHive();
    WriteEntry(s, "global", "good-entry", "A good entry");
    var locked = WriteEntry(s, "global", "locked-entry", "Locked");
    File.SetUnixFileMode(locked, UnixFileMode.None);
    try
    {
        Pre(!CanRead(locked), "the file unreadable (not running as root)");
        True(s.Search("").Any(h => h.Id == "good-entry"), "the good entry still found");
    }
    finally { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
});

Check("rv-a #4 an unreadable folder in the comb doesn't break search", () =>
{
    using var s = NewHive();
    WriteEntry(s, "global", "good-entry", "A good entry");
    WriteEntry(s, "locked", "hidden-entry", "Hidden");
    var folder = Path.Combine(s.Root, "comb", "locked");
    File.SetUnixFileMode(folder, UnixFileMode.None);
    try
    {
        Pre(!CanList(folder), "the folder unreadable (not running as root)");
        True(s.Search("").Any(h => h.Id == "good-entry"), "the good entry still found");
    }
    finally { File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
});

Check("rv-a #4 control: a session gets the Queen's briefing", () =>
{
    using var s = NewHive();
    s.SaveQueen("# Queen\n\nThe Queen's own page.");
    var reply = Hooks.Handle(s, "session-start", new System.Text.Json.Nodes.JsonObject { ["session_id"] = "s-1", ["cwd"] = scratch });
    True(Json.Str(reply?["hookSpecificOutput"]?["additionalContext"]).Contains("The Queen's own page."), "the Queen's page in the briefing");
});

Check("rv-a #4 a session still gets its briefing with a broken symlink in the comb", () =>
{
    using var s = NewHive();
    s.SaveQueen("# Queen\n\nThe Queen's own page.");
    File.CreateSymbolicLink(Path.Combine(s.Root, "comb", "global", "broken.md"), "nowhere.md");
    var reply = Hooks.Handle(s, "session-start", new System.Text.Json.Nodes.JsonObject { ["session_id"] = "s-1", ["cwd"] = scratch });
    var text = Json.Str(reply?["hookSpecificOutput"]?["additionalContext"]);
    True(text.Contains("The Queen's own page."), "the Queen's briefing; got: " + Text.OneLine(text)[..Math.Min(120, text.Length)]);
});

// ---- rv-a #5 Tasks.List writes "stopped" over a task that just finished ----
// List loads every task file, then checks each running one's pid. A runner
// that saves "done" and exits in between got its task saved back as stopped
// (the stale copy), its took lost. A FIFO among the task files holds List in
// its loading step, so the runners finish exactly there, every time.

Check("rv-a #5 control: a task whose runner died without saying so is marked stopped", () =>
{
    using var s = NewHive();
    Worker(s);
    var t = Tasks.Create(s, "Worker", "Look into it", "", "read");
    using var runner = Sleeper();
    t.Doc.Set("status", "running");
    t.Doc.Set("pid", runner.Id.ToString());
    t.Doc.Save(t.Path);
    runner.Kill();
    runner.WaitForExit();
    Pre(!Directory.Exists($"/proc/{runner.Id}"), "the runner gone");
    Tasks.List(s);
    True(Tasks.Load(s, t.Id).Status == "stopped", "status stopped, got " + Tasks.Load(s, t.Id).Status);
});

Check("rv-a #5 a task that finishes while Tasks.List runs stays done, with its took", () =>
{
    using var s = NewHive();
    Worker(s);
    var dir = Path.Combine(s.Root, ".tasks");
    var runs = new List<(BeeTask Task, Process Runner)>();
    var fifo = Path.Combine(dir, "00000000-000000-hold.md");
    // Running tasks made before and after the FIFO, so some are listed before
    // it whichever order the folder lists them in.
    for (int i = 0; i < 4; i++)
    {
        if (i == 2) Pre(Process.Start("mkfifo", [fifo]) is { } mk && mk.WaitForExit(5000) && mk.ExitCode == 0, "mkfifo to make the FIFO");
        var t = Tasks.Create(s, "Worker", $"Task {i}", "", "read");
        var p = Sleeper();
        t.Doc.Set("status", "running");
        t.Doc.Set("pid", p.Id.ToString());
        t.Doc.Save(t.Path);
        runs.Add((t, p));
    }
    var order = Directory.EnumerateFiles(dir, "*.md").ToList();
    var raced = runs.Where(r => order.IndexOf(r.Task.Path) < order.IndexOf(fifo)).ToList();
    Pre(raced.Count > 0, "a task file listed before the FIFO; order: " + string.Join(", ", order.Select(Path.GetFileName)));
    var pids = runs.Select(r => r.Runner.Id).ToList();
    Exception? threw = null;
    var lister = new Thread(() => { try { Tasks.List(s); } catch (Exception e) { threw = e; } });
    lister.Start();
    using (var hold = new FileStream(fifo, FileMode.Open, FileAccess.Write)) // opens once List reads the FIFO
    {
        foreach (var (t, p) in runs)
        {
            // What the runner does at the end of Tasks.Run: save done, exit.
            var now = Tasks.Load(s, t.Id);
            now.Doc.Set("status", "done");
            now.Doc.Set("took", "2 min");
            now.Doc.Save(now.Path);
            p.Kill();
            p.WaitForExit();
            p.Dispose();
        }
        Pre(pids.All(pid => !Directory.Exists($"/proc/{pid}")), "the runners gone");
        hold.Write(System.Text.Encoding.UTF8.GetBytes("---\nbee: Worker\nstatus: done\n---\n\nHeld List.\n"));
    }
    Pre(lister.Join(10_000), "Tasks.List to finish once the FIFO is written");
    if (threw != null) throw new SetupError("Tasks.List threw " + threw.Message);
    var flipped = raced.Select(r => Tasks.Load(s, r.Task.Id)).Where(t => t.Status != "done" || t.Doc.Get("took") != "2 min").ToList();
    True(flipped.Count == 0, $"all {raced.Count} tasks that finished during List done, took 2 min; {flipped.Count} became: {string.Join(", ", flipped.Select(t => $"{t.Status} (took \"{t.Doc.Get("took")}\")"))}");
});

// ---- rv-a #6 a review team setup that fails partway can't be retried ----

Check("rv-a #6 control: the review team sets up with four good names", () =>
{
    using var s = NewHive();
    True(Reviews.SetUp(s, new[] { "Cedar", "Birch", "Ash", "Arbiter" }).Count == 4 && Reviews.TeamOverseer(s) != null, "three reviewers and an Arbiter");
});

Check("rv-a #6 a setup refused for a bad fourth name can be retried with a good one", () =>
{
    using var s = NewHive();
    Pre(Throws(() => Reviews.SetUp(s, ["Cedar", "Birch", "Ash", "Over seer"])), "\"Over seer\" refused");
    True(!Throws(() => Reviews.SetUp(s, new[] { "Cedar", "Birch", "Ash", "Arbiter" })), $"the retry to set the team up; reviewers left from the first try: {string.Join(", ", Reviews.TeamReviewers(s).Select(b => b.Name))}");
    True(Reviews.TeamReviewers(s).Count == 3 && Reviews.TeamOverseer(s)?.Name == "Arbiter", "three reviewers and Arbiter");
});

// Since stage 6 a name that's already a bee isn't refused: that bee gets the role, card and all kept.
Check("rv-a #6 an existing bee named in the setup gets the role, keeps its card, and no second bee is made", () =>
{
    using var s = NewHive();
    s.CreateBee("Arbiter", "My own overseer", [], "Checks every finding.", "approve");
    var card = s.RequireBee("Arbiter").Doc.Body;
    True(!Throws(() => Reviews.SetUp(s, new[] { "Cedar", "Birch", "Ash", "Arbiter" })), "the setup with an existing Arbiter");
    var a = s.RequireBee("Arbiter");
    True(a.Doc.Get("role") == "overseer" && a.Specialty == "My own overseer" && a.Doc.Body == card, $"Arbiter the overseer, its specialty and card kept; role {a.Doc.Get("role")}, specialty {a.Specialty}");
    True(s.Bees().Count(b => b.Name.Equals("Arbiter", StringComparison.OrdinalIgnoreCase)) == 1 && Reviews.TeamReviewers(s).Count == 3, "one Arbiter, three reviewers");
});

// ---- rv-a #10 install and uninstall stop halfway ----
// A fake claude on PATH stands in for `claude mcp add/remove`.

string fakeBin = Path.Combine(scratch, "fakebin");
Directory.CreateDirectory(fakeBin);
File.WriteAllText(Path.Combine(fakeBin, "claude"), """
    #!/bin/sh
    printf '%s\n' "$@" > "$(dirname "$0")/claude.args"
    # Only a task (-p) sends a prompt on stdin; `claude mcp ...` reads none.
    case " $* " in *" -p "*) cat > /dev/null ;; esac
    echo '{"type":"result","subtype":"success","is_error":false,"duration_ms":1000,"result":"ok","session_id":"s-1"}'
    """.Replace("\r", ""));
File.SetUnixFileMode(Path.Combine(fakeBin, "claude"), (UnixFileMode)0b111_101_101);
string realPath = Environment.GetEnvironmentVariable("PATH") ?? "";
string home = Path.Combine(scratch, "home");
string skill = Path.Combine(home, ".claude", "skills", "my-review", "SKILL.md");
string settings = Path.Combine(home, ".claude", "settings.json");

string sandboxData = Environment.GetEnvironmentVariable("XDG_DATA_HOME")!;

// An ERROR, before anything runs, unless install and uninstall would write
// only inside this run's temp folder (HOME, and XDG_DATA_HOME for the launcher).
void Sandboxed()
{
    foreach (var (what, path) in new[] { ("HOME", Text.Home), ("XDG_DATA_HOME (Install.Data)", Install.Data) })
        Pre(Path.GetFullPath(path).StartsWith(scratch + "/"), $"{what} inside the test's temp folder before install or uninstall runs; it's {path}");
}

void WithEnv(string path, string dataHome, Action run)
{
    Environment.SetEnvironmentVariable("PATH", path);
    Environment.SetEnvironmentVariable("XDG_DATA_HOME", dataHome);
    try
    {
        Sandboxed();
        run();
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", realPath);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", sandboxData);
    }
}

// What install leaves when it turned auto memory off.
void AutoMemoryMarked(string data)
{
    Directory.CreateDirectory(Path.Combine(data, "hive"));
    File.WriteAllText(Path.Combine(data, "hive", "auto-memory-off"), "marked by the test\n");
}

void InstalledSkill()
{
    Directory.CreateDirectory(Path.GetDirectoryName(skill)!);
    File.WriteAllText(skill, "---\nname: my-review\n---\n\n<!-- written by hive install -->\n");
    Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
    File.WriteAllText(settings, "{ \"autoMemoryEnabled\": false }\n");
}

// The hive ships no skill: a ~/.claude/skills/<name> is the
// user's own, and install and uninstall leave it byte for byte.
Check("rv-a #10 control: uninstall with the launcher there cleans up and leaves the user's own skill", () =>
{
    InstalledSkill();
    var before = File.ReadAllText(skill);
    var data = Path.Combine(scratch, "data-full");
    Directory.CreateDirectory(Path.Combine(data, "applications"));
    Directory.CreateDirectory(Path.Combine(data, "icons", "hicolor", "scalable", "apps"));
    AutoMemoryMarked(data);
    WithEnv(fakeBin + ":" + realPath, data, () => Install.Remove(_ => { }));
    True(File.Exists(skill) && File.ReadAllText(skill) == before, "the user's skill as it was");
    True(!File.ReadAllText(settings).Contains("autoMemoryEnabled"), "settings.json cleaned (install's mark was there); it is: " + File.ReadAllText(settings).Trim());
});

Check("rv-a #10 uninstall carries on when the launcher's folder is missing", () =>
{
    InstalledSkill();
    var before = File.ReadAllText(skill);
    var missing = Path.Combine(scratch, "data-missing");
    if (Directory.Exists(missing)) Directory.Delete(missing, true);
    WithEnv(fakeBin + ":" + realPath, missing, () => Install.Remove(_ => { }));
    True(File.Exists(skill) && File.ReadAllText(skill) == before, "the user's skill as it was");
    True(File.ReadAllText(settings).Contains("\"autoMemoryEnabled\": false"), "the user's own autoMemoryEnabled: false kept (no mark from install); it is: " + File.ReadAllText(settings).Trim());
});

Check("rv-a #10 uninstall still cleans up when claude isn't on PATH", () =>
{
    InstalledSkill();
    var data = Path.Combine(scratch, "data-full");
    Directory.CreateDirectory(Path.Combine(data, "applications"));
    Directory.CreateDirectory(Path.Combine(data, "icons", "hicolor", "scalable", "apps"));
    var empty = Path.Combine(scratch, "empty-bin");
    Directory.CreateDirectory(empty);
    AutoMemoryMarked(data);
    var said = "";
    WithEnv(empty, data, () =>
    {
        try { Install.Remove(_ => { }); }
        catch (HiveException e) { said = e.Message; } // saying claude is missing is fine, as long as the rest is cleaned up
    });
    True(File.Exists(skill), "the user's own skill still there" + (said.Length > 0 ? $" (Remove stopped with \"{said}\")" : ""));
    True(!File.ReadAllText(settings).Contains("autoMemoryEnabled"), "auto memory back on in settings.json (install's mark was there); it is: " + File.ReadAllText(settings).Trim());
});

Check("rv-a #10 install with a settings.json that isn't JSON says which file", () =>
{
    using var s = NewHive();
    Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
    File.WriteAllText(settings, "{ not json,\n");
    Exception? threw = null;
    WithEnv(fakeBin + ":" + realPath, Path.Combine(scratch, "data-install"), () =>
    {
        try { Install.Run(s, _ => { }, "keep"); }
        catch (Exception e) { threw = e; }
    });
    True(threw is HiveException h && h.Message.Contains("settings.json"), "a HiveException naming settings.json; got " + (threw == null ? "no error" : $"{threw.GetType().Name}: {threw.Message}"));
});

// ---- System.Text.Json's node["key"] throws on a JsonValue ----

Check("install and uninstall carry on when settings.json has a string statusLine and hooks entries that aren't objects", () =>
{
    using var s = NewHive();
    Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
    File.WriteAllText(settings, """{ "statusLine": "x", "hooks": { "Stop": ["x", { "hooks": ["y", { "type": "command", "command": "mine.sh" }] }] } }""" + "\n");
    Exception? threw = null;
    WithEnv(fakeBin + ":" + realPath, Path.Combine(scratch, "data-odd"), () =>
    {
        try { Install.Run(s, _ => { }, "keep"); Install.Run(s, _ => { }, "keep"); Install.Remove(_ => { }); }
        catch (Exception e) { threw = e; }
    });
    True(threw == null, $"no error; got {threw?.GetType().Name}: {threw?.Message}");
    var after = File.ReadAllText(settings);
    True(after.Contains("\"statusLine\": \"x\"") && after.Contains("mine.sh") && !after.Contains(" hook "), "the user's statusLine and hooks kept, ours gone; settings.json: " + after);
});

Check("statusline: a model that isn't an object is no crash", () =>
{
    using var s = NewHive();
    Exception? threw = null;
    try { Usage.StatusLine(s, """{ "model": "Opus" }"""); }
    catch (Exception e) { threw = e; }
    True(threw == null, $"no error; got {threw?.GetType().Name}: {threw?.Message}");
});

// ---- rv-a #11 two card changes in one second lose a step of undo ----

Check("rv-a #11 undo walks back through two card changes made in the same second", () =>
{
    for (int attempt = 0; ; attempt++)
    {
        using var s = NewHive();
        s.CreateBee("Worker", "Does the work", [], "Version zero.", "approve");
        var second = DateTime.Now.ToString("yyyyMMddHHmmss");
        s.EditCard("Worker", "# Worker\n\nVersion one.\n");
        s.EditCard("Worker", "# Worker\n\nVersion two.\n");
        if (DateTime.Now.ToString("yyyyMMddHHmmss") != second)
        {
            Pre(attempt < 5, "two card changes inside one second");
            continue;
        }
        s.UndoCard("Worker");
        True(s.RequireBee("Worker").Doc.Body.Contains("Version one."), "the first undo to give version one; got " + Text.OneLine(s.RequireBee("Worker").Doc.Body));
        string? error = null;
        try { s.UndoCard("Worker"); } catch (HiveException e) { error = e.Message; }
        True(error == null && s.RequireBee("Worker").Doc.Body.Contains("Version zero."), "the second undo to give version zero; got " + (error ?? Text.OneLine(s.RequireBee("Worker").Doc.Body)));
        return;
    }
});

// ---- rv-a #12 "Queen" is accepted as a bee name ----

Check("rv-a #12 control: a bee name that only starts with Queen is fine", () =>
{
    using var s = NewHive();
    True(!Throws(() => s.CreateBee("Queenie", "Does the work", [], "", "approve")), "Queenie made");
});

Check("rv-a #12 a bee can't be named Queen, in any case", () =>
{
    using var s = NewHive();
    foreach (var name in new[] { "Queen", "queen", "QUEEN" })
        True(Throws(() => s.CreateBee(name, "Does the work", [], "", "approve")), $"{name} refused");
});

// ---- rv-a #13 a resets_at out of DateTimeOffset's range crashes hive statusline ----

Check("rv-a #13 a resets_at in milliseconds doesn't crash the status line, or the window reading it", () =>
{
    using var s = NewHive();
    var ms = DateTimeOffset.Now.ToUnixTimeMilliseconds() + 3_600_000;
    string? line = null;
    try { line = Usage.StatusLine(s, "{\"model\":{\"display_name\":\"Opus\"},\"rate_limits\":{\"five_hour\":{\"used_percentage\":42,\"resets_at\":" + ms + "}}}"); }
    catch (Exception e) { True(false, $"a status line, not {e.GetType().Name}: {e.Message}"); }
    True(line!.StartsWith("ʚ●ɞ Opus"), "the line to start with the model; got " + line);
    try { _ = Usage.Load(s)?.Session?.Over; _ = Usage.Load(s)?.Session?.ResetsAt; }
    catch (Exception e) { True(false, $"what was saved to load and show, not {e.GetType().Name}: {e.Message}"); }
});

// ---- rv-a #16 tasks trust the stored pid ----
// After a reboot the pid in a task file can belong to another process.

Check("rv-a #16 a task whose pid now belongs to another program is marked stopped", () =>
{
    using var s = NewHive();
    Worker(s);
    var t = Tasks.Create(s, "Worker", "Look into it", "", "read");
    using var other = Sleeper();
    try
    {
        t.Doc.Set("status", "running");
        t.Doc.Set("pid", other.Id.ToString());
        t.Doc.Save(t.Path);
        Tasks.List(s);
        True(Tasks.Load(s, t.Id).Status == "stopped", $"stopped (pid {other.Id} is `sleep`, not `hive run-task {t.Id}`), got {Tasks.Load(s, t.Id).Status}");
    }
    finally { if (!other.HasExited) other.Kill(); }
});

Check("rv-a #16 Stop doesn't signal a process group that isn't the task's runner", () =>
{
    using var s = NewHive();
    Worker(s);
    var t = Tasks.Create(s, "Worker", "Look into it", "", "read");
    // setsid makes the sleep lead a process group of its own, as a runner does.
    using var other = Process.Start("setsid", ["sleep", "60"]);
    try
    {
        Thread.Sleep(200);
        Pre(!other.HasExited && File.ReadAllText($"/proc/{other.Id}/cmdline").StartsWith("sleep"), "a sleep leading its own group");
        t.Doc.Set("status", "running");
        t.Doc.Set("pid", other.Id.ToString());
        t.Doc.Save(t.Path);
        Tasks.Stop(s, t.Id);
        True(!other.WaitForExit(500), "the unrelated sleep still running after Stop");
    }
    finally { if (!other.HasExited) other.Kill(); }
});

// ---- rv-a #17 the Queen page is written in place while hooks read it ----
// A reader racing the writer must see the old page or the new one, never an
// empty or half-written file.

int TornReads(string path, Action<string> write)
{
    var a = "# Queen\n\n" + new string('a', 300_000) + "\n";
    var b = "# Queen\n\n" + new string('b', 300_000) + "\n";
    write(a);
    var stop = false;
    var torn = 0;
    var reader = new Thread(() =>
    {
        while (!Volatile.Read(ref stop))
        {
            string got;
            try { got = File.ReadAllText(path); } catch (FileNotFoundException) { torn++; continue; }
            if (got != a && got != b) torn++;
        }
    });
    reader.Start();
    for (int i = 0; i < 200; i++) write(i % 2 == 0 ? b : a);
    Volatile.Write(ref stop, true);
    reader.Join();
    return torn;
}

Check("rv-a #17 control: a reader never sees a page moved into place half-written", () =>
{
    using var s = NewHive();
    var path = Path.Combine(s.Root, "comb", "global", "page.md");
    // A temp file moved into place, as Doc.Save and Usage.StatusLine write.
    var torn = TornReads(path, text => { File.WriteAllText(path + ".tmp", text); File.Move(path + ".tmp", path, overwrite: true); });
    True(torn == 0, $"no torn reads; {torn}");
});

Check("rv-a #17 a hook reading the Queen page never sees it half-written", () =>
{
    using var s = NewHive();
    var path = Path.Combine(s.Root, "queen.md");
    s.SaveQueen("# Queen\n");
    Pre(s.QueenText == "# Queen\n" && File.Exists(path), "the Queen page at queen.md");
    var torn = TornReads(path, text => s.SaveQueen(text));
    True(torn == 0, $"no empty or half-written reads; {torn} while SaveQueen ran 200 times");
});

// ---- rv-a #18 a card proposed back from hive_read keeps its header ----
// hive_read's output starts with a "[bee:Worker] ~/…" line, so the card's
// front matter isn't at the top and ended up in the card body.

Check("rv-a #18 approving a card copied from hive_read leaves one front-matter block and the card", () =>
{
    using var s = NewHive();
    s.SetProject("alpha", "Alpha", null, null);
    s.SetProject("beta", "Beta", null, null);
    s.CreateBee("Worker", "Does the work", ["alpha", "beta"], "Old way.", "approve");
    var read = s.Read("bee:Worker");
    Pre(read.StartsWith("[bee:Worker] "), "hive_read's output to start with its [bee:Worker] line; it starts " + read.Split('\n')[0]);
    s.Molt("Worker", "drop beta", read.Replace("projects: [alpha, beta]", "projects: [alpha]").Replace("Old way.", "New way."));
    s.DecideMolt(s.PendingMolts()[0].Id, true);
    var bee = s.RequireBee("Worker");
    True(FrontMatterBlocks(bee.Path) == 1 && !bee.Doc.Body.Contains("[bee:Worker]"), "one block and no [bee:Worker] line; the file is:\n" + File.ReadAllText(bee.Path));
    True(bee.Doc.Body.TrimStart().StartsWith("# Worker") && bee.Doc.Body.Contains("New way."), "the body to be the card: " + Text.OneLine(bee.Doc.Body));
    True(bee.Projects is ["alpha"], "projects to be [alpha], not " + string.Join(", ", bee.Projects));
});

// ---- rv-a #19 the hooks delete usage.json after two quiet days ----

Check("rv-a #19 a hook's clean-up of old .state files keeps the saved usage", () =>
{
    using var s = NewHive();
    Worker(s);
    var at = DateTimeOffset.Now.ToUnixTimeSeconds() + 3 * 86400;
    Usage.StatusLine(s, "{\"rate_limits\":{\"seven_day\":{\"used_percentage\":23,\"resets_at\":" + at + "}}}");
    var stale = Path.Combine(s.Root, ".state", "old-agent");
    File.WriteAllText(stale, "Worker\n0\n");
    foreach (var f in new[] { Usage.FilePath(s), stale }) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-3));
    Pre(Usage.Load(s)?.Week is { Used: 23 }, "the usage saved");
    Hooks.Handle(s, "subagent-start", new System.Text.Json.Nodes.JsonObject { ["agent_type"] = "worker", ["agent_id"] = "agent-1", ["session_id"] = "s-1", ["cwd"] = scratch });
    Pre(!File.Exists(stale), "(control) the hook cleared the old agent's .state file");
    True(Usage.Load(s)?.Week is { Used: 23 }, "the 7-day usage still there");
});

// The `by` half of #2, continued: Tasks.Run hands the hive server the task's bee.

foreach (var hiveHome in new[] { true, false })
    Check($"rv-a #2 a task's hive server is told which bee the task runs as ({(hiveHome ? "with" : "without")} HIVE_HOME)", () =>
    {
        using var s = NewHive();
        Worker(s);
        var t = Tasks.Create(s, "Worker", "Look into it", "", "read");
        var log = Path.Combine(fakeBin, "claude.args");
        File.Delete(log);
        var home = Environment.GetEnvironmentVariable("HIVE_HOME");
        if (!hiveHome) Environment.SetEnvironmentVariable("HIVE_HOME", null);
        try { WithEnv(fakeBin + ":" + realPath, "", () => Tasks.Run(s, t.Id)); }
        finally { Environment.SetEnvironmentVariable("HIVE_HOME", home); }
        Pre(File.Exists(log), "the fake claude to run; the task says: " + Tasks.Load(s, t.Id).Result.Trim());
        var args = File.ReadAllLines(log).ToList();
        var config = args.IndexOf("--mcp-config") is var at and >= 0 ? args[at + 1] : "";
        var env = config.Length > 0 ? System.Text.Json.Nodes.JsonNode.Parse(config)?["mcpServers"]?["hive"]?["env"] : null;
        True(Json.Str(env?["HIVE_TASK_BEE"]) == "Worker", "the hive server's env to have HIVE_TASK_BEE=Worker; --mcp-config: " + (config.Length > 0 ? config : "(none)"));
    });

// ---- rv-a #20 with HIVE_HOME set, tasks lose the user's other MCP servers ----
// Runs a task with a fake claude that writes down its arguments.

Check("rv-a #20 a task in a hive at HIVE_HOME keeps the user's other MCP servers (no --strict-mcp-config)", () =>
{
    using var s = NewHive();
    Worker(s);
    var t = Tasks.Create(s, "Worker", "Look into it", "", "read");
    var log = Path.Combine(fakeBin, "claude.args");
    File.Delete(log);
    WithEnv(fakeBin + ":" + realPath, "", () => Tasks.Run(s, t.Id));
    Pre(File.Exists(log), "the fake claude to run; the task says: " + Tasks.Load(s, t.Id).Result.Trim());
    var args = File.ReadAllLines(log).ToList();
    var config = args.IndexOf("--mcp-config") is var at and >= 0 ? args[at + 1] : "";
    Pre(config.Contains(s.Root), "a --mcp-config pointing the hive server at this hive; args: " + string.Join(' ', args));
    True(!args.Contains("--strict-mcp-config"), "no --strict-mcp-config");
});

// ---- work pane wp2: a Queen turn's command line (a fake claude writes down its arguments) ----

foreach (var access in new[] { "read", "edit" })
    Check($"queen: a Queen turn runs claude with no --agent, and with the Agent tool allowed ({access})", () =>
    {
        using var s = NewHive();
        var t = Tasks.QueenTalk(s, "hand this to a bee", "", access, null);
        var log = Path.Combine(fakeBin, "claude.args");
        File.Delete(log);
        WithEnv(fakeBin + ":" + realPath, "", () => Tasks.Run(s, t.Id));
        Pre(File.Exists(log), "the fake claude to run; the turn says: " + Tasks.Load(s, t.Id).Result.Trim());
        var args = File.ReadAllLines(log).ToList();
        True(!args.Contains("--agent"), "no --agent; args: " + string.Join(' ', args));
        var allowed = args.IndexOf("--allowedTools") is var at and >= 0 ? args[at + 1].Split(',') : [];
        True(allowed.Contains("Agent"), "Agent among --allowedTools: " + string.Join(",", allowed));
        True(args.Contains("-p") && args[args.IndexOf("-n") + 1] == "Hive talk: Queen", "-p and -n \"Hive talk: Queen\"");
        var config = args.IndexOf("--mcp-config") is var m and >= 0 ? args[m + 1] : "";
        var env = config.Length > 0 ? System.Text.Json.Nodes.JsonNode.Parse(config)?["mcpServers"]?["hive"]?["env"] : null;
        True(env != null && env["HIVE_TASK_BEE"] == null && Json.Str(env["HIVE_HOME"]) == s.Root, "the hive server told this hive, and no bee to run as; env: " + env?.ToJsonString());
        True(Tasks.Load(s, t.Id).Status == "done", "the turn done; it's " + Tasks.Load(s, t.Id).Status);
    });

Check("queen: control: a bee's task runs as its agent, without the Agent tool", () =>
{
    using var s = NewHive();
    s.CreateBee("Worker", "Does the work", [], "Works.", "approve");
    var t = Tasks.Create(s, "Worker", "Look into it", "", "read");
    var args = Tasks.Args(t);
    var allowed = args.IndexOf("--allowedTools") is var at and >= 0 ? args[at + 1].Split(',') : [];
    True(args.Contains("--agent") && args[args.IndexOf("--agent") + 1] == "worker" && !allowed.Contains("Agent"), "--agent worker, no Agent; args: " + string.Join(' ', args));
});

// ---- rv-a #21 the MCP server says it's version 0.1.0 ----

Check("rv-a #21 the MCP server's serverInfo.version is the product's version", () =>
{
    var csproj = File.ReadAllText(Path.Combine(repo, "hive.csproj"));
    var version = Regex.Match(csproj, "<Version>(.*?)</Version>").Groups[1].Value;
    Pre(version.Length > 0, "a <Version> in hive.csproj");
    using var s = NewHive();
    var info = new ProcessStartInfo(Environment.ProcessPath!, ["mcp"]) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
    info.Environment["HIVE_HOME"] = s.Root;
    using var server = Process.Start(info)!;
    server.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
    server.StandardInput.Close();
    var reply = server.StandardOutput.ReadLine();
    server.WaitForExit(5000);
    Pre(reply != null, "a reply to initialize");
    var got = Json.Str(System.Text.Json.Nodes.JsonNode.Parse(reply!)?["result"]?["serverInfo"]?["version"]);
    True(got == version || got.StartsWith(version + "+"), $"version {version} (hive.csproj), got \"{got}\"");
});

// ---- stage 2: automatic honey and misses ----
// Honey is reward: yes, a miss reward: no, each with reward_by (user, queen,
// hive), rewarded and reward_note. The hive awards reviewers from the
// Arbiter's "## Reviewers" table (Reviews.Award, on Report and Tasks.Run);
// the Queen awards with bee_reward (Store.Award). These go through Award,
// Report, Tasks.Run, Store.Award, RewardEntry, Review and the MCP server by
// name; the table parser (Reviews.Confirmed, private) only through Award.

// A finished review run in s: Ash, Birch and Cedar (status as given,
// done by default) and the Arbiter done with this report.
static (string Run, BeeTask Over) ReviewDone(Store s, string run, string report, Dictionary<string, string>? status = null)
{
    if (Reviews.TeamOverseer(s) == null) Reviews.SetUp(s, new[] { "Cedar", "Birch", "Ash", "Arbiter" });
    var reviewers = new[] { "Ash", "Birch", "Cedar" }.Select(b =>
    {
        var t = Tasks.Create(s, b, "Review src/", "", "read", run, "reviewer");
        Tasks.Record(s, t, $"{b}'s findings", status?.GetValueOrDefault(b) ?? "done");
        return t;
    }).ToList();
    var over = Tasks.Create(s, "Arbiter", "Oversee " + run, "", "read", run, "overseer", reviewers.Select(t => t.Id));
    Tasks.Record(s, over, report, "done");
    return (run, Tasks.Load(s, over.Id));
}

static BeeTask ReviewerTask(Store s, string run, string bee) => Tasks.List(s, bee).First(t => t.Doc.Get("run") == run);

// What a reviewer's task says about its honey: "honey 10", "miss 0", "none" (no award), or other text.
static string AwardOf(Store s, string run, string bee)
{
    var d = ReviewerTask(s, run, bee).Doc;
    if (d.Get("reward").Length == 0) return "none";
    var n = Regex.Match(d.Get("reward_note"), @"confirmed (\d+|none) of its findings in " + Regex.Escape(run)).Groups[1].Value;
    return (d.Get("reward"), d.Get("reward_by")) switch
    {
        ("yes", "hive") => "honey " + n,
        ("no", "hive") => "miss " + (n == "none" ? "0" : n),
        var (r, by) => $"reward {r} by {by}",
    };
}

static string Awards(Store s, string run) => string.Join(", ", new[] { "Ash", "Birch", "Cedar" }.Select(b => $"{b}: {AwardOf(s, run, b)}"));

static string Table(params string[] rows) => "## Reviewers\n| reviewer | confirmed | rejected | found alone |\n|---|---|---|---|\n" + string.Join("\n", rows) + "\n";

// A task's reward fields, to compare before and after.
static string RewardFields(Doc d) => $"reward={d.Get("reward")} by={d.Get("reward_by")} at={d.Get("rewarded")} note={d.Get("reward_note")}";

// Made-up Arbiter reports (an imaginary project, invented findings) in
// tests/StoreTests/fixtures/overseer, shaped like real ones: the Reviewers
// table with "10 of 10 (one lowered)", "**11** (8 bugs, 3 bloat)" and
// backticked names, a ### heading, a table under ## Confirmed and a row under
// ## Not covered (neither counts). The confirmed counts each should give:
var overseerFixtures = new Dictionary<string, (int Ash, int Birch, int Cedar)>
{
    ["review-20300101-090000"] = (4, 5, 3),
    ["review-20300102-100000"] = (10, 7, 6),
    ["review-20300103-110000"] = (12, 11, 9),
    ["review-20300104-120000"] = (2, 3, 1),
    ["review-20300105-130000"] = (6, 8, 5),
    ["review-20300106-140000"] = (17, 15, 14),
};

Check("honey: each made-up Arbiter report (fixtures) gives each reviewer honey from the hive with its confirmed count", () =>
{
    var dir = Path.Combine(repo, "tests", "StoreTests", "fixtures", "overseer");
    Pre(Directory.Exists(dir) && Directory.GetFiles(dir, "*.md").Length == overseerFixtures.Count, $"{overseerFixtures.Count} reports in {dir}");
    var wrong = new List<string>();
    foreach (var (run, (b, p, t)) in overseerFixtures)
    {
        using var s = NewHive();
        ReviewDone(s, run, File.ReadAllText(Path.Combine(dir, run + ".md")));
        Reviews.Award(s, Reviews.Find(s, run));
        var want = $"Ash: honey {b}, Birch: honey {p}, Cedar: honey {t}";
        if (Awards(s, run) != want) wrong.Add($"{run}: want {want}; got {Awards(s, run)}");
    }
    True(wrong.Count == 0, "the counts in each table; " + string.Join("; ", wrong));
});

foreach (var (what, report, want) in new[]
{
    ("plain numbers, 0 is a miss", Table("| Ash | 3 | 0 | x |", "| Birch | 0 | 2 | none |", "| Cedar | 1 | 0 | y |"), "Ash: honey 3, Birch: miss 0, Cedar: honey 1"),
    ("\"10 of 10 (one lowered)\" and \"0 of 3\"", Table("| Ash | 10 of 10 (one lowered) | 0 | x |", "| Birch | 0 of 3 | 3 | none |", "| Cedar | 2 of 4 | 2 | y |"), "Ash: honey 10, Birch: miss 0, Cedar: honey 2"),
    ("bold and backticked names and counts", Table("| **Ash** | **4** | 0 | x |", "| `Birch` | 5 | 0 | y |", "| **`Cedar`** | 6 (15 bugs) | 0 | z |"), "Ash: honey 4, Birch: honey 5, Cedar: honey 6"),
    ("### Reviewers, lower-case names, no outer pipes trimmed", "### Reviewers\n\n| reviewer | confirmed | rejected | found alone |\n|:--|--:|--:|:--|\n| ash | 7 | 0 | x |\n|birch|8|0|y|\n|   CEDAR   |   9   | 0 | z |\n", "Ash: honey 7, Birch: honey 8, Cedar: honey 9"),
    ("\"## Reviewers (credit)\" and a table with CRLF line ends", Table("| Ash | 1 | 0 | x |", "| Birch | 2 | 0 | y |", "| Cedar | 3 | 0 | z |").Replace("## Reviewers", "## Reviewers (credit)").Replace("\n", "\r\n"), "Ash: honey 1, Birch: honey 2, Cedar: honey 3"),
    ("rows under other headings are ignored", "# Review\n## Confirmed\n| Ash | 9 | 0 | x |\n" + Table("| Birch | 2 | 0 | y |") + "Some text after.\n## Not covered\n| Cedar | 4 | 0 | z |\n| Ash | 0 | 0 | z |\n", "Ash: none, Birch: honey 2, Cedar: none"),
    ("a row without a number gives nothing", Table("| Ash | none confirmed | 2 | x |", "| Birch | – | 0 | y |", "| Cedar | | 0 | z |"), "Ash: none, Birch: none, Cedar: none"),
    ("no Reviewers table at all", "# Review\n## Confirmed\n1. a bug\n## Rejected\n- nothing\n", "Ash: none, Birch: none, Cedar: none"),
    ("a reworded heading gives nothing rather than a wrong award", "## Per reviewer\n| reviewer | confirmed | rejected | found alone |\n|---|---|---|---|\n| Ash | 3 | 0 | x |\n**Reviewers**\n| Birch | 0 | 1 | y |\n", "Ash: none, Birch: none, Cedar: none"),
    ("a reviewer named twice: the first row counts", Table("| Ash | 3 | 0 | x |", "| Ash | 0 | 5 | y |", "| Birch | 1 | 0 | z |"), "Ash: honey 3, Birch: honey 1, Cedar: none"),
})
    Check("honey: the Reviewers table, " + what, () =>
    {
        using var s = NewHive();
        var (run, _) = ReviewDone(s, "review-20261021-120000", report);
        Reviews.Award(s, Reviews.Find(s, run));
        True(Awards(s, run) == want, $"{want}; got {Awards(s, run)}");
    });

// The card's table is | reviewer | confirmed | rejected | found alone |, and
// Award reads the second cell whatever the header says. An Arbiter that
// writes the columns in another order would give honey for rejections. A
// fix may read the column by its header or give nothing; never a wrong award.
Check("honey: a Reviewers table with its columns in another order never awards from the wrong column", () =>
{
    using var s = NewHive();
    var (run, _) = ReviewDone(s, "review-20261021-120000",
        "## Reviewers\n| reviewer | rejected | confirmed | found alone |\n|---|---|---|---|\n| Ash | 0 | 5 | x |\n| Birch | 4 | 0 | y |\n| Cedar | 0 | 2 | z |\n");
    Reviews.Award(s, Reviews.Find(s, run));
    var got = Awards(s, run);
    True(got == "Ash: honey 5, Birch: miss 0, Cedar: honey 2" || got == "Ash: none, Birch: none, Cedar: none",
         "honey for Ash (5 confirmed) and Cedar (2), a miss for Birch (0), or nothing at all; got " + got);
});

Check("honey: a reviewer that didn't finish (failed, stopped) gets nothing from the table", () =>
{
    using var s = NewHive();
    var (run, _) = ReviewDone(s, "review-20261021-120000", Table("| Ash | 3 | 0 | x |", "| Birch | 0 | 0 | y |", "| Cedar | 2 | 0 | z |"),
        new() { ["Birch"] = "failed", ["Cedar"] = "stopped" });
    Reviews.Award(s, Reviews.Find(s, run));
    True(Awards(s, run) == "Ash: honey 3, Birch: none, Cedar: none", "honey for Ash only; got " + Awards(s, run));
});

Check("honey: control: an Arbiter that isn't done (failed) awards nothing", () =>
{
    using var s = NewHive();
    var (run, over) = ReviewDone(s, "review-20261021-120000", Table("| Ash | 3 | 0 | x |", "| Birch | 0 | 0 | y |", "| Cedar | 2 | 0 | z |"));
    over.Doc.Set("status", "failed");
    over.Doc.Save(over.Path);
    Reviews.Award(s, Reviews.Find(s, run));
    True(Awards(s, run) == "Ash: none, Birch: none, Cedar: none", "nothing; got " + Awards(s, run));
});

Check("honey: the hive's award never overwrites what's there: the user's honey or miss, old honey without reward_by, the Queen's, or its own earlier award", () =>
{
    using var s = NewHive();
    var (run, _) = ReviewDone(s, "review-20261021-120000", Table("| Ash | 0 | 3 | x |", "| Birch | 5 | 0 | y |", "| Cedar | 0 | 1 | z |"));
    Tasks.Reward(s, ReviewerTask(s, run, "Ash").Id, "the user liked it", true);                        // the user's honey, but the table says 0
    Tasks.Reward(s, ReviewerTask(s, run, "Birch").Id, "missed the point", true, "user", miss: true);        // the user's miss, but the table says 5
    var old = ReviewerTask(s, run, "Cedar");                                                               // honey from before reward_by
    old.Doc.Set("reward", "yes");
    old.Doc.Set("rewarded", "2026-10-18 12:00");
    old.Doc.Set("reward_note", "old honey");
    old.Doc.Save(old.Path);
    var before = new[] { "Ash", "Birch", "Cedar" }.Select(b => RewardFields(ReviewerTask(s, run, b).Doc)).ToList();
    Reviews.Award(s, Reviews.Find(s, run));
    var after = new[] { "Ash", "Birch", "Cedar" }.Select(b => RewardFields(ReviewerTask(s, run, b).Doc)).ToList();
    True(before.SequenceEqual(after), "each reward as it was; before: " + string.Join(" | ", before) + "; after: " + string.Join(" | ", after));

    using var s2 = NewHive();
    var (run2, _) = ReviewDone(s2, "review-20261021-130000", Table("| Ash | 2 | 0 | x |", "| Birch | 0 | 0 | y |", "| Cedar | 1 | 0 | z |"));
    s2.Award("task:" + ReviewerTask(s2, run2, "Cedar").Id, "miss", "its one finding was a duplicate (task:x)", "queen");
    Reviews.Award(s2, Reviews.Find(s2, run2));
    var first = new[] { "Ash", "Birch", "Cedar" }.Select(b => RewardFields(ReviewerTask(s2, run2, b).Doc)).ToList();
    True(ReviewerTask(s2, run2, "Cedar").Doc.Get("reward_by") == "queen" && ReviewerTask(s2, run2, "Cedar").Doc.Get("reward") == "no", "the Queen's miss for Cedar kept; " + first[2]);
    Thread.Sleep(1100); // a second award a second later would show in rewarded
    Reviews.Award(s2, Reviews.Find(s2, run2));
    var second = new[] { "Ash", "Birch", "Cedar" }.Select(b => RewardFields(ReviewerTask(s2, run2, b).Doc)).ToList();
    True(first.SequenceEqual(second), "a second Award changes nothing; first: " + string.Join(" | ", first) + "; second: " + string.Join(" | ", second));
});

Check("honey: session-led review: team_review_report for the Arbiter gives the reviewers honey or a miss and says so", () =>
{
    using var s = NewHive();
    Reviews.SetUp(s, new[] { "Cedar", "Birch", "Ash", "Arbiter" });
    Reviews.Begin(s, "", "src/", scratch);
    var run = Reviews.Runs(s).First().Id;
    foreach (var b in new[] { "Ash", "Birch", "Cedar" }) Reviews.Report(s, run, b, $"{b}'s report");
    var said = Reviews.Report(s, run, "Arbiter", "# Review\n" + Table("| Ash | 4 | 0 | x |", "| Birch | 0 | 2 | y |", "| Cedar | 1 of 1 | 0 | z |") + "## Not covered\n");
    True(Awards(s, run) == "Ash: honey 4, Birch: miss 0, Cedar: honey 1", "honey 4, a miss, honey 1; got " + Awards(s, run));
    True(said.Contains("honey for Ash") && said.Contains("a miss for Birch") && said.Contains("honey for Cedar"), "the reply names what the hive gave; it says: " + said);
});

Check("honey: control: a reviewer's own team_review_report awards nothing yet", () =>
{
    using var s = NewHive();
    Reviews.SetUp(s, new[] { "Cedar", "Birch", "Ash", "Arbiter" });
    Reviews.Begin(s, "", "src/", scratch);
    var run = Reviews.Runs(s).First().Id;
    Reviews.Report(s, run, "Ash", Table("| Ash | 4 | 0 | x |"));
    True(Awards(s, run) == "Ash: none, Birch: none, Cedar: none", "nothing before the Arbiter's report; got " + Awards(s, run));
});

// Tasks.Run with a stand-in claude on PATH that prints a stream-json result.
void RunWithStubClaude(Store s, BeeTask task, string result, bool isError = false)
{
    var bin = Path.Combine(scratch, "stub-bin");
    Directory.CreateDirectory(bin);
    var stub = Path.Combine(bin, "claude");
    File.WriteAllText(stub, "#!/bin/sh\ncat > \"${HIVE_TEST_STUB_PROMPT:-/dev/null}\"\ncat \"$HIVE_TEST_STUB_RESULT\"\n");
    File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var json = new System.Text.Json.Nodes.JsonObject { ["type"] = "result", ["subtype"] = "success", ["is_error"] = isError, ["result"] = result, ["session_id"] = "stub-session", ["duration_ms"] = 10.0 };
    var resultFile = Path.Combine(bin, task.Id + ".json");
    File.WriteAllText(resultFile, json.ToJsonString() + "\n");
    var (path, stubbed) = (Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("HIVE_TEST_STUB_RESULT"));
    try
    {
        Environment.SetEnvironmentVariable("PATH", bin + ":" + path);
        Environment.SetEnvironmentVariable("HIVE_TEST_STUB_RESULT", resultFile);
        Pre(Text.Which("claude") == stub, "the stub claude first on PATH");
        Tasks.Run(s, task.Id);
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", path);
        Environment.SetEnvironmentVariable("HIVE_TEST_STUB_RESULT", stubbed);
    }
}

// A review run whose reviewers are done and whose Arbiter is still to run.
(string Run, BeeTask Over) ReviewToOversee(Store s, string run)
{
    var (r, over) = ReviewDone(s, run, "placeholder");
    over.Doc.Set("status", "queued");
    over.Doc.Remove("finished");
    over.Doc.Save(over.Path);
    File.Delete(over.ResultPath);
    return (r, Tasks.Load(s, over.Id));
}

Check("honey: background review: Tasks.Run of the Arbiter (a stub claude) gives the reviewers honey or a miss", () =>
{
    using var s = NewHive();
    var (run, over) = ReviewToOversee(s, "review-20261021-120000");
    RunWithStubClaude(s, over, "# Review\n" + Table("| Ash | 2 | 0 | x |", "| Birch | 0 | 1 | y |", "| Cedar | 3 of 3 | 0 | z |"));
    Pre(Tasks.Load(s, over.Id).Status == "done", "the Arbiter's run done; it's " + Tasks.Load(s, over.Id).Status + ": " + Tasks.Load(s, over.Id).Result);
    True(Awards(s, run) == "Ash: honey 2, Birch: miss 0, Cedar: honey 3", "honey 2, a miss, honey 3; got " + Awards(s, run));
});

Check("honey: control: background review whose Arbiter run fails (is_error) awards nothing", () =>
{
    using var s = NewHive();
    var (run, over) = ReviewToOversee(s, "review-20261021-120000");
    RunWithStubClaude(s, over, "# Review\n" + Table("| Ash | 2 | 0 | x |", "| Birch | 0 | 1 | y |", "| Cedar | 3 | 0 | z |"), isError: true);
    Pre(Tasks.Load(s, over.Id).Status == "failed", "the Arbiter's run failed; it's " + Tasks.Load(s, over.Id).Status);
    True(Awards(s, run) == "Ash: none, Birch: none, Cedar: none", "nothing; got " + Awards(s, run));
});

// ---- bee_reward (Store.Award): the Queen's honey and misses ----

// A done task of bee with result "Found it.".
static BeeTask DoneTask(Store s, string bee)
{
    if (s.FindBee(bee) == null) s.CreateBee(bee, "Does the work", [], "", "approve");
    var t = Tasks.Create(s, bee, "Look into it", "", "read");
    Tasks.Record(s, t, "Found it.", "done");
    return Tasks.Load(s, t.Id);
}

Check("bee_reward: the Queen gives honey and a miss with the evidence as the note; by left empty is the Queen", () =>
{
    using var s = NewHive();
    var a = DoneTask(s, "Mason");
    var b = DoneTask(s, "Smith");
    s.Award("task:" + a.Id, "honey", "stage 3 builds and tests/run.sh is green (task:x)", "queen");
    s.Award("task:" + b.Id, "miss", "the finding came back in review-y #3", "");
    var (da, db) = (Tasks.Load(s, a.Id).Doc, Tasks.Load(s, b.Id).Doc);
    True(da.Get("reward") == "yes" && da.Get("reward_by") == "queen" && da.Get("reward_note") == "stage 3 builds and tests/run.sh is green (task:x)" && da.Get("rewarded").Length > 0, "Mason's honey from the Queen; " + RewardFields(da));
    True(db.Get("reward") == "no" && db.Get("reward_by") == "queen" && db.Get("reward_note") == "the finding came back in review-y #3", "Smith's miss from the Queen; " + RewardFields(db));
    True(Tasks.Load(s, a.Id).Rewarded && !Tasks.Load(s, b.Id).Rewarded, "honey counts as Rewarded, a miss doesn't");
});

Check("bee_reward: an entry a bee wrote takes the Queen's honey; one the Queen wrote is refused", () =>
{
    using var s = NewHive();
    s.CreateBee("Mason", "Builds", [], "", "approve");
    var bees = s.Deposit("Mason", "worklog", "Stage 3 done", "Stage 3 built", "body", "", [], []);
    var queens = s.Deposit("queen", "note", "A note", "A note by the Queen", "body", "", [], []);
    s.Award(bees, "honey", "the user committed it as is (commit abc123)", "queen");
    True(Doc.Load(s.Locate(bees)!.Value.Item2).Get("reward_by") == "queen", "the worklog's honey from the Queen");
    True(Throws(() => s.Award(queens, "honey", "evidence", "queen")), "the Queen's own entry refused");
});

foreach (var (what, giver) in new[] { ("its own bee", "Birch"), ("its own bee in another case", "birch"), ("another bee", "Ash"), ("the hive", "hive"), ("the user", "user") })
    Check($"bee_reward: refused when the giver is {what} (\"{giver}\"), and the task is untouched", () =>
    {
        using var s = NewHive();
        var t = DoneTask(s, "Birch");
        s.CreateBee("Ash", "Breaks", [], "", "approve");
        var before = RewardFields(Tasks.Load(s, t.Id).Doc);
        True(Throws(() => s.Award("task:" + t.Id, "honey", "the Arbiter confirmed 3 (task:x)", giver)), $"refused for by \"{giver}\"");
        True(RewardFields(Tasks.Load(s, t.Id).Doc) == before, "untouched; " + RewardFields(Tasks.Load(s, t.Id).Doc));
    });

foreach (var (what, evidence, outcome) in new[] { ("empty evidence", "", "honey"), ("blank evidence", "  \n\t ", "miss"), ("an outcome that isn't honey or miss", "evidence", "yes"), ("an empty outcome", "evidence", "") })
    Check($"bee_reward: refused with {what}, and the task is untouched", () =>
    {
        using var s = NewHive();
        var t = DoneTask(s, "Birch");
        True(Throws(() => s.Award("task:" + t.Id, outcome, evidence, "queen")), "refused");
        True(Tasks.Load(s, t.Id).Doc.Get("reward") == "", "no reward; " + RewardFields(Tasks.Load(s, t.Id).Doc));
    });

foreach (var (what, set) in new (string, Action<Store, BeeTask>)[]
{
    ("the user's honey", (s, t) => Tasks.Reward(s, t.Id, "great", true)),
    ("the user's miss", (s, t) => Tasks.Reward(s, t.Id, "off", true, "user", miss: true)),
    ("old honey without reward_by (the user's)", (s, t) => { t.Doc.Set("reward", "yes"); t.Doc.Set("rewarded", "2026-10-18 12:00"); t.Doc.Save(t.Path); }),
})
    foreach (var outcome in new[] { "honey", "miss" })
        Check($"bee_reward: {outcome} refused on {what}, which stays as it was", () =>
        {
            using var s = NewHive();
            var t = DoneTask(s, "Birch");
            set(s, Tasks.Load(s, t.Id));
            var before = RewardFields(Tasks.Load(s, t.Id).Doc);
            True(Throws(() => s.Award("task:" + t.Id, outcome, "the Arbiter confirmed 3 (task:x)", "queen")), "refused");
            True(RewardFields(Tasks.Load(s, t.Id).Doc) == before, $"as it was ({before}); now {RewardFields(Tasks.Load(s, t.Id).Doc)}");
        });

// The Queen asked (2026-10-21) for one rule to change: the hive's award comes
// from a checkable outcome (the Arbiter's count), so the Queen's bee_reward
// must not overwrite it. Stage 2 as built lets her (only the user's is kept).
foreach (var outcome in new[] { "honey", "miss" })
    Check($"bee_reward: the Queen's {outcome} doesn't overwrite the hive's award from the Arbiter's count", () =>
    {
        using var s = NewHive();
        var (run, _) = ReviewDone(s, "review-20261021-120000", Table("| Ash | 0 | 2 | x |", "| Birch | 4 | 0 | y |", "| Cedar | 1 | 0 | z |"));
        Reviews.Award(s, Reviews.Find(s, run));
        var t = ReviewerTask(s, run, outcome == "honey" ? "Ash" : "Birch");
        Pre(t.Doc.Get("reward_by") == "hive", "the hive's award there first");
        var before = RewardFields(t.Doc);
        try { s.Award("task:" + t.Id, outcome, "the Queen thinks otherwise", "queen"); } catch (HiveException) { }
        True(RewardFields(Tasks.Load(s, t.Id).Doc) == before, $"the hive's award kept ({before}); now {RewardFields(Tasks.Load(s, t.Id).Doc)}");
    });

Check("bee_reward: control: the Queen may judge a task nobody judged yet, and change her own judgment", () =>
{
    using var s = NewHive();
    var t = DoneTask(s, "Birch");
    s.Award("task:" + t.Id, "honey", "tests green (task:x)", "queen");
    s.Award("task:" + t.Id, "miss", "the user reverted it (commit def456)", "queen");
    var d = Tasks.Load(s, t.Id).Doc;
    True(d.Get("reward") == "no" && d.Get("reward_by") == "queen" && d.Get("reward_note") == "the user reverted it (commit def456)", "her miss replaced her honey; " + RewardFields(d));
});

Check("bee_reward: honey for a task that isn't done is refused; a miss for a failed one is allowed; a running one takes neither", () =>
{
    using var s = NewHive();
    s.CreateBee("Birch", "Probes", [], "", "approve");
    var failed = Tasks.Create(s, "Birch", "Look into it", "", "read");
    Tasks.Record(s, failed, "It broke.", "failed");
    True(Throws(() => s.Award("task:" + failed.Id, "honey", "evidence", "queen")), "honey for a failed task refused");
    s.Award("task:" + failed.Id, "miss", "it failed: the build broke (task:x)", "queen");
    True(Tasks.Load(s, failed.Id).Doc.Get("reward") == "no", "a miss for the failed task");
    var running = Tasks.Create(s, "Birch", "Still at it", "", "read");
    using var runner = Runner(running.Id);
    try
    {
        running.Doc.Set("status", "running");
        running.Doc.Set("pid", runner.Id.ToString());
        running.Doc.Save(running.Path);
        True(Throws(() => s.Award("task:" + running.Id, "miss", "evidence", "queen")), "a miss for a running task refused");
        True(Tasks.Load(s, running.Id).Doc.Get("reward") == "", "the running task untouched");
    }
    finally { runner.Kill(entireProcessTree: true); }
});

// The MCP server: in a task's session HIVE_TASK_BEE is the giver, whatever by says.
string McpCall(Store s, string? taskBee, string tool, System.Text.Json.Nodes.JsonObject arguments)
{
    var info = new ProcessStartInfo(Environment.ProcessPath!, ["mcp"]) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
    info.Environment["HIVE_HOME"] = s.Root;
    if (taskBee != null) info.Environment["HIVE_TASK_BEE"] = taskBee; else info.Environment.Remove("HIVE_TASK_BEE");
    using var server = Process.Start(info)!;
    server.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
    var call = new System.Text.Json.Nodes.JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/call", ["params"] = new System.Text.Json.Nodes.JsonObject { ["name"] = tool, ["arguments"] = arguments } };
    server.StandardInput.WriteLine(call.ToJsonString());
    server.StandardInput.Close();
    string? line, reply = null;
    while ((line = server.StandardOutput.ReadLine()) != null)
        if (System.Text.Json.Nodes.JsonNode.Parse(line)?["id"]?.GetValue<int>() == 2) reply = line;
    server.WaitForExit(5000);
    Pre(reply != null, "a reply to tools/call");
    return reply!;
}

Check("bee_reward over MCP: in Ash's task session (HIVE_TASK_BEE), by \"queen\" is still refused", () =>
{
    using var s = NewHive();
    var t = DoneTask(s, "Birch");
    var reply = McpCall(s, "Ash", "bee_reward", new() { ["id"] = "task:" + t.Id, ["outcome"] = "miss", ["evidence"] = "it missed the bug (task:x)", ["by"] = "queen" });
    True(Tasks.Load(s, t.Id).Doc.Get("reward") == "", "no reward; the reply: " + reply);
    True(reply.Contains("\"isError\":true"), "the reply is an error: " + reply);
});

Check("bee_reward over MCP: control: outside a task session, by \"queen\" gives the miss", () =>
{
    using var s = NewHive();
    var t = DoneTask(s, "Birch");
    var reply = McpCall(s, null, "bee_reward", new() { ["id"] = "task:" + t.Id, ["outcome"] = "miss", ["evidence"] = "it missed the bug (task:x)", ["by"] = "queen" });
    True(Tasks.Load(s, t.Id).Doc.Get("reward") == "no" && Tasks.Load(s, t.Id).Doc.Get("reward_by") == "queen", "the Queen's miss; the reply: " + reply);
});

// ---- who gave it, in what bees and the Sage read ----

Check("honey: old honey without reward_by reads as the user's in bee_review and the bee's brief; misses are listed and not counted as honey", () =>
{
    using var s = NewHive();
    var old = DoneTask(s, "Birch");
    old.Doc.Set("reward", "yes");
    old.Doc.Set("rewarded", "2026-10-18 12:00");
    old.Doc.Set("reward_note", "sharp find");
    old.Doc.Save(old.Path);
    var missed = DoneTask(s, "Birch");
    s.Award("task:" + missed.Id, "miss", "none of its findings held (task:x)", "queen");
    var review = s.Review("Birch");
    True(review.Contains("Honey earned: 1 · misses: 1"), "Honey earned: 1 · misses: 1; it says " + review.Split('\n').FirstOrDefault(l => l.StartsWith("Honey earned")));
    True(review.Contains($"[task:{old.Id}]") && review.Split('\n').Any(l => l.Contains($"[task:{old.Id}]") && l.Contains("the user") && l.Contains("sharp find")), "the old honey as the user's, with its note");
    True(review.Split('\n').Any(l => l.Contains($"[task:{missed.Id}]") && l.Contains("MISS") && l.Contains("the Queen") && l.Contains("none of its findings held")), "the miss as the Queen's, with its evidence");
    var brief = s.Brief(null, "Birch", null);
    var best = brief.Split('\n').FirstOrDefault(l => l.Contains($"[task:{old.Id}]")) ?? "";
    True(best.Contains("the user"), "the brief's best work names the user for the old honey; line: " + best);
    True(!brief.Contains($"[task:{missed.Id}]") || !brief.Contains("## Your best work\n") || brief.IndexOf($"[task:{missed.Id}]") < brief.IndexOf("## Your best work"), "the miss isn't under Your best work");
    True(s.Honey().GetValueOrDefault("birch") == 1, $"Honey() counts 1 for Birch; {s.Honey().GetValueOrDefault("birch")}");
});

Check("honey: taking back (RewardEntry give: false) removes honey or a miss whoever gave it, all four fields", () =>
{
    using var s = NewHive();
    var t = DoneTask(s, "Birch");
    foreach (var (by, miss) in new[] { ("user", false), ("queen", true), ("hive", false), ("hive", true) })
    {
        Tasks.Reward(s, t.Id, "a note", true, by, miss);
        s.RewardEntry("task:" + t.Id, "", false);
        var d = Tasks.Load(s, t.Id).Doc;
        True(d.Get("reward") == "" && d.Get("reward_by") == "" && d.Get("rewarded") == "" && d.Get("reward_note") == "", $"nothing left after taking back {(miss ? "a miss" : "honey")} by {by}; {RewardFields(d)}");
    }
});

// ---- stage 3: the Sage runs by itself; small rules apply at once ----
// After any honey or miss, once a bee has 5 outcomes newer than its last
// Sage review, the hive starts one (a task; here `run-task` is a no-op, so
// no claude starts; RunWithStubClaude runs one with a stand-in). A molt that
// only appends rules citing 2+ of the bee's own outcomes applies at once.
// Through Store.Award, Tasks.Reward, Reviews.Award, Store.Molt, ReopenMolt,
// AutoApplied and (for the minute boundary) Store.AutoEvolve, by name.

static List<BeeTask> Reviews_Of(Store s, string bee) => Tasks.List(s, "Sage").Where(t => Tasks.IsReviewOf(t, bee)).ToList();

// n done tasks of bee, each given the Queen's honey (odd ones a miss); their ids.
static List<string> Judged(Store s, string bee, int n)
{
    var ids = new List<string>();
    for (int i = 0; i < n; i++)
    {
        var t = DoneTask(s, bee);
        s.Award("task:" + t.Id, i % 2 == 0 ? "honey" : "miss", $"outcome {i} (task:x)", "queen");
        ids.Add(t.Id);
    }
    return ids;
}

Check("evolve: 4 outcomes start no review; the 5th starts exactly one Sage task, marked as the hive's, for that bee", () =>
{
    using var s = NewHive();
    s.CreateBee("Sage", "Reviews other bees", [], "", "approve", role: "evolver");
    Judged(s, "Mason", 4);
    True(Reviews_Of(s, "Mason").Count == 0, $"no review after 4; {Reviews_Of(s, "Mason").Count}");
    Judged(s, "Mason", 1);
    var reviews = Reviews_Of(s, "Mason");
    True(reviews.Count == 1, $"one review after the 5th; {reviews.Count}");
    var r = reviews[0];
    True(r.Doc.Get("reviews") == "Mason" && r.Doc.Get("started_by") == "hive" && r.Doc.Get("access") == "read" && r.Prompt.StartsWith("Review the bee Mason.") && r.Prompt.Contains("The hive started this review"),
         $"reviews: Mason, started_by: hive, read access, the prompt says the hive started it; {r.Doc.Get("reviews")}, {r.Doc.Get("started_by")}, {r.Doc.Get("access")}");
    True(Tasks.List(s, "Sage").Count == 1, "no other Sage task");
});

Check("evolve: more outcomes while the review is still to run (queued or running) start no second one", () =>
{
    using var s = NewHive();
    s.CreateBee("Sage", "Reviews other bees", [], "", "approve", role: "evolver");
    Judged(s, "Mason", 7);
    True(Reviews_Of(s, "Mason").Count == 1, $"one review after 7 outcomes; {Reviews_Of(s, "Mason").Count}");
    var r = Reviews_Of(s, "Mason")[0];
    using var runner = Runner(r.Id);
    try
    {
        r.Doc.Set("status", "running");
        r.Doc.Set("pid", runner.Id.ToString());
        r.Doc.Save(r.Path);
        Judged(s, "Mason", 5);
        True(Reviews_Of(s, "Mason").Count == 1, $"still one while it runs, after 5 more; {Reviews_Of(s, "Mason").Count}");
    }
    finally { runner.Kill(entireProcessTree: true); }
});

Check("evolve: outcomes of different bees count apart; the 5th of each starts that bee's own review", () =>
{
    using var s = NewHive();
    s.CreateBee("Sage", "Reviews other bees", [], "", "approve", role: "evolver");
    Judged(s, "Mason", 3);
    Judged(s, "Smith", 4);
    True(Reviews_Of(s, "Mason").Count == 0 && Reviews_Of(s, "Smith").Count == 0, "none at 3 and 4");
    Judged(s, "Smith", 1);
    True(Reviews_Of(s, "Mason").Count == 0 && Reviews_Of(s, "Smith").Count == 1, $"Smith's review only; Mason {Reviews_Of(s, "Mason").Count}, Smith {Reviews_Of(s, "Smith").Count}");
});

Check("evolve: never for the Sage itself, and nothing (no error) in a hive without an Sage", () =>
{
    using var s = NewHive();
    Judged(s, "Mason", 6);
    True(Tasks.List(s).All(t => t.Bee != "Sage"), "no Sage task without an Sage bee");
    s.CreateBee("Sage", "Reviews other bees", [], "", "approve", role: "evolver");
    var tasks = Enumerable.Range(0, 6).Select(_ => { var t = Tasks.Create(s, "Sage", "Review the bee Nobody.", "", "read"); Tasks.Record(s, t, "Nothing to change.", "done"); return t; }).ToList();
    foreach (var t in tasks) s.Award("task:" + t.Id, "honey", "it proposed nothing on thin evidence (task:x)", "queen");
    True(Reviews_Of(s, "Sage").Count == 0, $"no review of the Sage after 6 outcomes; {Reviews_Of(s, "Sage").Count}");
});

Check("evolve: the user's honey, the hive's review awards and entries all count toward the 5", () =>
{
    using var s = NewHive();
    s.CreateBee("Sage", "Reviews other bees", [], "", "approve", role: "evolver");
    Reviews.SetUp(s, new[] { "Cedar", "Birch", "Ash", "Arbiter" });
    var t1 = DoneTask(s, "Ash");
    Tasks.Reward(s, t1.Id, "", true); // the user's, from the window or `hive reward`
    var e = s.Deposit("Ash", "lesson", "A lesson", "A lesson", "body", "", [], []);
    s.RewardEntry(e, "", true, "user", miss: true); // the user's miss on an entry
    s.Award("task:" + DoneTask(s, "Ash").Id, "honey", "evidence (task:x)", "queen");
    var (run, _) = ReviewDone(s, "review-20261021-120000", Table("| Ash | 2 | 0 | x |", "| Birch | 1 | 0 | y |", "| Cedar | 1 | 0 | z |"));
    Pre(Reviews_Of(s, "Ash").Count == 0, "no review of Ash at 3 outcomes");
    var t4 = DoneTask(s, "Ash");
    Tasks.Reward(s, t4.Id, "", true);
    Pre(Reviews_Of(s, "Ash").Count == 0, "none at 4");
    Reviews.Award(s, Reviews.Find(s, run)); // the hive's honey for Ash is the 5th
    True(Reviews_Of(s, "Ash").Count == 1, $"the hive's award started Ash's review; {Reviews_Of(s, "Ash").Count}");
});

Check("evolve: taking honey back starts nothing", () =>
{
    using var s = NewHive();
    s.CreateBee("Sage", "Reviews other bees", [], "", "approve", role: "evolver");
    var ids = Judged(s, "Mason", 4);
    foreach (var id in ids) s.RewardEntry("task:" + id, "", false);
    True(Reviews_Of(s, "Mason").Count == 0, "no review from taking back");
});

// The minute boundary: rewarded and created are "yyyy-MM-dd HH:mm".
foreach (var (what, reviewed, rewarded, starts) in new[]
{
    ("5 outcomes in the same minute as the last review start nothing", "2026-10-21 10:00", "2026-10-21 10:00", false),
    ("control: 5 outcomes a minute after the last review start one", "2026-10-21 10:00", "2026-10-21 10:01", true),
    ("5 outcomes before the last review start nothing", "2026-10-21 10:00", "2026-10-21 09:59", false),
    ("an old review without reviews: (by its prompt) counts as the last one", "2026-10-21 10:00", "2026-10-21 10:00", false),
})
    Check("evolve: " + what, () =>
    {
        using var s = NewHive();
        s.CreateBee("Sage", "Reviews other bees", [], "", "approve", role: "evolver");
        var review = Tasks.Create(s, "Sage", "Review the bee Mason. Call bee_review for it.", "", "read");
        if (!what.Contains("old review")) review.Doc.Set("reviews", "Mason");
        review.Doc.Set("created", reviewed);
        review.Doc.Save(review.Path);
        Tasks.Record(s, Tasks.Load(s, review.Id), "Nothing yet.", "done");
        for (int i = 0; i < 5; i++)
        {
            var t = DoneTask(s, "Mason");
            Store.SetReward(t.Doc, $"outcome {i}", true, "queen", miss: false);
            t.Doc.Set("rewarded", rewarded);
            t.Doc.Save(t.Path);
        }
        var started = s.AutoEvolve("Mason");
        True((started != null) == starts && Reviews_Of(s, "Mason").Count == (starts ? 2 : 1), $"{(starts ? "one new review" : "no new review")}; {Reviews_Of(s, "Mason").Count - 1} new");
    });

// Two processes giving honey at the same moment. Each is this program as
// `hive mcp`; its `run-task` children log what they start (see the top).
foreach (var together in new[] { true, false })
Check(together ? "evolve: two processes awarding the 5th and 6th at the same moment start one review, not two (10 tries)"
               : "evolve: control: the same two processes one after the other start one review (10 tries)", () =>
{
    var twice = new List<string>();
    for (int trial = 0; trial < 10; trial++)
    {
        using var s = NewHive();
        s.CreateBee("Sage", "Reviews other bees", [], "", "approve", role: "evolver");
        Judged(s, "Mason", 4);
        var a = DoneTask(s, "Mason");
        var b = DoneTask(s, "Mason");
        var log = Path.Combine(s.Root, "run-task.log");
        var servers = new[] { a, b }.Select(t =>
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!, ["mcp"]) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
            info.Environment["HIVE_HOME"] = s.Root;
            info.Environment["HIVE_TEST_RUNTASK_LOG"] = log;
            info.Environment.Remove("HIVE_TASK_BEE");
            var p = Process.Start(info)!;
            p.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
            p.StandardInput.Flush();
            p.StandardOutput.ReadLine(); // ready
            return (p, t);
        }).ToList();
        foreach (var (p, t) in servers) // as close together as two writes go (or, for the control, each after the last one's reply)
        {
            p.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"bee_reward\",\"arguments\":{\"id\":\"task:" + t.Id + "\",\"outcome\":\"honey\",\"evidence\":\"tests green (task:x)\",\"by\":\"queen\"}}}");
            p.StandardInput.Flush();
            if (!together) p.StandardOutput.ReadLine();
        }
        foreach (var (p, _) in servers) { p.StandardInput.Close(); p.StandardOutput.ReadToEnd(); p.WaitForExit(10000); p.Dispose(); }
        Pre(Tasks.Load(s, a.Id).Rewarded && Tasks.Load(s, b.Id).Rewarded, "both awards given");
        var sw = Stopwatch.StartNew();
        int Started() => File.Exists(log) ? File.ReadAllLines(log).Count(l => l.Contains("-sage")) : 0;
        while (sw.ElapsedMilliseconds < 3000 && Started() < 2) Thread.Sleep(50);
        var files = Reviews_Of(s, "Mason").Count;
        if (Started() > 1 || files > 1) twice.Add($"try {trial + 1}: {files} Sage task files, {Started()} runners started ({string.Join(", ", File.Exists(log) ? File.ReadAllLines(log) : [])})");
        else Pre(files == 1, $"try {trial + 1}: one review started at all; {files}");
    }
    True(twice.Count == 0, $"never two; {twice.Count} of 10 tries started two: " + string.Join("; ", twice));
});

Check("evolve: the review the hive starts runs as the Sage with bee_review, read only (a stub claude)", () =>
{
    using var s = NewHive();
    s.CreateBee("Sage", "Reviews other bees", [], "", "approve", role: "evolver");
    Judged(s, "Mason", 5);
    var r = Reviews_Of(s, "Mason").Single();
    var args = Tasks.Args(r);
    True(args.Contains("--agent") && FlagValue(args, "--permission-mode") == "dontAsk" && (FlagValue(args, "--allowedTools") ?? "").Contains("mcp__hive__bee_review") && !args.Contains("--dangerously-skip-permissions"),
         "--agent <Sage>, dontAsk, bee_review allowed; args: " + string.Join(' ', args));
    RunWithStubClaude(s, r, "Nothing to change yet: the evidence is thin.");
    True(Tasks.Load(s, r.Id).Status == "done" && Tasks.Load(s, r.Id).Result.Contains("Nothing to change yet"), "the review ran (stub) and is done; " + Tasks.Load(s, r.Id).Status);
});

// ---- small rules ----

const string BuilderCard = "# Mason\n\n## How I work\n\n1. Build in the style around it.\n2. Never fix bugs.\n\n## Rules I've learned\n\n- Old rule one.\n- Old rule two.\n";

// A hive with Mason (that card), Birch, an Sage, Mason's 3 judged tasks
// (honey, miss, honey) and an entry of Mason's with honey, and 2 of Birch's.
static (Store S, List<string> Tasks, string Entry, List<string> Probers) RuleHive(Func<Store> make, string card = BuilderCard)
{
    var s = make();
    s.CreateBee("Sage", "Reviews other bees", [], "", "approve", role: "evolver");
    s.CreateBee("Mason", "Builds C# code", [], "", "approve");
    s.EditCard("Mason", card);
    s.CreateBee("Birch", "Probes", [], "", "approve");
    var tasks = Judged(s, "Mason", 3);
    var entry = s.Deposit("Mason", "worklog", "Stage 1 done", "Stage 1 built", "body", "", [], []);
    s.Award(entry, "honey", "the user committed it (commit abc)", "queen");
    return (s, tasks, entry, Judged(s, "Birch", 2));
}

static string CardOf(Store s, string bee) => s.RequireBee(bee).Doc.Body.Trim();
static bool Waits(Store s, string bee) => s.PendingMolts().Any(m => m.Doc.Get("bee") == bee);

foreach (var (what, make) in new (string, Func<List<string>, string, List<string>, string>)[]
{
    ("one appended rule citing two task outcomes", (t, e, p) => BuilderCard + $"- Run the suite before handing over ([task:{t[0]}], [task:{t[1]}]).\n"),
    ("two appended rules, each citing two", (t, e, p) => BuilderCard + $"- Rule A (task:{t[0]}, task:{t[1]}).\n- Rule B (task:{t[1]}; task:{t[2]}).\n"),
    ("bare task ids", (t, e, p) => BuilderCard + $"- Rule from {t[0]} and {t[2]}.\n"),
    ("an entry id and a task id", (t, e, p) => BuilderCard + $"- Rule ([{e}], `task:{t[1]}`).\n"),
    ("a blank line before the new rule", (t, e, p) => BuilderCard + $"\n- Rule ([task:{t[0]}] [task:{t[1]}]).\n"),
    ("the card with its own front matter, same specialty", (t, e, p) => "---\nspecialty: Builds C# code\n---\n" + BuilderCard + $"- Rule ([task:{t[0]}] [task:{t[1]}]).\n"),
})
    Check("small rule: applies at once: " + what, () =>
    {
        var (s, t, e, p) = RuleHive(NewHive);
        using var _ = s;
        var card = make(t, e, p);
        var said = s.Molt("Mason", "a rule from the outcomes", card, "Sage");
        True(!Waits(s, "Mason"), "nothing waiting; it said: " + said);
        True(CardOf(s, "Mason") == SplitBody(card), "the card is the new one; it is:\n" + CardOf(s, "Mason"));
        var applied = s.AutoApplied();
        True(applied.Count == 1 && applied[0].Doc.Get("bee") == "Mason" && applied[0].Doc.Get("by") == "Sage", $"one auto-applied record by Sage; {applied.Count}");
    });

// The card body a molt sets (front matter dropped).
static string SplitBody(string card) => card.StartsWith("---") ? Doc.Parse(card).Body.Trim() : card.Trim();

foreach (var (what, make) in new (string, Func<List<string>, string, List<string>, string>)[]
{
    ("an old line edited too", (t, e, p) => BuilderCard.Replace("Old rule one.", "Old rule one, sharper.") + $"- Rule ([task:{t[0]}] [task:{t[1]}]).\n"),
    ("an old line deleted and two added", (t, e, p) => BuilderCard.Replace("- Old rule two.\n", "") + $"- Rule A ([task:{t[0]}] [task:{t[1]}]).\n- Rule B ([task:{t[1]}] [task:{t[2]}]).\n"),
    ("a rule inserted between the old ones", (t, e, p) => BuilderCard.Replace("- Old rule two.", $"- New ([task:{t[0]}] [task:{t[1]}]).\n- Old rule two.")),
    ("a line added under How I work", (t, e, p) => BuilderCard.Replace("2. Never fix bugs.", $"2. Never fix bugs.\n3. New step ([task:{t[0]}] [task:{t[1]}]).")),
    ("one id only", (t, e, p) => BuilderCard + $"- Rule ([task:{t[0]}]).\n"),
    ("the same id twice", (t, e, p) => BuilderCard + $"- Rule ([task:{t[0]}], task:{t[0]}, {t[0]}).\n"),
    ("another bee's outcome ids", (t, e, p) => BuilderCard + $"- Rule ([task:{p[0]}] [task:{p[1]}]).\n"),
    ("one own id and one of another bee's", (t, e, p) => BuilderCard + $"- Rule ([task:{t[0]}] [task:{p[0]}]).\n"),
    ("ids of Mason's tasks that have no honey or miss", (t, e, p) => BuilderCard + "- Rule ([task:20260101-000000-mason] [task:20260101-000001-mason]).\n"),
    ("two good rules and one with one id", (t, e, p) => BuilderCard + $"- A ([task:{t[0]}] [task:{t[1]}]).\n- B ([task:{t[2]}]).\n"),
    ("a changed specialty", (t, e, p) => "---\nspecialty: Builds and fixes C# code\n---\n" + BuilderCard + $"- Rule ([task:{t[0]}] [task:{t[1]}]).\n"),
    ("changed projects", (t, e, p) => "---\nprojects: [alpha]\n---\n" + BuilderCard + $"- Rule ([task:{t[0]}] [task:{t[1]}]).\n"),
    ("only blank lines added", (t, e, p) => BuilderCard + "\n\n"),
    ("a new heading with ids", (t, e, p) => BuilderCard + $"## Notes ([task:{t[0]}] [task:{t[1]}])\n"),
})
    Check("small rule: waits for the user: " + what, () =>
    {
        var (s, t, e, p) = RuleHive(NewHive);
        using var _ = s;
        if (what == "changed projects") s.SetProject("alpha", "Alpha", null, null);
        var before = CardOf(s, "Mason");
        s.Molt("Mason", "a change", make(t, e, p), "Sage");
        True(Waits(s, "Mason") && CardOf(s, "Mason") == before && s.AutoApplied().Count == 0, $"waiting, card unchanged; waiting {Waits(s, "Mason")}, card changed {CardOf(s, "Mason") != before}, auto {s.AutoApplied().Count}");
    });

const string CardWithNotes = "# Mason\n\n## Rules I've learned\n\n- Old rule one.\n\n## Notes\n\nSome notes.\n";

Check("small rule: with a section after the rules, a rule at the end of the rules applies; one at the end of the card waits", () =>
{
    var (s, t, _, _) = RuleHive(NewHive, CardWithNotes);
    using var _s = s;
    s.Molt("Mason", "end of card", CardWithNotes + $"- Rule ([task:{t[0]}] [task:{t[1]}]).\n", "Sage");
    True(Waits(s, "Mason") && CardOf(s, "Mason") == CardWithNotes.Trim(), "the one at the end of the card waits");
    var good = CardWithNotes.Replace("- Old rule one.\n", $"- Old rule one.\n- Rule ([task:{t[0]}] [task:{t[1]}]).\n");
    s.Molt("Mason", "end of rules", good, "Sage");
    True(CardOf(s, "Mason") == good.Trim(), "the one at the end of the rules applied; card:\n" + CardOf(s, "Mason"));
});

Check("small rule: a card without \"## Rules I've learned\" gets no rule at once; with ’ in the heading it does", () =>
{
    const string none = "# Mason\n\n## How I work\n\n1. Build.\n";
    var (s, t, _, _) = RuleHive(NewHive, none);
    using var _s = s;
    s.Molt("Mason", "new section", none + $"\n## Rules I've learned\n\n- Rule ([task:{t[0]}] [task:{t[1]}]).\n", "Sage");
    True(Waits(s, "Mason") && CardOf(s, "Mason") == none.Trim(), "a new rules section waits");
    var curly = BuilderCard.Replace("I've", "I’ve");
    var (s2, t2, _, _) = RuleHive(NewHive, curly);
    using var _s2 = s2;
    s2.Molt("Mason", "a rule", curly + $"- Rule ([task:{t2[0]}] [task:{t2[1]}]).\n", "Sage");
    True(!Waits(s2, "Mason"), "applied under \"Rules I’ve learned\"");
});

Check("small rule: Undo (ReopenMolt) puts the old card back and the change waits, no longer marked applied", () =>
{
    var (s, t, _, _) = RuleHive(NewHive);
    using var _s = s;
    s.Molt("Mason", "a rule", BuilderCard + $"- Rule ([task:{t[0]}] [task:{t[1]}]).\n", "Sage");
    var (id, _) = s.AutoApplied().Single();
    s.ReopenMolt(id);
    True(CardOf(s, "Mason") == BuilderCard.Trim(), "the old card back; card:\n" + CardOf(s, "Mason"));
    var waiting = s.PendingMolts().SingleOrDefault(m => m.Id == id);
    True(waiting.Doc != null && waiting.Doc.Get("applied") == "" && s.AutoApplied().Count == 0, "the change waits, not marked applied, and no longer listed as applied");
});

// Stage 5: a small rule applies at once only when the bee with the evolver
// role proposes it for another bee. The Queen's, a bee's own and the
// evolver's for its own card wait for the user.
foreach (var (who, by) in new[] { ("the Queen", "queen"), ("the bee itself (molt: approve)", "Mason"), ("another bee without the evolver role", "Birch") })
    Check($"small rule: proposed by {who}, a small rule waits for the user", () =>
    {
        var (s, t, _, _) = RuleHive(NewHive);
        using var _s = s;
        s.Molt("Mason", "a rule", BuilderCard + $"- Rule ([task:{t[0]}] [task:{t[1]}]).\n", by);
        True(Waits(s, "Mason") && CardOf(s, "Mason") == BuilderCard.Trim() && s.AutoApplied().Count == 0, $"waiting, the card unchanged; waiting {Waits(s, "Mason")}, auto {s.AutoApplied().Count}");
    });

Check("small rule: control: the evolver's small rule for another bee applies, by its name in any case", () =>
{
    var (s, t, _, _) = RuleHive(NewHive);
    using var _s = s;
    s.Molt("Mason", "a rule", BuilderCard + $"- Rule ([task:{t[0]}] [task:{t[1]}]).\n", "sage");
    True(!Waits(s, "Mason") && s.AutoApplied().Single().Doc.Get("by") == "Sage", "applied at once, recorded as by Sage");
});

Check("small rule: the evolver's small rule for its own card waits", () =>
{
    var (s, _, _, _) = RuleHive(NewHive);
    using var _s = s;
    s.EditCard("Sage", BuilderCard.Replace("# Mason", "# Sage"));
    var own = Judged(s, "Sage", 2);
    var card = BuilderCard.Replace("# Mason", "# Sage") + $"- Rule ([task:{own[0]}] [task:{own[1]}]).\n";
    s.Molt("Sage", "my own rule", card, "Sage");
    True(Waits(s, "Sage") && s.AutoApplied().Count == 0, "Sage's own rule waits");
});

Check("small rule: once its evolver role is taken away, a bee's small rule waits", () =>
{
    var (s, t, _, _) = RuleHive(NewHive);
    using var _s = s;
    s.UpdateBee("Sage", null, null, null, role: "none");
    s.Molt("Mason", "a rule", BuilderCard + $"- Rule ([task:{t[0]}] [task:{t[1]}]).\n", "Sage");
    True(Waits(s, "Mason") && s.AutoApplied().Count == 0, "waits: Sage isn't the evolver any more");
});

Check("small rule: a new bee's first rule in place of \"None yet.\" applies at once when the evolver proposes it (stage 5)", () =>
{
    using var s = NewHive();
    s.CreateBee("Sage", "Reviews other bees", [], "", "approve", role: "evolver");
    s.CreateBee("Mason", "Builds C# code", [], "", "approve");
    var t = Judged(s, "Mason", 2);
    var card = CardOf(s, "Mason");
    Pre(card.EndsWith("## Rules I've learned\n\nNone yet."), "a new card ends with None yet.");
    s.Molt("Mason", "first rule", card.Replace("None yet.", $"- Rule ([task:{t[0]}] [task:{t[1]}])."), "Sage");
    True(!Waits(s, "Mason") && CardOf(s, "Mason").EndsWith($"## Rules I've learned\n\n- Rule ([task:{t[0]}] [task:{t[1]}])."), "applied; card:\n" + CardOf(s, "Mason"));
});

// ---- stage 6: roles and settings instead of preset bees ----
// Roles (reviewer, overseer, evolver, keeper) in a card's front matter, the
// settings evolve_after and deposit_before_stop, and team setup by names.
// Through UpdateBee, SetSetting, RoleBee, Reviews.SetUp, Tasks.Args,
// AutoEvolve, Molt, Hooks.Handle and Install by name. All bees are made up.

static string BeeRole(Store s, string bee) => s.RequireBee(bee).Doc.Get("role");

Check("roles: a role is set, cleaned up (case, spaces) and taken away; an unknown one is refused and changes nothing", () =>
{
    using var s = NewHive();
    s.CreateBee("Quill", "Writes", [], "", "approve");
    s.UpdateBee("Quill", null, null, null, role: " Keeper ");
    True(BeeRole(s, "Quill") == "keeper", "keeper; got " + BeeRole(s, "Quill"));
    True(Throws(() => s.UpdateBee("Quill", null, null, null, role: "boss")) && BeeRole(s, "Quill") == "keeper", "boss refused, still keeper");
    s.UpdateBee("Quill", null, null, null, role: "none");
    True(BeeRole(s, "Quill") == "", "none takes it away");
    s.UpdateBee("Quill", null, null, null, role: "evolver");
    s.UpdateBee("Quill", null, null, null, role: "");
    True(BeeRole(s, "Quill") == "", "empty takes it away");
    True(Throws(() => s.CreateBee("Fern", "Grows", [], "", "approve", role: "gardener")) && s.FindBee("Fern") == null && !File.Exists(Path.Combine(s.Root, "bees", "Fern.md")),
         "a new bee with an unknown role refused, and no card left behind");
});

Check("roles: RoleBee is nobody without the role, the one bee with it, and with two the first (observed)", () =>
{
    using var s = NewHive();
    True(s.RoleBee("evolver") == null && s.RoleBee("keeper") == null, "nobody in a fresh hive");
    s.CreateBee("Quill", "Writes", [], "", "approve");
    s.CreateBee("Zinnia", "Reviews records", [], "", "approve", role: "evolver");
    True(s.RoleBee("evolver")?.Name == "Zinnia", "Zinnia; got " + s.RoleBee("evolver")?.Name);
    s.CreateBee("Alder", "Also reviews records", [], "", "approve", role: "evolver");
    var first = s.RoleBee("evolver")!.Name;
    var other = first == "Alder" ? "Zinnia" : "Alder";
    var review = Tasks.Evolve(s, "Quill");
    True(review.Bee == first, $"the review runs as {first} (RoleBee); it runs as {review.Bee}");
    var t = Judged(s, "Quill", 2);
    var card = s.RequireBee("Quill").Doc.Body.Trim().Replace("None yet.", $"- Rule ([task:{t[0]}] [task:{t[1]}]).");
    s.Molt("Quill", "a rule", card, other);
    Console.WriteLine($"      (observed: with two evolvers, {first} runs the reviews (RoleBee), {other}'s small rule {(Waits(s, "Quill") ? "waits" : "applies at once too (Molt checks the proposer's own role)")}; giving {other} the role said nothing)");
});

Check("roles: a read task's tools by role: evolver gets bee_review, keeper hive_tidy and project_set, the rest neither", () =>
{
    using var s = NewHive();
    var extra = new[] { "mcp__hive__bee_review", "mcp__hive__hive_tidy", "mcp__hive__project_set" };
    var want = new Dictionary<string, string[]>
    {
        [""] = [], ["reviewer"] = [], ["overseer"] = [],
        ["evolver"] = ["mcp__hive__bee_review"], ["keeper"] = ["mcp__hive__hive_tidy", "mcp__hive__project_set"],
    };
    var wrong = new List<string>();
    foreach (var (role, tools) in want)
    {
        var name = "Bee" + (role.Length > 0 ? role : "plain");
        s.CreateBee(name, "Does things", [], "", "approve", role: role);
        var allowed = (FlagValue(Tasks.Args(Tasks.Create(s, name, "Look into it", "", "read")), "--allowedTools") ?? "").Split(',');
        var got = extra.Where(allowed.Contains).ToArray();
        if (!got.SequenceEqual(tools)) wrong.Add($"{(role.Length > 0 ? role : "no role")}: {string.Join(" ", got)}");
    }
    True(wrong.Count == 0, "the extra hive tools as wanted; " + string.Join("; ", wrong));
});

Check("roles: an edit or all task gets the same tools whatever the bee's role", () =>
{
    using var s = NewHive();
    foreach (var access in new[] { "edit", "all" })
    {
        var lists = new List<string>();
        foreach (var role in new[] { "", "reviewer", "overseer", "evolver", "keeper" })
        {
            var name = $"B{access}{role}";
            s.CreateBee(name, "Does things", [], "", "approve", role: role);
            var args = Tasks.Args(Tasks.Create(s, name, "Look into it", "", access));
            lists.Add(access == "all" ? string.Join(' ', args.SkipWhile(a => a != "--dangerously-skip-permissions")) : FlagValue(args, "--allowedTools") ?? "");
        }
        True(lists.Distinct().Count() == 1, $"{access}: one tool list for every role; {lists.Distinct().Count()} different");
    }
});

Check("roles: a task keeps the tools of the role its bee had when it was made (taken away after, queued keeper task: observed)", () =>
{
    using var s = NewHive();
    s.CreateBee("Warden", "Tidies", [], "", "approve", role: "keeper");
    var t = Tasks.Create(s, "Warden", "Tidy up", "", "read");
    s.UpdateBee("Warden", null, null, null, role: "none");
    var allowed = FlagValue(Tasks.Args(Tasks.Load(s, t.Id)), "--allowedTools") ?? "";
    var fresh = FlagValue(Tasks.Args(Tasks.Create(s, "Warden", "Tidy again", "", "read")), "--allowedTools") ?? "";
    True(!fresh.Contains("hive_tidy"), "a task made after the role went has no hive_tidy");
    Console.WriteLine($"      (observed: the task made while Warden was keeper {(allowed.Contains("hive_tidy") ? "still gets" : "no longer gets")} hive_tidy after the role went)");
});

Check("roles: the evolver role moved to another bee while a review runs: no second review of the same bee starts", () =>
{
    using var s = NewHive();
    s.CreateBee("Sage", "Reviews records", [], "", "approve", role: "evolver");
    s.CreateBee("Alder", "Reviews records too", [], "", "approve");
    Judged(s, "Mason", 5);
    var first = Reviews_Of(s, "Mason");
    Pre(first.Count == 1 && first[0].Bee == "Sage", "Sage's review of Mason after 5 outcomes");
    using var runner = Runner(first[0].Id);
    try
    {
        var r = first[0];
        r.Doc.Set("status", "running");
        r.Doc.Set("pid", runner.Id.ToString());
        r.Doc.Save(r.Path);
        s.UpdateBee("Sage", null, null, null, role: "none");
        s.UpdateBee("Alder", null, null, null, role: "evolver");
        Judged(s, "Mason", 5);
        var reviews = Tasks.List(s).Where(t => Tasks.IsReviewOf(t, "Mason") && (t.Bee == "Sage" || t.Bee == "Alder")).ToList();
        True(reviews.Count == 1, $"still one review of Mason while Sage's runs; {reviews.Count}: {string.Join(", ", reviews.Select(x => x.Bee + " " + x.Status))}");
    }
    finally { runner.Kill(entireProcessTree: true); }
});

// ---- team setup ----

foreach (var (what, names) in new (string, string[])[]
{
    ("no names", []), ("one name", ["Ash"]), ("an empty overseer", ["Ash", ""]), ("a blank reviewer", ["  ", "Arbiter"]),
    ("the same name twice in another case", ["Ash", "ash", "Arbiter"]), ("a bad name", ["Ash", "bad name", "Arbiter"]), ("queen", ["Ash", "Queen"]),
})
    Check($"team setup: refused with {what}, and nothing is made or changed", () =>
    {
        using var s = NewHive();
        s.CreateBee("Ash", "Breaks things", [], "", "approve");
        var before = string.Join("|", s.Bees().Select(b => b.Name + ":" + b.Doc.Get("role")));
        True(Throws(() => Reviews.SetUp(s, names)), "refused");
        True(string.Join("|", s.Bees().Select(b => b.Name + ":" + b.Doc.Get("role"))) == before, $"bees as before ({before}); now {string.Join("|", s.Bees().Select(b => b.Name + ":" + b.Doc.Get("role")))}");
    });

foreach (var (what, role) in new[] { ("a reviewer", "reviewer"), ("an overseer only", "overseer") })
    Check($"team setup: refused when the hive already has {what}, and nothing changes", () =>
    {
        using var s = NewHive();
        s.CreateBee("Elm", "Reviews", [], "", "approve", role: role);
        True(Throws(() => Reviews.SetUp(s, ["Ash", "Birch", "Arbiter"])), "refused");
        True(s.FindBee("Ash") == null && s.FindBee("Arbiter") == null && BeeRole(s, "Elm") == role, "nothing made, Elm's role kept");
    });

Check("team setup: two names make one reviewer and an overseer; new bees get empty cards and the role", () =>
{
    using var s = NewHive();
    var team = Reviews.SetUp(s, ["Ash", "Arbiter"]);
    True(team.Count == 2 && BeeRole(s, "Ash") == "reviewer" && BeeRole(s, "Arbiter") == "overseer", "Ash reviewer, Arbiter overseer");
    True(s.RequireBee("Ash").Doc.Body.Contains("## How I work\n\n## Rules I've learned\n\nNone yet."), "an empty card: " + s.RequireBee("Ash").Doc.Body);
});

Check("team setup: naming the evolver as a reviewer doesn't silently take its evolver role", () =>
{
    using var s = NewHive();
    s.CreateBee("Sage", "Reviews records", [], "", "approve", role: "evolver");
    var refused = Throws(() => Reviews.SetUp(s, ["Sage", "Arbiter"]));
    True(refused || BeeRole(s, "Sage") == "evolver", $"refused, or Sage still the evolver; Sage is now {(BeeRole(s, "Sage") is { Length: > 0 } r ? r : "no role")} and the hive has {(s.RoleBee("evolver")?.Name ?? "no evolver")}");
});

Check("team setup: with one reviewer, its prompt doesn't speak of other reviewers", () =>
{
    using var s = NewHive();
    Reviews.SetUp(s, ["Ash", "Arbiter"]);
    var run = Reviews.Start(s, "", "src/");
    var prompt = run.Reviewers.Single().Prompt;
    True(!prompt.Contains("0 other"), "no \"0 other reviewers\"; it says: " + prompt.Split('\n').FirstOrDefault(l => l.Contains("other")));
});

// ---- evolve_after ----

foreach (var good in new[] { "0", "1", "5", "12", " 3 " })
    Check($"settings: evolve_after \"{good}\" is kept as a number", () =>
    {
        using var s = NewHive();
        s.CreateBee("Sage", "Reviews records", [], "", "approve", role: "evolver");
        s.SetSetting("Sage", "evolve_after", good);
        True(s.RequireBee("Sage").Doc.Get("evolve_after") == good.Trim(), $"evolve_after: {good.Trim()}; got {s.RequireBee("Sage").Doc.Get("evolve_after")}");
    });

foreach (var bad in new[] { "abc", "-1", "2.5", "", "1e3", "99999999999" })
    Check($"settings: evolve_after \"{bad}\" is refused and the old value stays", () =>
    {
        using var s = NewHive();
        s.CreateBee("Sage", "Reviews records", [], "", "approve", role: "evolver");
        s.SetSetting("Sage", "evolve_after", "3");
        True(Throws(() => s.SetSetting("Sage", "evolve_after", bad)) && s.RequireBee("Sage").Doc.Get("evolve_after") == "3", $"refused, still 3; it's {s.RequireBee("Sage").Doc.Get("evolve_after")}");
    });

Check("settings: deposit_before_stop takes yes and no in any case; anything else is refused; an unknown setting too", () =>
{
    using var s = NewHive();
    s.CreateBee("Quill", "Writes", [], "", "approve");
    s.SetSetting("Quill", "deposit_before_stop", "YES");
    True(s.RequireBee("Quill").Doc.Get("deposit_before_stop") == "yes", "yes");
    True(Throws(() => s.SetSetting("Quill", "deposit_before_stop", "maybe")) && s.RequireBee("Quill").Doc.Get("deposit_before_stop") == "yes", "maybe refused, still yes");
    s.SetSetting("Quill", "deposit_before_stop", "no");
    True(s.RequireBee("Quill").Doc.Get("deposit_before_stop") == "", "no removes it");
    True(Throws(() => s.SetSetting("Quill", "molt", "free")), "molt isn't a setting here");
});

// n outcomes for Mason after Sage (the evolver, with evolve_after as given) exists; the reviews of Mason started.
static int ReviewsAfter(string? evolveAfter, int n, bool handEdited = false, string? onMason = null)
{
    var root = Path.Combine(Path.GetTempPath(), $"hive-storetests-{Environment.ProcessId}", "evolve-" + Guid.NewGuid().ToString("N"));
    using var s = new Store(root);
    s.Init();
    var sage = s.CreateBee("Sage", "Reviews records", [], "", "approve", role: "evolver");
    if (evolveAfter != null)
    {
        if (handEdited) { sage.Doc.Set("evolve_after", evolveAfter); sage.Doc.Save(sage.Path); }
        else s.SetSetting("Sage", "evolve_after", evolveAfter);
    }
    s.CreateBee("Mason", "Builds", [], "", "approve");
    if (onMason != null) s.SetSetting("Mason", "evolve_after", onMason);
    Judged(s, "Mason", n);
    return Reviews_Of(s, "Mason").Count;
}

foreach (var (what, after, hand, onMason, counts) in new (string, string?, bool, string?, (int N, int Want)[])[]
{
    ("0 turns it off", "0", false, null, [(7, 0)]),
    ("1 starts one at the first outcome", "1", false, null, [(1, 1)]),
    ("5 (as set): none at 4, one at 5", "5", false, null, [(4, 0), (5, 1)]),
    ("not set: the default 5", null, false, null, [(4, 0), (5, 1)]),
    ("hand-edited \"abc\": the default 5", "abc", true, null, [(4, 0), (5, 1)]),
    ("hand-edited \"-2\": the default 5", "-2", true, null, [(4, 0), (5, 1)]),
    ("on another bee's card it doesn't count: the evolver's default 5", null, false, "1", [(4, 0), (5, 1)]),
})
    Check("evolve_after: " + what, () =>
    {
        foreach (var (n, want) in counts)
        {
            var got = ReviewsAfter(after, n, hand, onMason);
            True(got == want, $"{want} review(s) after {n} outcome(s); {got}");
        }
    });

// ---- deposit_before_stop: the stop check ----

foreach (var (what, setting, role, sentBack) in new[]
{
    ("on: a bee that saved nothing is sent back once", "yes", "", true),
    ("off (not set): it stops", "", "", false),
    ("set to no: it stops", "no", "", false),
    ("on, for a reviewer: it stops (a reviewer saves nothing)", "yes", "reviewer", false),
    ("on, for the overseer: sent back", "yes", "overseer", true),
})
    Check("deposit_before_stop " + what, () =>
    {
        using var s = NewHive();
        s.CreateBee("Quill", "Writes", [], "", "approve", role: role);
        if (setting.Length > 0) s.SetSetting("Quill", "deposit_before_stop", setting);
        Hooks.Handle(s, "subagent-start", new System.Text.Json.Nodes.JsonObject { ["agent_type"] = "quill", ["agent_id"] = "agent-1", ["session_id"] = "p-1", ["cwd"] = "/tmp" });
        var reply = Hooks.Handle(s, "subagent-stop", new System.Text.Json.Nodes.JsonObject { ["agent_id"] = "agent-1" });
        var blocked = reply != null && Json.Str(reply["decision"]) == "block";
        True(blocked == sentBack, sentBack ? "sent back" : "let stop; it was sent back: " + reply?.ToJsonString());
        if (sentBack)
        {
            var again = Hooks.Handle(s, "subagent-stop", new System.Text.Json.Nodes.JsonObject { ["agent_id"] = "agent-1", ["stop_hook_active"] = true });
            True(again == null || Json.Str(again["decision"]) != "block", "only once");
        }
    });

Check("deposit_before_stop: control: on, a bee that saved something stops", () =>
{
    using var s = NewHive();
    s.CreateBee("Quill", "Writes", [], "", "approve");
    s.SetSetting("Quill", "deposit_before_stop", "yes");
    Hooks.Handle(s, "subagent-start", new System.Text.Json.Nodes.JsonObject { ["agent_type"] = "quill", ["agent_id"] = "agent-2", ["session_id"] = "p-1", ["cwd"] = "/tmp" });
    Thread.Sleep(1100);
    s.Deposit("Quill", "worklog", "Did it", "Did the thing", "body", "", [], []);
    var reply = Hooks.Handle(s, "subagent-stop", new System.Text.Json.Nodes.JsonObject { ["agent_id"] = "agent-2" });
    True(reply == null || Json.Str(reply["decision"]) != "block", "let stop: " + reply?.ToJsonString());
});

// ---- install never touches the user's own skills ----

foreach (var (what, existing) in new[] { ("with none there, it makes none", (string?)null), ("an existing one stays byte for byte", "---\nname: my-review\n---\n\nMy own steps.\n"),
                                           ("one with the old install marker stays too", "---\nname: my-review\n---\n\n<!-- written by hive install -->\nOld text.\n") })
    Check("install: a user skill in ~/.claude/skills: " + what, () =>
    {
        using var s = NewHive();
        var dir = Path.GetDirectoryName(skill)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        if (existing != null) { Directory.CreateDirectory(dir); File.WriteAllText(skill, existing); }
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, "{}\n");
        WithEnv(fakeBin + ":" + realPath, Path.Combine(scratch, "data-install2"), () => Install.Run(s, _ => { }, "keep"));
        True(existing == null ? !Directory.Exists(dir) : File.ReadAllText(skill) == existing, existing == null ? "no skill folder made" : "the file as it was");
        WithEnv(fakeBin + ":" + realPath, Path.Combine(scratch, "data-install2"), () => Install.Remove(_ => { }));
        True(existing == null ? !Directory.Exists(dir) : File.ReadAllText(skill) == existing, "and after uninstall too");
    });

// ---- the software's own text says no workflow and names no bee ----
// What the hive sends to Claude (MCP instructions and tools, briefings, the
// subagent files, task, pane, review and evolver prompts, the review dossier,
// the stop check) and the README, gathered from a hive of made-up bees.
// None may name a bee that isn't in the hive, or carry a workflow rule:
// the markers are generic phrases of a working process and invented
// stand-in names, never names from anyone's own hive.

// The prompt Tasks.Run sends for a task (a stub claude saves its stdin).
string PromptOf(Store s, BeeTask task)
{
    var file = Path.Combine(scratch, "stub-bin", task.Id + ".prompt");
    var before = Environment.GetEnvironmentVariable("HIVE_TEST_STUB_PROMPT");
    Environment.SetEnvironmentVariable("HIVE_TEST_STUB_PROMPT", file);
    try { RunWithStubClaude(s, task, "Done."); }
    finally { Environment.SetEnvironmentVariable("HIVE_TEST_STUB_PROMPT", before); }
    return File.Exists(file) ? File.ReadAllText(file) : "";
}

string McpRaw(Store s, string method)
{
    var info = new ProcessStartInfo(Environment.ProcessPath!, ["mcp"]) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
    info.Environment["HIVE_HOME"] = s.Root;
    using var server = Process.Start(info)!;
    server.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
    server.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"" + method + "\"}");
    server.StandardInput.Close();
    var all = server.StandardOutput.ReadToEnd();
    server.WaitForExit(5000);
    return all;
}

List<(string Where, string Text)> SoftwareTexts()
{
    var texts = new List<(string, string)>();
    using var s = NewHive();
    s.SetProject("orchard", "An imaginary project", null, "# orchard\n\nA made-up brief.");
    s.CreateBee("Quill", "Writes small things", [], "", "approve");
    s.CreateBee("Fern", "Reviews records", [], "", "approve", role: "evolver");
    s.CreateBee("Moss", "Tidies", [], "", "approve", role: "keeper");
    s.SetSetting("Quill", "deposit_before_stop", "yes");
    Reviews.SetUp(s, ["Larch", "Rowan", "Hazel"]);
    texts.Add(("Mcp.Instructions", Mcp.Instructions));
    texts.Add(("MCP initialize and tools/list", McpRaw(s, "tools/list")));
    texts.Add(("the main session's briefing", s.Brief(null, null, scratch)));
    texts.Add(("Quill's briefing", s.Brief(null, "Quill", null)));
    foreach (var f in Directory.EnumerateFiles(Environment.GetEnvironmentVariable("HIVE_AGENTS_DIR")!, "*.md"))
        texts.Add(("subagent file " + Path.GetFileName(f), File.ReadAllText(f)));
    texts.Add(("a read task's prompt", PromptOf(s, Tasks.Create(s, "Quill", "Look at the orchard.", "orchard", "read"))));
    texts.Add(("an edit task's prompt", PromptOf(s, Tasks.Create(s, "Quill", "Fix the orchard.", "", "edit"))));
    texts.Add(("a pane turn's prompt", PromptOf(s, Tasks.QueenTalk(s, "Hello there.", "", "edit", null))));
    var evolve = Tasks.Evolve(s, "Quill");
    texts.Add(("an evolver review's prompt", PromptOf(s, evolve)));
    var run = Reviews.Start(s, "", "the orchard");
    texts.Add(("a reviewer's prompt", PromptOf(s, run.Reviewers[0])));
    Tasks.Record(s, Tasks.Load(s, run.Reviewers[1].Id), "Rowan's findings.", "done");
    texts.Add(("the overseer's prompt", PromptOf(s, Tasks.Load(s, run.Overseer!.Id))));
    Thread.Sleep(1100); // another run id
    var begun = Reviews.Begin(s, "", "the orchard again", scratch);
    texts.Add(("team_review_begin", begun));
    var led = Reviews.Runs(s).First(r => r.Reviewers.All(t => t.Doc.Get("led_by") == "session"));
    texts.Add(("team_review_report (reviewers in)", string.Join("\n", led.Reviewers.Select(t => Reviews.Report(s, led.Id, t.Bee, "My findings.")))));
    texts.Add(("team_review_report (overseer)", Reviews.Report(s, led.Id, led.Overseer!.Bee, "# Review\n## Reviewers\n| reviewer | confirmed | rejected | found alone |\n|---|---|---|---|\n| Larch | 1 | 0 | x |\n| Rowan | 0 | 1 | y |\n")));
    texts.Add(("bee_review of Quill", s.Review("Quill")));
    Hooks.Handle(s, "subagent-start", new System.Text.Json.Nodes.JsonObject { ["agent_type"] = "quill", ["agent_id"] = "agent-c", ["session_id"] = "p-1", ["cwd"] = "/tmp" });
    texts.Add(("the stop check", Hooks.Handle(s, "subagent-stop", new System.Text.Json.Nodes.JsonObject { ["agent_id"] = "agent-c" })?.ToJsonString() ?? ""));
    texts.Add(("README.md", File.ReadAllText(Path.Combine(repo, "README.md"))));
    foreach (var flag in new[] { "off", "keep" })
    {
        var said = new List<string>();
        var data = AutoMemoryStart("{}\n", "content-" + flag);
        WithEnv(fakeBin + ":" + realPath, data, () => { Install.Run(s, said.Add, flag); Install.Remove(said.Add); });
        texts.Add(($"install --auto-memory {flag} and uninstall, what they say", string.Join("\n", said)));
    }
    if (Environment.GetEnvironmentVariable("HIVE_TEST_DUMP") is { Length: > 0 } dump)
        File.WriteAllText(dump, string.Join("\n\n", texts.Select(t => $"===== {t.Item1}\n{t.Item2}")));
    return texts;
}

// Generic phrases of a working process (who does what, in which order) and
// invented stand-in names for bees nobody made. Whole words, any case.
string[] workflowMarkers =
[
    "pipeline", "hand it to", "hand work to", "tell the user", "note your assumptions", "save what you found",
    "decide, note", "aim for it", "earned honey from the user", "only the user gives honey",
    "stage snapshot", "the review team does it", "report back to the lead",
    "Tinker", "Plumber", "Scribe",
];

Check("content: the software's own text names no bee outside the hive and carries no workflow rule", () =>
{
    var texts = SoftwareTexts();
    Pre(texts.Count >= 15 && texts.All(t => t.Text.Length > 0), "every text gathered; empty: " + string.Join(", ", texts.Where(t => t.Text.Length == 0).Select(t => t.Where)));
    var hits = new List<string>();
    foreach (var (where, text) in texts)
        foreach (var m in workflowMarkers)
            if (Regex.IsMatch(text, @"(?<![\w-])" + Regex.Escape(m) + @"(?![\w-])", RegexOptions.IgnoreCase)) hits.Add($"{where}: \"{m}\"");
    True(hits.Count == 0, "none of the markers; found " + string.Join("; ", hits));
});

Check("content: control: the marker search finds a marker planted in a text", () =>
{
    var planted = "Briefing.\nWhen in doubt, hand it to the Tinker.";
    var found = workflowMarkers.Where(m => Regex.IsMatch(planted, @"(?<![\w-])" + Regex.Escape(m) + @"(?![\w-])", RegexOptions.IgnoreCase)).ToList();
    True(found.SequenceEqual(["hand it to", "Tinker"]), "hand it to and Tinker; found " + string.Join(", ", found));
});

// ---- stage 7: what an edit task may run is a hive setting ----
// <hive>/settings.md front matter edit_commands: missing means make and
// ./build.sh, [] none; anything with wildcards, quotes, commas or shell
// operators is left out. Each edit task saves the list it was made with, and
// Args allows Bash(<command> *) for each. Through Store.EditCommands,
// Tasks.Create and Tasks.Args by name. All commands here are made up.

// The Bash rules an edit task gets beyond a read task's (and the built-in ps).
static List<string> EditExtras(Store s, BeeTask task)
{
    List<string> Bash(BeeTask t) => (FlagValue(Tasks.Args(t), "--allowedTools") ?? "").Split(',').Where(r => r.StartsWith("Bash(")).ToList();
    var read = Bash(Tasks.Create(s, task.Bee, "Look around", "", "read"));
    return Bash(task).Where(r => !read.Contains(r) && r != "Bash(ps *)").ToList();
}

static Store SettingsHive(Func<Store> make, string? settingsText)
{
    var s = make();
    s.CreateBee("Quill", "Writes", [], "", "approve");
    if (settingsText != null) File.WriteAllText(Path.Combine(s.Root, "settings.md"), settingsText);
    return s;
}

static string BashRules(IEnumerable<string> commands) => string.Join(" ", commands.Select(c => $"Bash({c} *)"));

foreach (var (what, settingsText, want) in new (string, string?, string[])[]
{
    ("no settings.md: make and ./build.sh", null, ["make", "./build.sh"]),
    ("an empty settings.md: the default", "", ["make", "./build.sh"]),
    ("settings.md without front matter: the default", "edit_commands: [cargo test]\n", ["make", "./build.sh"]),
    ("front matter that never closes: the default", "---\nedit_commands: [cargo test]\n", ["make", "./build.sh"]),
    ("front matter without edit_commands: the default", "---\nother: 1\n---\n", ["make", "./build.sh"]),
    ("edit_commands: [] means none", "---\nedit_commands: []\n---\n", []),
    ("a flow list", "---\nedit_commands: [cargo test, dotnet build]\n---\n", ["cargo test", "dotnet build"]),
    ("a block list", "---\nedit_commands:\n  - cargo test\n  - dotnet build\n---\n", ["cargo test", "dotnet build"]),
    ("one command, not a list", "---\nedit_commands: cargo test\n---\n", ["cargo test"]),
    ("spaces inside a command are tidied; a repeat is dropped", "---\nedit_commands: [cargo   test, cargo test,  make ]\n---\n", ["cargo test", "make"]),
    ("allowed characters: letters, digits and ./-_:=+", "---\nedit_commands: [npm run test:unit, X=1 make, ./tools/b-u_i.l+d]\n---\n", ["npm run test:unit", "X=1 make", "./tools/b-u_i.l+d"]),
    ("wildcards and shell operators are left out", "---\nedit_commands: [rm *, make && touch x, echo $(id), a|b, x;y, x > f, `id`, x\\ty, sh -c (x), make]\n---\n", ["make"]),
    ("non-ASCII letters are left out", "---\nedit_commands: [tést, make]\n---\n", ["make"]),
    ("CRLF line ends", "---\r\nedit_commands: [cargo test]\r\n---\r\n", ["cargo test"]),
})
    Check("edit_commands: " + what, () =>
    {
        using var s = SettingsHive(NewHive, settingsText);
        var got = s.EditCommands();
        True(got.SequenceEqual(want), $"EditCommands [{string.Join(", ", want)}]; got [{string.Join(", ", got)}]");
        var task = Tasks.Create(s, "Quill", "Fix it", "", "edit");
        var extras = EditExtras(s, task);
        True(string.Join(" ", extras) == BashRules(want), $"the edit task allows {BashRules(want)}; it allows {string.Join(" ", extras)}");
    });

Check("edit_commands: a comma inside a quoted item doesn't turn its second half into a command of its own", () =>
{
    using var s = SettingsHive(NewHive, "---\nedit_commands: [\"cargo build, rm\", make]\n---\n");
    var got = s.EditCommands();
    True(!got.Contains("rm"), $"no bare \"rm\" allowed; the commands are [{string.Join(", ", got)}]");
});

Check("edit_commands: quoted items (observed)", () =>
{
    using var s = SettingsHive(NewHive, "---\nedit_commands: [\"cargo test\", 'dotnet build', make]\n---\n");
    Console.WriteLine($"      (observed: [\"cargo test\", 'dotnet build', make] gives [{string.Join(", ", s.EditCommands())}])");
});

Check("edit_commands: an unclosed flow list never allows an operator or a wildcard", () =>
{
    using var s = SettingsHive(NewHive, "---\nedit_commands: [make, rm *\n---\n");
    var got = s.EditCommands();
    True(got.All(c => !c.Contains('*') && !c.Contains('[')), $"nothing odd; got [{string.Join(", ", got)}]");
    Console.WriteLine($"      (observed: an unclosed list gives [{string.Join(", ", got)}])");
});

Check("edit_commands: a settings.md that is a folder means the default", () =>
{
    using var s = SettingsHive(NewHive, null);
    Directory.CreateDirectory(Path.Combine(s.Root, "settings.md"));
    True(s.EditCommands().SequenceEqual(Store.DefaultEditCommands), "the default");
});

Check("edit_commands: an unreadable settings.md doesn't crash making an edit task with anything but a HiveException", () =>
{
    using var s = SettingsHive(NewHive, "---\nedit_commands: [cargo test]\n---\n");
    var path = Path.Combine(s.Root, "settings.md");
    File.SetUnixFileMode(path, UnixFileMode.None);
    try
    {
        Pre(!CanRead(path), "settings.md unreadable (not running as root)");
        Exception? threw = null;
        try { Tasks.Create(s, "Quill", "Fix it", "", "edit"); } catch (Exception e) { threw = e; }
        True(threw is null or HiveException, "made, or a HiveException; got " + threw?.GetType().Name + ": " + threw?.Message);
    }
    finally { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
});

Check("edit_commands: an edit task keeps the list it was made with when settings.md changes after", () =>
{
    using var s = SettingsHive(NewHive, "---\nedit_commands: [cargo test]\n---\n");
    var task = Tasks.Create(s, "Quill", "Fix it", "", "edit");
    True(task.Doc.GetList("edit_commands").SequenceEqual(["cargo test"]), "saved on the task: " + task.Doc.Get("edit_commands"));
    File.WriteAllText(Path.Combine(s.Root, "settings.md"), "---\nedit_commands: [dotnet build]\n---\n");
    True(string.Join(" ", EditExtras(s, Tasks.Load(s, task.Id))) == "Bash(cargo test *)", "still cargo test; " + string.Join(" ", EditExtras(s, Tasks.Load(s, task.Id))));
    True(string.Join(" ", EditExtras(s, Tasks.Create(s, "Quill", "Fix more", "", "edit"))) == "Bash(dotnet build *)", "a new task gets dotnet build");
});

Check("edit_commands: an edit task made under [] runs nothing extra, also read back from its file", () =>
{
    using var s = SettingsHive(NewHive, "---\nedit_commands: []\n---\n");
    var task = Tasks.Load(s, Tasks.Create(s, "Quill", "Fix it", "", "edit").Id);
    True(EditExtras(s, task).Count == 0, "no extra commands; " + string.Join(" ", EditExtras(s, task)) + "; the file says edit_commands: \"" + task.Doc.Get("edit_commands") + "\"");
});

Check("edit_commands: an edit task from before the setting (no field) gets the default", () =>
{
    using var s = SettingsHive(NewHive, "---\nedit_commands: [cargo test]\n---\n");
    var task = Tasks.Create(s, "Quill", "Fix it", "", "edit");
    task.Doc.Remove("edit_commands");
    task.Doc.Save(task.Path);
    True(string.Join(" ", EditExtras(s, Tasks.Load(s, task.Id))) == "Bash(make *) Bash(./build.sh *)", "make and ./build.sh; " + string.Join(" ", EditExtras(s, Tasks.Load(s, task.Id))));
});

Check("edit_commands: read and all tasks never get them, and save none", () =>
{
    using var s = SettingsHive(NewHive, "---\nedit_commands: [cargo test, make]\n---\n");
    var read = Tasks.Create(s, "Quill", "Look", "", "read");
    var allowed = FlagValue(Tasks.Args(read), "--allowedTools") ?? "";
    True(!allowed.Contains("cargo test") && !allowed.Contains("Bash(make") && read.Doc.Get("edit_commands") == "", "a read task: neither, and no field; " + allowed);
    var all = Tasks.Create(s, "Quill", "Anything", "", "all");
    True(Tasks.Args(all).Contains("--dangerously-skip-permissions") && all.Doc.Get("edit_commands") == "", "an all task: skip permissions, no field");
});

Check("edit_commands: a task file edited by hand to hold a wildcard or an operator still allows neither", () =>
{
    using var s = SettingsHive(NewHive, null);
    var task = Tasks.Create(s, "Quill", "Fix it", "", "edit");
    task.Doc.SetList("edit_commands", ["make", "rm *", "make *),Bash(*"]);
    task.Doc.Save(task.Path);
    var rules = (FlagValue(Tasks.Args(Tasks.Load(s, task.Id)), "--allowedTools") ?? "").Split(',');
    var bad = rules.Where(r => r.StartsWith("Bash(") && (r.Count(ch => ch == '*') > 1 || r == "Bash(*)" || r.Contains("rm"))).ToList();
    True(bad.Count == 0, "no rule beyond <command> *; got " + string.Join(" ", bad));
});

Check("edit_commands: the edit task's prompt names exactly the commands it may run", () =>
{
    foreach (var (text, says) in new[] { ("---\nedit_commands: [cargo test]\n---\n", "run: cargo test; read-only git"), ("---\nedit_commands: []\n---\n", "run: read-only git") })
    {
        using var s = SettingsHive(NewHive, text);
        var prompt = PromptOf(s, Tasks.Create(s, "Quill", "Fix it", "", "edit"));
        True(prompt.Contains(says) && !prompt.Contains("make;"), $"\"{says}\"; it says: " + prompt.Split('\n').FirstOrDefault(l => l.Contains("run:")));
    }
});

// ---- stage 8: install asks before turning off Claude Code's auto memory ----
// `hive install` sets autoMemoryEnabled: false only on --auto-memory off or a
// "y" on a terminal (no by default), writes a mark at
// $XDG_DATA_HOME/hive/auto-memory-off when it does, and uninstall removes the
// key only with that mark. Everything runs in this run's temp HOME and
// XDG_DATA_HOME (Sandboxed), with a fake claude on PATH.

static string? AutoMemoryKey(string settingsPath) =>
    File.Exists(settingsPath) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settingsPath)) is System.Text.Json.Nodes.JsonObject o && o["autoMemoryEnabled"] is { } v ? v.ToJsonString() : null;

// settings.json without what install adds (hooks, status line), to compare the rest.
static string UserPart(string settingsPath)
{
    if (!File.Exists(settingsPath)) return "{}"; // none: no settings of the user's
    var o = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settingsPath))!;
    o.Remove("hooks");
    o.Remove("statusLine");
    return o.ToJsonString();
}

// A fresh ~/.claude/settings.json (null: none) and a fresh data folder; its path.
string AutoMemoryStart(string? settingsText, string name)
{
    var settingsDir = Path.GetDirectoryName(settings)!;
    if (Directory.Exists(settingsDir)) foreach (var f in Directory.GetFiles(settingsDir, "settings.json*")) File.Delete(f);
    if (settingsText != null) { Directory.CreateDirectory(settingsDir); File.WriteAllText(settings, settingsText); }
    var data = Path.Combine(scratch, "am-" + name);
    if (Directory.Exists(data)) Directory.Delete(data, true);
    Directory.CreateDirectory(data);
    return data;
}

static string Mark(string data) => Path.Combine(data, "hive", "auto-memory-off");

// install with no flag, as `hive install` runs it: on a pty answering typed (null: no terminal at all).
string InstallChild(Store s, string data, string? typed, string? flag = null)
{
    var output = "";
    WithEnv(fakeBin + ":" + realPath, data, () =>
    {
        var self = Environment.ProcessPath!;
        var childArgs = flag == null ? "install-child" : $"install-child {flag}";
        var info = typed == null
            ? new ProcessStartInfo(self, childArgs.Split(' '))
            : new ProcessStartInfo(Text.Which("script"), ["-qec", $"'{self}' {childArgs}", "/dev/null"]);
        info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
        info.UseShellExecute = false;
        info.Environment["HIVE_HOME"] = s.Root;
        using var p = Process.Start(info)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (typed != null)
        {
            Thread.Sleep(1500); // the question is up
            p.StandardInput.Write(typed + "\n");
            p.StandardInput.Flush();
        }
        p.StandardInput.Close();
        if (!p.WaitForExit(30000)) { p.Kill(true); throw new SetupError("install-child timed out; it said: " + stdout.Result); }
        output = stdout.Result + stderr.Result;
    });
    Pre(output.Contains("DONE"), "install ran to the end; it said: " + output);
    return output;
}

foreach (var (typed, off) in new[] { ("y", true), ("yes", true), ("Y", true), ("n", false), ("", false), ("sure", false) })
    Check($"auto memory: on a terminal, answering \"{typed}\" {(off ? "turns it off and leaves the mark" : "leaves it alone")}", () =>
    {
        using var s = NewHive();
        var data = AutoMemoryStart("{ \"theme\": \"dark\" }\n", "tty-" + typed);
        var said = InstallChild(s, data, typed);
        True(said.Contains("[y/N]"), "the question asked, no by default; it said: " + said);
        True(AutoMemoryKey(settings) == (off ? "false" : null), $"autoMemoryEnabled {(off ? "false" : "not set")}; it's {AutoMemoryKey(settings) ?? "not set"}");
        True(File.Exists(Mark(data)) == off, $"the mark {(off ? "written" : "not written")}");
        True(UserPart(settings) == (off ? "{\"theme\":\"dark\",\"autoMemoryEnabled\":false}" : "{\"theme\":\"dark\"}"), "the user's own settings kept; " + UserPart(settings));
    });

foreach (var (what, before) in new[] { ("none", (string?)null), ("no key", "{ \"theme\": \"dark\" }\n"), ("auto memory on", "{ \"autoMemoryEnabled\": true }\n") })
    Check($"auto memory: no terminal and no flag ({what}): nothing asked, the key and the rest of settings.json as they were, no mark", () =>
    {
        using var s = NewHive();
        var data = AutoMemoryStart(before, "noterm-" + what.Replace(' ', '-'));
        var userBefore = UserPart(settings);
        var keyBefore = AutoMemoryKey(settings);
        var said = InstallChild(s, data, null);
        True(!said.Contains("[y/N]") && said.Contains("Left Claude Code's own auto memory as it is"), "not asked, and it says it left it; it said: " + said);
        True(AutoMemoryKey(settings) == keyBefore && UserPart(settings) == userBefore && !File.Exists(Mark(data)), $"as before ({keyBefore ?? "no key"}, {userBefore}); now {AutoMemoryKey(settings) ?? "no key"}, {UserPart(settings)}");
    });

Check("auto memory: control: no terminal but --auto-memory off in the child turns it off", () =>
{
    using var s = NewHive();
    var data = AutoMemoryStart(null, "noterm-off");
    InstallChild(s, data, null, "off");
    True(AutoMemoryKey(settings) == "false" && File.Exists(Mark(data)), "off, with the mark");
});

foreach (var (flag, off) in new[] { ("off", true), ("keep", false) })
    Check($"auto memory: --auto-memory {flag} {(off ? "turns it off with the mark, and uninstall turns it back on" : "leaves it, and uninstall leaves it")}", () =>
    {
        using var s = NewHive();
        var data = AutoMemoryStart("{ \"theme\": \"dark\" }\n", "flag-" + flag);
        var said = new List<string>();
        WithEnv(fakeBin + ":" + realPath, data, () => Install.Run(s, said.Add, flag));
        True(AutoMemoryKey(settings) == (off ? "false" : null) && File.Exists(Mark(data)) == off, $"after install: {AutoMemoryKey(settings) ?? "no key"}, mark {File.Exists(Mark(data))}");
        True(said.Any(l => l.Contains(off ? "Turned off Claude Code's own auto memory" : "Left Claude Code's own auto memory as it is")), "install says which; " + string.Join(" | ", said));
        said.Clear();
        WithEnv(fakeBin + ":" + realPath, data, () => Install.Remove(said.Add));
        True(AutoMemoryKey(settings) == null && !File.Exists(Mark(data)) && UserPart(settings) == "{\"theme\":\"dark\"}", $"after uninstall: {AutoMemoryKey(settings) ?? "no key"}, mark {File.Exists(Mark(data))}, {UserPart(settings)}");
        True(said.Any(l => l.Contains("turned Claude Code's auto memory back on")) == off, "uninstall says it turned it back on only if it did; " + string.Join(" | ", said));
    });

foreach (var bad in new[] { "OFF", "maybe", "", " off", "no" })
    Check($"auto memory: --auto-memory \"{bad}\" is refused before anything changes", () =>
    {
        using var s = NewHive();
        var data = AutoMemoryStart("{ \"theme\": \"dark\" }\n", "bad");
        var before = File.ReadAllText(settings);
        Exception? threw = null;
        WithEnv(fakeBin + ":" + realPath, data, () => { try { Install.Run(s, _ => { }, bad); } catch (Exception e) { threw = e; } });
        True(threw is HiveException, "a HiveException; got " + (threw?.GetType().Name ?? "none"));
        True(File.ReadAllText(settings) == before && !File.Exists(Mark(data)) && !Directory.Exists(Path.Combine(data, "applications")), "settings.json byte for byte, no mark, no launcher");
    });

Check("auto memory: already off by the user: off changes nothing, writes no mark, and uninstall leaves it off", () =>
{
    using var s = NewHive();
    var data = AutoMemoryStart("{ \"autoMemoryEnabled\": false }\n", "already");
    var said = new List<string>();
    WithEnv(fakeBin + ":" + realPath, data, () => Install.Run(s, said.Add, "off"));
    True(AutoMemoryKey(settings) == "false" && !File.Exists(Mark(data)) && said.Any(l => l.Contains("already off")), "still false, no mark, says already off; " + string.Join(" | ", said));
    WithEnv(fakeBin + ":" + realPath, data, () => Install.Remove(_ => { }));
    True(AutoMemoryKey(settings) == "false", "still off after uninstall; " + (AutoMemoryKey(settings) ?? "no key"));
});

Check("auto memory: uninstall without the mark leaves the user's own setting, true or false", () =>
{
    foreach (var value in new[] { "true", "false" })
    {
        var data = AutoMemoryStart($"{{ \"autoMemoryEnabled\": {value} }}\n", "nomark-" + value);
        WithEnv(fakeBin + ":" + realPath, data, () => Install.Remove(_ => { }));
        True(AutoMemoryKey(settings) == value, $"{value} kept; {AutoMemoryKey(settings) ?? "no key"}");
    }
});

Check("auto memory: install off twice, then uninstall: back on, and the mark gone", () =>
{
    using var s = NewHive();
    var data = AutoMemoryStart(null, "twice");
    WithEnv(fakeBin + ":" + realPath, data, () => { Install.Run(s, _ => { }, "off"); Install.Run(s, _ => { }, "off"); Install.Remove(_ => { }); });
    True(AutoMemoryKey(settings) == null && !File.Exists(Mark(data)), $"no key, no mark; {AutoMemoryKey(settings) ?? "no key"}, mark {File.Exists(Mark(data))}");
});

// ---- the install guard ----

Check("safety: control: the install guard is an ERROR when XDG_DATA_HOME points outside the temp folder", () =>
{
    var threw = false;
    var before = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    try
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", "/nonexistent-real-home/.local/share");
        try { Sandboxed(); } catch (SetupError) { threw = true; }
    }
    finally { Environment.SetEnvironmentVariable("XDG_DATA_HOME", before); }
    True(threw, "Sandboxed() refused");
    True(!Directory.Exists("/nonexistent-real-home"), "nothing made there");
});

// ---- team setup with a keeper named ----

Check("team setup: naming the keeper is refused, and it keeps its role", () =>
{
    using var s = NewHive();
    s.CreateBee("Warden", "Tidies", [], "", "approve", role: "keeper");
    True(Throws(() => Reviews.SetUp(s, ["Warden", "Arbiter"])), "refused");
    True(BeeRole(s, "Warden") == "keeper" && s.FindBee("Arbiter") == null, "Warden still the keeper, nothing made");
});

// ---- reviewer read-only Bash: the PreToolUse hook ----
// A PreToolUse (matcher Bash) hook lets reviewers and the
// Overseer only read: git log/diff/show/status/blame, ls, wc, du, one
// command at a time, no shell operators, $, backticks, --output or --ext-diff.
// Hooks.ReadOnlyRefusal(cmd) is public and pure: null when the command may
// run, else the reason it was refused. These checks try to slip a write past
// it (quoting, abbreviations, env prefixes, absolute paths, operators) and
// confirm every legitimate read-only command still runs. Made-up names only.

// Every command here must be ALLOWED (ReadOnlyRefusal returns null): plain
// read-only forms, plus quoting that still resolves to an allowed command.
string[] allowedCmds =
{
    "git log", "git log --oneline -n 20", "git log --format=%H", "git log --format='%an %s'".Replace("'", ""),
    "git diff", "git diff --stat", "git diff --no-index a b", "git diff --output-indicator-new=+",
    "git show HEAD", "git show --stat", "git status", "git status --porcelain", "git blame src/x.cs",
    "ls", "ls -la /some/where", "wc -l file", "du -sh .",
    "git   log", "git\tlog", "  git log  ", "git \"log\"", "\"git\" log", "\\ls",
};
// Every command here must be REFUSED, with a non-empty reason. The comment on
// each group says the trick it tries.
string[] refusedCmds =
{
    // not an allowed command at all
    "", "   ", "touch x", "git", "git clone https://example.com/x", "lsblk", "lsof", "rm -rf x",
    // the write/execute git options, however written
    "git diff --output=x", "git diff --output x", "git diff --out\"\"put=x", "git diff --o=x",
    "git log --output", "git diff --ext-diff=foo", "git diff --ext-diff", "git show --ext", "git diff --outp=y",
    // shell operators (chaining, pipes, redirects, substitution)
    "git log; touch x", "git log && touch x", "git log || touch x", "git log | wc -l",
    "git diff > out", "git diff >> out", "git log 2> err", "git show < in", "echo $HOME",
    "git log $(touch x)", "git log `touch x`", "git log\ntouch x", "git log\r\ntouch x",
    "(touch x)", "git log & touch x",
    // placing git global options before the subcommand, or an env/path prefix
    "git -c core.pager=touch log", "git -C /other/repo log", "git --git-dir=/x/.git log",
    "git --no-pager log", "GIT_EXTERNAL_DIFF=touch git diff", "PAGER=touch git log",
    "/usr/bin/git log", "/bin/ls", "./git log",
    // an unclosed quote
    "git log \"unterminated", "git log 'unterminated",
    // over-refused but safe: a legit read-only git flag whose value is an
    // operator char (< here) trips the operator check. Refused, with a reason.
    "git log --output-indicator-old=<",
};

Check("hook ReadOnlyRefusal: every read-only command is allowed (returns null)", () =>
{
    var wrong = allowedCmds.Where(c => Hooks.ReadOnlyRefusal(c) is not null)
                           .Select(c => $"[{c.Replace("\t", "\\t")}] -> {Hooks.ReadOnlyRefusal(c)}").ToList();
    True(wrong.Count == 0, "all allowed, but refused: " + string.Join(" ;; ", wrong));
});

Check("hook ReadOnlyRefusal: every write/chaining attempt is refused with a reason", () =>
{
    var leaked = refusedCmds.Where(c => Hooks.ReadOnlyRefusal(c) is null).ToList();
    True(leaked.Count == 0, "these slipped through (allowed): " + string.Join(" ;; ", leaked.Select(c => $"[{c.Replace("\n", "\\n").Replace("\r", "\\r")}]")));
    var noReason = refusedCmds.Where(c => Hooks.ReadOnlyRefusal(c) is { Length: 0 }).ToList();
    True(noReason.Count == 0, "refused with an empty reason: " + string.Join(" ;; ", noReason));
});

Check("hook ReadOnlyRefusal control: --output refused but the same git diff without it allowed", () =>
{
    True(Hooks.ReadOnlyRefusal("git diff --output=/tmp/x") is { Length: > 0 }, "git diff --output=... refused with a reason");
    True(Hooks.ReadOnlyRefusal("git diff --stat") is null, "the same git diff --stat allowed (so the refusal is the --output, not git diff)");
});

Check("hook Words: quotes and backslashes are taken out; an unclosed quote is null", () =>
{
    True(Hooks.Words("git \"a b\" c") is ["git", "a b", "c"], "double quotes join a word");
    True(Hooks.Words("git 'a b'") is ["git", "a b"], "single quotes join a word");
    True(Hooks.Words("git --out\"\"put") is ["git", "--output"], "empty quotes vanish: --out\"\"put is --output");
    True(Hooks.Words("a\\ b") is ["a b"], "a backslash escapes the next char");
    True(Hooks.Words("  a   b  ") is ["a", "b"], "runs of spaces/leading/trailing collapse");
    True(Hooks.Words("a\tb") is ["a", "b"], "a tab separates words");
    True(Hooks.Words("git \"oops") is null, "an unclosed double quote is null");
    True(Hooks.Words("git 'oops") is null, "an unclosed single quote is null");
    True(Hooks.Words("") is { Count: 0 }, "the empty command is an empty word list, not null");
});

// ---- the hook routing: Hooks.Handle("pre-tool-use", input) ----
// A deny goes back as hookSpecificOutput.permissionDecision "deny" with a
// reason; a pass-through is a null reply. Only reviewers/Overseer with a Bash
// call that doesn't read are denied; everything else passes.
static string? Decision(JObj? reply) => ((reply?["hookSpecificOutput"] as JObj)?["permissionDecision"])?.GetValue<string>();
static string Reason(JObj? reply) => ((reply?["hookSpecificOutput"] as JObj)?["permissionDecisionReason"])?.GetValue<string>() ?? "";
JObj Bash(string agentType, string command) => new() { ["tool_name"] = "Bash", ["agent_type"] = agentType, ["tool_input"] = new JObj { ["command"] = command } };

Store ReviewerHive()
{
    var s = NewHive();
    s.CreateBee("Scout", "Reads code for bugs", [], "Looks hard.", "approve", role: "reviewer");
    s.CreateBee("Marshal", "Checks the findings", [], "Weighs each one.", "approve", role: "overseer");
    s.CreateBee("Mason", "Builds things", [], "Builds carefully.", "approve");
    return s;
}

Check("hook Handle: a reviewer's write Bash is denied with a reason, its read Bash passes", () =>
{
    using var s = ReviewerHive();
    var deny = Hooks.Handle(s, "pre-tool-use", Bash("scout", "touch pwned"));
    True(Decision(deny) == "deny" && Reason(deny).Length > 0, "touch denied for the reviewer with a reason; got " + (deny?.ToJsonString() ?? "null"));
    True(Hooks.Handle(s, "pre-tool-use", Bash("scout", "git log --oneline")) == null, "git log passes (null reply)");
});

Check("hook Handle: the Overseer is held to read-only too", () =>
{
    using var s = ReviewerHive();
    True(Decision(Hooks.Handle(s, "pre-tool-use", Bash("marshal", "git clone x"))) == "deny", "git clone denied for the Overseer");
    True(Hooks.Handle(s, "pre-tool-use", Bash("marshal", "git diff")) == null, "git diff passes for the Overseer");
});

Check("hook Handle pass-through: a non-reviewer bee, no agent_type, a plugin and a non-bee all pass", () =>
{
    using var s = ReviewerHive();
    True(Hooks.Handle(s, "pre-tool-use", Bash("mason", "touch x")) == null, "a builder bee's touch passes through");
    True(Hooks.Handle(s, "pre-tool-use", new JObj { ["tool_name"] = "Bash", ["tool_input"] = new JObj { ["command"] = "touch x" } }) == null, "no agent_type (the main session) passes");
    True(Hooks.Handle(s, "pre-tool-use", Bash("a:b", "touch x")) == null, "a plugin agent_type (has a colon) passes");
    True(Hooks.Handle(s, "pre-tool-use", Bash("ghost", "touch x")) == null, "an agent_type that is no bee passes");
});

Check("hook Handle pass-through: a non-Bash tool from a reviewer is never touched", () =>
{
    using var s = ReviewerHive();
    var read = new JObj { ["tool_name"] = "Read", ["agent_type"] = "scout", ["tool_input"] = new JObj { ["file_path"] = "/etc/passwd" } };
    True(Hooks.Handle(s, "pre-tool-use", read) == null, "a reviewer's Read passes through");
    var write = new JObj { ["tool_name"] = "Write", ["agent_type"] = "scout", ["tool_input"] = new JObj { ["file_path"] = "/x", ["content"] = "y" } };
    True(Hooks.Handle(s, "pre-tool-use", write) == null, "even a reviewer's Write isn't the Bash hook's business (the tools line keeps Write out)");
});

Check("hook Handle: malformed input never crashes or blocks (empty, missing fields, odd types)", () =>
{
    using var s = ReviewerHive();
    True(Hooks.Handle(s, "pre-tool-use", new JObj()) == null, "an empty object passes, no crash");
    True(Hooks.Handle(s, "pre-tool-use", new JObj { ["tool_name"] = 42, ["agent_type"] = 7 }) == null, "numbers where strings are expected don't crash");
    True(Hooks.Handle(s, "pre-tool-use", new JObj { ["tool_name"] = "Bash", ["agent_type"] = "scout" }) is { } r && Decision(r) == "deny", "a reviewer Bash with no tool_input is denied (empty command), not a crash");
});

// ---- the subagent files get Bash only while the hook is installed ----
// Agents.Write gives reviewers/Overseer plain Bash exactly when
// Install.BashHookInstalled() is true: settings.json has `hive hook
// pre-tool-use` AND that binary exists. Build a real binary and a settings.json
// to drive every branch.
string hiveBin = Path.Combine(scratch, "hive-fake-bin");
File.WriteAllText(hiveBin, "#!/bin/sh\n");
string hiveBinSpaced = Path.Combine(scratch, "dir with space", "hive-fake-bin");
Directory.CreateDirectory(Path.GetDirectoryName(hiveBinSpaced)!);
File.WriteAllText(hiveBinSpaced, "#!/bin/sh\n");
void WriteSettings(string? hookCommand)
{
    Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
    var root = new JObj();
    if (hookCommand != null)
        root["hooks"] = new JObj { ["PreToolUse"] = new JArr { new JObj { ["matcher"] = "Bash", ["hooks"] = new JArr { new JObj { ["type"] = "command", ["command"] = hookCommand, ["timeout"] = 10 } } } } };
    File.WriteAllText(settings, root.ToJsonString());
}
string ReviewerTools(Store s, string name) { Agents.Write(s.RequireBee(name)); return Doc.Load(AgentFile(name)).Get("tools"); }
bool HasBash(string tools) => Regex.IsMatch(tools, @"\bBash\b");

Check("BashHookInstalled: true only with our pre-tool-use hook AND an existing binary", () =>
{
    WriteSettings($"{hiveBin} hook pre-tool-use");
    True(Install.BashHookInstalled(), "installed: our hook with a binary that exists");
    WriteSettings($"HIVE_HOME=/some/hive {hiveBin} hook pre-tool-use");
    True(Install.BashHookInstalled(), "installed: a HIVE_HOME= prefix is fine");
    WriteSettings($"'{hiveBinSpaced}' hook pre-tool-use");
    True(Install.BashHookInstalled(), "installed: a quoted path with a space resolves");
    WriteSettings($"{Path.Combine(scratch, "gone-hive-bin")} hook pre-tool-use");
    True(!Install.BashHookInstalled(), "not installed: the hook's binary is gone (Claude Code fails open, so no Bash)");
    WriteSettings(null);
    True(!Install.BashHookInstalled(), "not installed: no PreToolUse hook at all");
    File.WriteAllText(settings, "{ not json");
    True(!Install.BashHookInstalled(), "not installed: an unreadable settings.json counts as no");
});

Check("agent files: a reviewer gets Bash when the hook is installed, none when it isn't", () =>
{
    using var s = ReviewerHive();
    WriteSettings($"{hiveBin} hook pre-tool-use");
    True(HasBash(ReviewerTools(s, "Scout")) && HasBash(ReviewerTools(s, "Marshal")), "Scout and Marshal get Bash while the hook is installed");
    WriteSettings(null);
    var tools = ReviewerTools(s, "Scout");
    True(!HasBash(tools), "no Bash in Scout's file without the hook; tools: " + tools);
    True(tools.Contains("Read") && tools.Contains("mcp__hive__hive_recall"), "the read-only tools are still there; tools: " + tools);
});

Check("agent files: the installed companion to \"no Bash entry of any form\" (installed case)", () =>
{
    using var s = NewHive();
    WriteSettings($"{hiveBin} hook pre-tool-use");
    Reviews.SetUp(s, teamNames);
    foreach (var name in teamNames)
    {
        var tools = Doc.Load(AgentFile(name)).Get("tools");
        True(HasBash(tools), $"{name} has a Bash entry with the hook installed; tools: {tools}");
        True(!Regex.IsMatch(tools, @"\bEdit\b|\bWrite\b|\bMultiEdit\b"), $"still no editing tools for {name}; tools: {tools}");
    }
    WriteSettings(null); // leave settings without our hook for anything after
});

Check("agent files control: a worker bee never gets a tools line (so no Bash gating applies)", () =>
{
    using var s = ReviewerHive();
    WriteSettings($"{hiveBin} hook pre-tool-use");
    True(!Agents.ReadsOnly(s.RequireBee("Mason")), "Mason is not read-only");
    Agents.Write(s.RequireBee("Mason"));
    True(Doc.Load(AgentFile("Mason")).Get("tools").Length == 0, "a worker's file has no tools line: it gets every tool, and the hook leaves it alone");
    True(Agents.ReadsOnly(s.RequireBee("Scout")) && Agents.ReadsOnly(s.RequireBee("Marshal")), "a reviewer and the Overseer are read-only");
    WriteSettings(null);
});

// ---- install / uninstall carry the PreToolUse (Bash) entry ----
static int OurPreToolUse(string settingsJson)
{
    var pre = (System.Text.Json.Nodes.JsonNode.Parse(settingsJson)?["hooks"] as JObj)?["PreToolUse"] as JArr;
    return pre?.Count(e => (e as JObj)?["hooks"] is JArr inner && inner.Any(h =>
        (h as JObj)?["command"]?.GetValue<string>() is { } c && c.Contains("hive") && c.TrimEnd().EndsWith("hook pre-tool-use"))) ?? 0;
}

Check("install writes one PreToolUse entry with matcher Bash; installing twice keeps one", () =>
{
    using var s = NewHive();
    File.Delete(settings);
    WithEnv(fakeBin + ":" + realPath, Path.Combine(scratch, "data-ptu"), () => { Install.Run(s, _ => { }, "keep"); Install.Run(s, _ => { }, "keep"); });
    var json = File.ReadAllText(settings);
    var pre = (System.Text.Json.Nodes.JsonNode.Parse(json)?["hooks"] as JObj)?["PreToolUse"] as JArr;
    True(pre != null && pre.Count == 1, "exactly one PreToolUse entry after installing twice; got " + (pre?.Count.ToString() ?? "none"));
    True((pre?[0] as JObj)?["matcher"]?.GetValue<string>() == "Bash", "the entry's matcher is Bash");
    True(OurPreToolUse(json) == 1, "the one entry is ours (ends with hook pre-tool-use)");
});

Check("uninstall removes our PreToolUse entry and keeps the user's own hooks", () =>
{
    using var s = NewHive();
    Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
    File.WriteAllText(settings, """{ "hooks": { "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "my-own-linter" } ] } ] } }""");
    var data = Path.Combine(scratch, "data-ptu2");
    WithEnv(fakeBin + ":" + realPath, data, () => Install.Run(s, _ => { }));
    var afterInstall = File.ReadAllText(settings);
    True(OurPreToolUse(afterInstall) == 1 && afterInstall.Contains("my-own-linter"), "after install: our hook added, the user's linter kept");
    WithEnv(fakeBin + ":" + realPath, data, () => Install.Remove(_ => { }));
    var afterRemove = File.ReadAllText(settings);
    True(OurPreToolUse(afterRemove) == 0, "after uninstall: our PreToolUse entry gone");
    True(afterRemove.Contains("my-own-linter"), "the user's own PreToolUse hook survives uninstall");
    File.Delete(settings);
});

// ---- after merging main's 0.1.05-0.1.08 into the role-based hive ----

Check("merge: giving a bee the reviewer role rewrites its subagent file read-only; taking the role away gives every tool back", () =>
{
    using var s = NewHive();
    s.CreateBee("Quill", "Writes", [], "", "approve");
    Pre(Doc.Load(AgentFile("Quill")).Get("tools") == "", "no tools line to start");
    s.UpdateBee("Quill", null, null, null, role: "reviewer");
    var tools = Doc.Load(AgentFile("Quill")).Get("tools");
    True(tools.Length > 0 && !Regex.IsMatch(tools, @"\b(Edit|Write|MultiEdit)\b"), "a read-only tools line once a reviewer; tools: " + tools);
    s.UpdateBee("Quill", null, null, null, role: "none");
    True(Doc.Load(AgentFile("Quill")).Get("tools") == "", "no tools line again; tools: " + Doc.Load(AgentFile("Quill")).Get("tools"));
});

Check("merge: an edit task has Skill and the settings' edit_commands, and none of the old fixed build commands", () =>
{
    using var s = NewHive();
    s.CreateBee("Quill", "Writes", [], "", "approve");
    File.WriteAllText(Path.Combine(s.Root, "settings.md"), "---\nedit_commands: [npm run]\n---\n");
    var allowed = (FlagValue(Tasks.Args(Tasks.Create(s, "Quill", "Fix it", "", "edit")), "--allowedTools") ?? "").Split(',');
    True(allowed.Contains("Skill") && allowed.Contains("Bash(npm run *)") && allowed.Contains("Bash(ps *)"), "Skill, npm run and ps; " + string.Join(",", allowed));
    True(!allowed.Any(a => a.Contains("make") || a.Contains("build.sh") || a.Contains("cargo") || a.Contains("godot") || a.Contains("dotnet")), "nothing else from a fixed list; " + string.Join(",", allowed));
    var read = (FlagValue(Tasks.Args(Tasks.Create(s, "Quill", "Look", "", "read")), "--allowedTools") ?? "").Split(',');
    True(!read.Contains("Skill") && !read.Contains("Bash(npm run *)"), "a read task gets neither; " + string.Join(",", read));
});

// ---- run ----

int failed = 0, errors = 0;
foreach (var (name, run) in checks)
{
    try { run(); Console.WriteLine($"PASS  {name}"); }
    catch (SetupError e)
    {
        errors++;
        Console.WriteLine($"ERROR {name}\n      setup: {e.Message.Replace("\n", "\n      ")}");
    }
    catch (Exception e)
    {
        failed++;
        var what = e.GetType() == typeof(Exception) ? e.Message : $"threw {e.GetType().Name}: {e.Message}";
        Console.WriteLine($"FAIL  {name}\n      {what.Replace("\n", "\n      ")}");
    }
}
try { Directory.Delete(scratch, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
Console.WriteLine($"\n{checks.Count - failed - errors} passed, {failed} failed" + (errors > 0 ? $", {errors} couldn't run" : ""));
return failed + errors == 0 ? 0 : 1;

static string FindRepo()
{
    for (var dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
        if (File.Exists(Path.Combine(dir, "hive.csproj"))) return dir;
    throw new Exception("can't find the repository (hive.csproj) above " + AppContext.BaseDirectory);
}

// Claude Code's rules after a flag (--allowedTools or --disallowedTools): one
// argument or several, each split at commas and spaces outside parentheses.
static List<string> Rules(List<string> args, string flag)
{
    var rules = new List<string>();
    for (int at = args.IndexOf(flag) + 1; at > 0 && at < args.Count && !args[at].StartsWith('-'); at++)
    {
        var depth = 0;
        var rule = new System.Text.StringBuilder();
        foreach (var ch in args[at] + ",")
        {
            if (ch == '(') depth++;
            if (ch == ')') depth--;
            if (depth == 0 && ch is ',' or ' ') { if (rule.Length > 0) rules.Add(rule.ToString()); rule.Clear(); }
            else rule.Append(ch);
        }
    }
    return rules;
}

static bool RuleMatches(string rule, string command) =>
    rule == "Bash" || rule.StartsWith("Bash(") && rule.EndsWith(')')
        && Regex.IsMatch(command, "^" + Regex.Escape(rule[5..^1]).Replace(@"\*", ".*") + "$");

static bool Permitted(List<string> args, string command) =>
    !args.Contains("--dangerously-skip-permissions")
        ? Rules(args, "--allowedTools").Any(r => RuleMatches(r, command)) && !Rules(args, "--disallowedTools").Any(r => RuleMatches(r, command))
        : true;

static bool AllowsTool(List<string> args, string tool)
{
    bool Names(string r) => r == tool || r == "mcp__hive" || r == "mcp__hive__*";
    return Rules(args, "--allowedTools").Any(Names) && !Rules(args, "--disallowedTools").Any(Names);
}

static bool CanRead(string path)
{
    try { File.ReadAllText(path); return true; }
    catch (UnauthorizedAccessException) { return false; }
}

static bool CanList(string dir)
{
    try { Directory.GetFiles(dir); return true; }
    catch (UnauthorizedAccessException) { return false; }
}

// A process that stands in for a runner (or for a stranger that got its pid).
static Process Sleeper() => Process.Start("sleep", ["60"]);

// A stand-in for a task's live runner: its command line holds `run-task <id>`,
// as `hive run-task <id>`'s does (Tasks takes any other process for a stranger).
static Process Runner(string id) => Process.Start(new ProcessStartInfo("sh", ["-c", "sleep 60; :", "run-task", id])
{
    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
})!;

static bool Throws(Action a)
{
    try { a(); return false; }
    catch (HiveException) { return true; }
}

static void True(bool ok, string what)
{
    if (!ok) throw new Exception("expected " + what);
}

static void Pre(bool ok, string what)
{
    if (!ok) throw new SetupError("expected " + what);
}

sealed class SetupError(string message) : Exception(message);
