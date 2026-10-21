using System.Text.Json.Nodes;
using Hive;

return Cli.Run(args);

static class Cli
{
    const string Usage = """
        hive: long-term memory for Claude, kept on this computer.

        First time: `hive install`, then make a bee with `hive new-bee`, then `hive app`.

          hive app                        open the Hive window
          hive init                       make the hive (~/Hive, or $HIVE_HOME)
          hive install [--auto-memory off|keep]
                                          connect it to Claude Code: MCP server, a briefing at the
                                          start of every session, and a subagent for each bee; asks
                                          before turning Claude Code's own auto memory off
          hive uninstall                  disconnect it (the hive's files stay)

          hive new-bee <Name> --specialty "..." [--projects a,b] [--molt free|approve] [--instructions "..."]
          hive bees                       list the bees
          hive bee <Name> [--molt free|approve] [--specialty "..."] [--projects a,b] [--skills a,b]
                  [--role reviewer|overseer|evolver|keeper|none] [--evolve-after n] [--deposit-before-stop yes|no]
                                          show a bee's card, or change it

          hive project <name> [--summary "..."] [--path dir]... [--brief "..."]
                                          make or change a project; sessions in its folders get its briefing
          hive projects                   list the projects

          hive task <Bee> <what to do...> [--project p] [--access read|edit|all]
                                          give a bee a task; it runs in the background
          hive tasks [--bee B]            the tasks and how they went
          hive talk <Bee> [--project p]   a Claude session with you, run as that bee
          hive stop <task id>

          hive team setup <reviewer>... <overseer>
                                          the review team: the reviewer and overseer roles (new names
                                          become bees with empty cards)
          hive team review [--project p] <what to review...>
                                          blind reviews by the reviewers, then the overseer's report
          hive team runs | show <run> | stop <run>

          hive search <words...> [--project p] [--bee B] [--type t] [--all] [--limit n]
          hive read <id>
          hive brief [--project p] [--bee B]
          hive reward <id> [--note "..."] give a bee honey for an entry or a task (task:<id>) that was on point
          hive unreward <id>              take back honey or a miss, whoever gave it (you, the Queen or the hive)
          hive evolve <Bee>               ask the bee with the evolver role to review a bee
          hive review <Bee>               a bee's record: its card, its card's history, tasks, honey and misses
          hive undo-card <Bee>            put back the card from before its last change
          hive molts                      card changes bees proposed, with diffs
          hive approve <id> | reject <id>
          hive tidy                       duplicates, broken links, molts waiting
          hive import <file.md|dir>... [--project p]
                                          bring in Claude Code memory files (or any Markdown)
          hive reindex                    rebuild the search index from the files
          hive statusline                 Claude Code's status line: saves your usage limits for the
                                          Hive window (hive install sets it, if you have none)
        """;

    public static int Run(string[] argv)
    {
        List<string> pos;
        Dictionary<string, List<string>> opt;
        try { (pos, opt) = Args.Parse(argv); }
        catch (HiveException e)
        {
            Console.Error.WriteLine("hive: " + e.Message);
            return 2;
        }
        var cmd = Args.Command(pos, opt);
        string? One(string key) => opt.TryGetValue(key, out var v) ? v[^1] : null;
        List<string>? Many(string key) => opt.TryGetValue(key, out var v)
            ? v.SelectMany(x => x.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToList()
            : null;
        string Arg(int i, string what) => pos.Count > i ? pos[i] : throw new HiveException($"`hive {cmd}` needs {what}.");

        using var store = new Store(Store.DefaultRoot);
        try
        {
            switch (cmd)
            {
                case "mcp":
                    return Mcp.Serve(store);

                case "app":
                    return Hive.Gui.App.Run(store);

                case "run-task":
                    return Tasks.Run(store, Arg(1, "a task id"));

                case "task":
                {
                    var bee = store.RequireBee(Arg(1, "a bee's name"));
                    var task = Tasks.Create(store, bee.Name, string.Join(' ', pos.Skip(2)),
                                            One("project") ?? bee.Projects.FirstOrDefault() ?? "", One("access") ?? "");
                    Tasks.Start(task);
                    Console.WriteLine($"{bee.Name} is on it: task {task.Id}, in {task.Doc.Get("folder")}. `hive tasks` shows how it goes.");
                    return 0;
                }

                case "talk":
                    return Agents.Talk(store, store.RequireBee(Arg(1, "a bee's name")), One("project"));

                case "tasks":
                {
                    var tasks = Tasks.List(store, One("bee"));
                    if (tasks.Count == 0) { Console.WriteLine("No tasks yet."); return 0; }
                    foreach (var t in tasks.Take(20))
                    {
                        Console.WriteLine($"{t.Id} · {t.Bee} · {t.Status}{(t.Doc.Get("took") is { Length: > 0 } took ? " in " + took : "")} — {t.Title}");
                        if (!t.Running && t.Result.Trim() is { Length: > 0 } r) Console.WriteLine("    " + Text.Cap(Text.OneLine(r), 200));
                        if (t.Doc.Get("session") is { Length: > 0 } s) Console.WriteLine($"    continue it: claude --resume {s}");
                    }
                    return 0;
                }

                case "team":
                    return Team(store, pos, One);

                case "stop":
                    Tasks.Stop(store, Arg(1, "a task id"));
                    Console.WriteLine("Stopped.");
                    return 0;

                case "brief" when opt.ContainsKey("hook"):
                    return Hooks.Run(store, "session-start");

                case "hook":
                    return Hooks.Run(store, Arg(1, "an event"));

                case "statusline":
                    // Claude Code's status line: never fails a session, prints one line.
                    try { Console.WriteLine(Hive.Usage.StatusLine(store, Console.IsInputRedirected ? Console.In.ReadToEnd() : "")); }
                    catch (Exception) { Console.WriteLine("ʚ●ɞ"); }
                    return 0;

                case "init":
                    store.Init();
                    Console.WriteLine($"The hive is at {Text.Tilde(store.Root)}. Tell the Queen about yourself in {Text.Tilde(store.QueenPath)}.");
                    return 0;

                case "install":
                    Install.Run(store, Console.WriteLine, One("auto-memory"));
                    Console.WriteLine("New Claude sessions are connected to the hive.");
                    return 0;

                case "uninstall":
                    Install.Remove(Console.WriteLine);
                    return 0;

                case "new-bee":
                {
                    var bee = store.CreateBee(Arg(1, "a name"), One("specialty") ?? throw new HiveException("A bee needs --specialty."),
                                              Many("projects") ?? [], One("instructions") ?? "", One("molt") ?? "approve");
                    Console.WriteLine(Store.BeeLine(bee));
                    Console.WriteLine($"Card: {Text.Tilde(bee.Path)}");
                    return 0;
                }

                case "bees":
                    var bees = store.Bees();
                    Console.WriteLine(bees.Count == 0 ? "No bees yet. `hive new-bee <Name> --specialty \"...\"` makes one." : string.Join('\n', bees.Select(Store.BeeLine)));
                    return 0;

                case "bee":
                {
                    var name = Arg(1, "a bee's name");
                    // Every option checked first: a bad one changes nothing.
                    if (One("evolve-after") is { } a) Store.SettingValue("evolve_after", a);
                    if (One("deposit-before-stop") is { } d) Store.SettingValue("deposit_before_stop", d);
                    var bee = opt.Count > 0 ? store.UpdateBee(name, One("molt"), One("specialty"), Many("projects"), Many("skills"), One("role")) : store.RequireBee(name);
                    if (One("evolve-after") is { } after) bee = store.SetSetting(bee.Name, "evolve_after", after);
                    if (One("deposit-before-stop") is { } deposit) bee = store.SetSetting(bee.Name, "deposit_before_stop", deposit);
                    Console.WriteLine(Store.BeeLine(bee));
                    if (opt.Count == 0) Console.WriteLine($"{Text.Tilde(bee.Path)}\n\n{bee.Doc.Body.Trim()}");
                    return 0;
                }

                case "project":
                {
                    var p = store.SetProject(Arg(1, "a project name"), One("summary"), Many("path"), One("brief"));
                    Console.WriteLine($"{p.Name} — {p.Summary}" + (p.Paths.Count > 0 ? $" ({string.Join(", ", p.Paths)})" : ""));
                    return 0;
                }

                case "projects":
                    var projects = store.Projects();
                    Console.WriteLine(projects.Count == 0 ? "No projects yet. Make one with `hive project`." :
                        string.Join('\n', projects.Select(p => $"{p.Name} — {p.Summary}" + (p.Paths.Count > 0 ? $" ({string.Join(", ", p.Paths)})" : ""))));
                    return 0;

                case "search":
                    Console.WriteLine(Store.Lines(store.Search(string.Join(' ', pos.Skip(1)), One("project"), One("bee"), One("type"),
                                                               int.TryParse(One("limit"), out var n) ? n : 12, opt.ContainsKey("all")),
                                                  "Nothing matches.", snippets: true));
                    return 0;

                case "read":
                    Console.WriteLine(store.Read(Arg(1, "an id")));
                    return 0;

                case "brief":
                    Console.Write(store.Brief(One("project"), One("bee"), Environment.CurrentDirectory));
                    return 0;

                case "reward" or "unreward":
                {
                    var id = Arg(1, "an entry id, or task:<id>");
                    store.RewardEntry(id, One("note") ?? "", cmd == "reward");
                    Console.WriteLine(cmd == "reward" ? "Honey given." : "Taken back.");
                    return 0;
                }

                case "review":
                    Console.WriteLine(store.Review(Arg(1, "a bee's name")));
                    return 0;

                case "evolve":
                {
                    var task = Tasks.Evolve(store, Arg(1, "a bee's name"));
                    Console.WriteLine($"{task.Bee} is reviewing it: task {task.Id}. A card change it proposes shows in `hive molts`.");
                    return 0;
                }

                case "undo-card":
                {
                    var name = Arg(1, "a bee's name");
                    store.UndoCard(name);
                    Console.WriteLine($"{store.RequireBee(name).Name}'s card is back to the version before its last change.");
                    return 0;
                }

                case "molts":
                    ShowMolts(store);
                    return 0;

                case "approve" or "reject":
                {
                    var id = Arg(1, "a molt id (`hive molts` lists them)");
                    var bee = store.PendingMolt(id).Get("bee");
                    store.DecideMolt(id, cmd == "approve");
                    Console.WriteLine(cmd == "approve" ? $"{bee}'s card is changed." : $"Rejected; {bee}'s card stays as it was.");
                    return 0;
                }

                case "tidy":
                    Console.WriteLine(store.Tidy());
                    return 0;

                case "import":
                    Import(store, pos.Skip(1).ToList(), One("project") ?? "");
                    return 0;

                case "reindex":
                    store.Reindex();
                    Console.WriteLine("The index is rebuilt.");
                    return 0;

                case "help":
                    Console.WriteLine(Usage);
                    return 0;

                default:
                    Console.Error.WriteLine($"hive: there's no command {cmd}.\n\n{Usage}");
                    return 2;
            }
        }
        catch (HiveException e)
        {
            Console.Error.WriteLine("hive: " + e.Message);
            return 1;
        }
    }

    static void ShowMolts(Store store)
    {
        var pending = store.PendingMolts();
        if (pending.Count == 0) { Console.WriteLine("No molts are waiting."); return; }
        foreach (var (id, doc) in pending)
        {
            var by = doc.Get("by", doc.Get("bee"));
            Console.WriteLine($"── {id} · {(by == doc.Get("bee") ? by : $"{by} → {doc.Get("bee")}")} · {doc.Get("created")}");
            Console.WriteLine($"why: {doc.Get("reason")}\n");
            Console.WriteLine(store.MoltDiff(doc));
            Console.WriteLine($"hive approve {id}    hive reject {id}\n");
        }
    }

    static int Team(Store store, List<string> pos, Func<string, string?> one)
    {
        switch (pos.ElementAtOrDefault(1))
        {
            case "setup":
                var names = pos.Skip(2).ToArray();
                foreach (var b in Reviews.SetUp(store, names)) Console.WriteLine(Store.BeeLine(b));
                return 0;
            case "review":
            {
                var run = Reviews.Start(store, one("project") ?? "", string.Join(' ', pos.Skip(2)));
                Console.WriteLine($"Review {run.Id} started: {string.Join(", ", run.Reviewers.Select(r => r.Bee))} on their own, then {run.Overseer!.Bee}. `hive team show {run.Id}` shows how it goes.");
                return 0;
            }
            case "runs":
                var runs = Reviews.Runs(store);
                Console.WriteLine(runs.Count == 0 ? "No reviews yet." : string.Join('\n', runs.Select(r => $"{r.Id} · {r.Stage} — {r.Focus}{(r.Project.Length > 0 ? $" ({r.Project})" : "")}")));
                return 0;
            case "show":
            {
                var run = Reviews.Find(store, pos.ElementAtOrDefault(2) ?? throw new HiveException("`hive team show` needs a run id."));
                Console.WriteLine($"{run.Id} · {run.Stage} — {run.Focus}");
                foreach (var t in run.All) Console.WriteLine($"  {t.Bee}: {t.Status}{(t.Doc.Get("took") is { Length: > 0 } took ? " in " + took : "")}");
                if (run.Overseer is { Status: "done" } o) Console.WriteLine("\n" + o.Result.Trim());
                return 0;
            }
            case "stop":
                Reviews.Stop(store, pos.ElementAtOrDefault(2) ?? throw new HiveException("`hive team stop` needs a run id."));
                Console.WriteLine("Stopped.");
                return 0;
            default:
                throw new HiveException("`hive team` takes setup, review, runs, show or stop.");
        }
    }

    // Claude Code memory files: name, description and metadata.type in front
    // matter. Their types map onto the hive's; the description is the summary.
    static void Import(Store store, List<string> paths, string project)
    {
        if (paths.Count == 0) throw new HiveException("`hive import` needs files or folders.");
        var files = paths.Select(Text.Expand)
            .SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p, "*.md").Order() : File.Exists(p) ? (IEnumerable<string>)[p] : throw new HiveException($"{p} isn't there."))
            .Where(f => Path.GetFileName(f) != "MEMORY.md");
        foreach (var file in files)
        {
            var doc = Doc.Load(file);
            var name = doc.Get("name", Path.GetFileNameWithoutExtension(file));
            var type = doc.Get("metadata.type", doc.Get("type")) switch
            {
                "user" => "preference",
                "feedback" => "lesson",
                "project" or "reference" => "fact",
                var t when Store.Types.Contains(t) => t,
                _ => "note",
            };
            var title = string.Join(' ', Text.Words(name.Replace('-', ' ')));
            title = title.Length > 0 ? char.ToUpperInvariant(title[0]) + title[1..] : name;
            var summary = doc.Get("description", doc.Get("summary", title));
            var id = store.Deposit("queen", type, title, summary, doc.Body, project, ["imported"], []);
            Console.WriteLine($"{Path.GetFileName(file)} → {id}");
        }
    }
}
