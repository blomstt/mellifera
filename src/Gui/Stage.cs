using static Hive.Gui.Ui;

namespace Hive.Gui;

// The Hive page: the scene at the top (Scene.cs), the Timeline under it, the
// work pane beside them, and the status line under everything. This file is
// what they show: who works on what (tasks, reviews, subagents in the user's
// sessions, chats in a terminal), what's connected, and a bee's popover.
sealed unsafe partial class App
{
    // A hive cell's bee (or the Queen) and what it says under its name.
    record struct Cell(string Kind, string Name, string Sub);

    // Something a bee is doing now: a task from the window or a review, a
    // subagent in one of the user's own sessions (its .state file: Parent is
    // the session that spawned it), or a chat the user has open with it
    // (`hive talk`, its .talk file).
    sealed class Work(string bee, string title, DateTime started, BeeTask? task, string parent = "", string folder = "", bool talk = false)
    {
        public readonly string Bee = bee, Title = title, Parent = parent, Folder = folder;
        public readonly DateTime Started = started;
        public readonly BeeTask? Task = task; // null: a subagent in a session, or a chat
        public readonly bool Talk = talk;
        public bool Waiting => Task?.Status == "waiting";
        public StepLog? Log; // a task's live steps

        // The step it's on now, and how the newest result came back.
        public Steps.Step? Step => Log?.Current;
    }

    // A running task's newest steps, read from its step log as it grows.
    sealed class StepLog
    {
        public long Offset;
        public readonly List<Steps.Step> Last = []; // the newest 50
        public Steps.Step? Current; // the newest step that isn't a result
        public Steps.Step? Result;  // the newest result, if it came after Current
        public int Steps;           // how many steps it has taken (not counting results)
    }

    readonly Dictionary<string, StepLog> stepLogs = [];

    // Reads what was added to a task's step log since the last look; true if anything was.
    bool ReadSteps(Work w)
    {
        if (w.Task == null) return false;
        if (!stepLogs.TryGetValue(w.Task.Id, out var log)) stepLogs[w.Task.Id] = log = new StepLog();
        w.Log = log;
        string added;
        try
        {
            using var f = new FileStream(w.Task.StepsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (f.Length == log.Offset) return false;
            if (f.Length < log.Offset) log.Offset = 0; // started over
            f.Seek(log.Offset, SeekOrigin.Begin);
            var bytes = new byte[f.Length - log.Offset];
            var n = f.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            var end = Array.LastIndexOf(bytes, (byte)'\n', n - 1); // whole lines only
            if (end < 0) return false;
            log.Offset += end + 1;
            added = System.Text.Encoding.UTF8.GetString(bytes, 0, end);
        }
        catch (IOException) { return false; } // no steps yet
        foreach (var line in added.Split('\n'))
        {
            if (Steps.Parse(line) is not { } step) continue;
            log.Last.Add(step);
            if (step.IsResult) { log.Result = step; continue; }
            log.Steps++;
            log.Current = step;
            log.Result = null;
        }
        if (log.Last.Count > 50) log.Last.RemoveRange(0, log.Last.Count - 50);
        return true;
    }

    // A step log grew: only the live steps change, so no full refresh.
    void RefreshSteps()
    {
        var any = false;
        foreach (var w in work) any |= ReadSteps(w);
        if (!any) return;
        BuildCells();
        RefreshTasks();
        SyncScene();
        UpdateTaskView();
        UpdateTalk();
    }

    // Connected work: what one bee hands another, like a reviewer's report the
    // overseer waits for. A dotted path joins them while the first bee still works.
    sealed class Link(string from, string to, string what, bool flowing, string task)
    {
        public readonly string From = from, To = to, What = what, Task = task;
        public readonly bool Flowing = flowing;
    }

    nint hiveStats, popover, hiveSheet, hiveSplit, hiveColumn, pane, sheetBar, timelineRange;
    List<Hit> activityHits = []; // the newest entries: for deposits and the Timeline's ticks
    List<BeeTask> allTasks = [];
    List<Work> work = [];
    List<Link> links = [];
    List<Cell> occupied = []; // the Queen, then the bees
    Dictionary<string, int> honey = [];
    Dictionary<string, (long Count, string Last)> activity = [];
    HashSet<string>? seenHits;

    // As the work pane design lays it out: the scene over the Timeline in a
    // column, and beside them the work pane, the page's full height, 38% of
    // the page wide and never under 380 px. It follows the window: no
    // handle, nothing remembered. The page scrolls when the window is short.
    // Narrow, the pane goes into a bottom sheet. The inbox is in the
    // header's ▤ popover (App.cs).
    nint BuildHive()
    {
        hiveStats = Css(N.gtk_label_new(""), "dim-label", "counts");
        atWork = Css(N.gtk_label_new(""), "honey");
        restLabel = N.gtk_label_new("");
        var scenePanel = Panel("hive", Margin(BuildScene(), 10, 18, 12, 18));
        PanelRight(scenePanel, Box(Horizontal, 0, atWork, restLabel));
        Expand(scenePanel, true, false);
        popover = N.gtk_popover_new();
        N.gtk_popover_set_has_arrow(popover, false);
        N.gtk_popover_set_position(popover, 1); // right of the bee, or left near the right edge (centred under it, a left-column bee's ran out of the window)
        N.gtk_widget_set_parent(popover, sceneArea);

        var timelinePanel = Panel("timeline", Margin(BuildTimeline(), 10, 18, 14, 18));
        timelineRange = PanelRight(timelinePanel, "");
        // The gap before the pane is the column's margin, so the pane is its 38% exactly.
        hiveColumn = Margin(Box(Vertical, 0, scenePanel, Margin(timelinePanel, 20, 0, 0, 0)), 0, 20, 0, 0);
        pane = BuildTalk(); // the Queen's conversation (TalkView.cs)
        hiveSplit = N.adw_overlay_split_view_new();
        N.adw_overlay_split_view_set_content(hiveSplit, hiveColumn);
        N.adw_overlay_split_view_set_sidebar(hiveSplit, pane);
        N.adw_overlay_split_view_set_sidebar_position(hiveSplit, 1); // GTK_PACK_END
        N.adw_overlay_split_view_set_sidebar_width_unit(hiveSplit, 0); // ADW_LENGTH_UNIT_PX
        N.adw_overlay_split_view_set_sidebar_width_fraction(hiveSplit, 0.38);
        N.adw_overlay_split_view_set_min_sidebar_width(hiveSplit, 380);
        N.adw_overlay_split_view_set_max_sidebar_width(hiveSplit, 1 << 20); // no maximum: 38% however wide
        BuildInbox();
        var page = Margin(hiveSplit, 24, 20, 20, 20);
        // Narrow, the pane is a sheet that this bar opens.
        sheetBar = Margin(Css(ArtLabel(), "panel-title"), 10, 18, 10, 18);
        N.gtk_label_set_markup(sheetBar, QueenTitle);
        N.gtk_widget_set_halign(sheetBar, Start);
        N.g_object_ref(sheetBar); // kept while it's out of the sheet
        hiveSheet = N.adw_bottom_sheet_new();
        N.adw_bottom_sheet_set_content(hiveSheet, Scroll(page));
        return hiveSheet;
    }

    // ┤ ♛ queen ├, in the honey frame.
    static readonly string QueenTitle = Palette.Span(Palette.Flight, "┤ ") + Palette.Span(Palette.Pulse, "♛") + Palette.Span(Palette.Bright, " queen") + Palette.Span(Palette.Flight, " ├");

    // What the scene, the Timeline and the inbox show, from the files.
    void RefreshHive()
    {
        var (entries, beeCount, projectCount) = store.Counts();
        entryCount = entries;
        N.gtk_label_set_text(hiveStats, entries + beeCount + projectCount == 0 ? "a new hive"
            : $"{entries} memor{(entries == 1 ? "y" : "ies")} · {beeCount} bee{(beeCount == 1 ? "" : "s")} · {projectCount} project{(projectCount == 1 ? "" : "s")}");
        activityHits = store.Search("", limit: 60);
        allTasks = Tasks.List(store);
        taskSignature = Tasks.Signature(store); // after Tasks.List, which marks dead tasks stopped
        stateSignature = StateSignature();
        activity = store.BeeActivity();
        honey = store.Honey();

        work = [];
        links = [];
        foreach (var t in allTasks)
        {
            if (!t.Running) continue;
            var w = new Work(t.Bee, TitleOf(t), When(t.Doc.Get("started", t.Doc.Get("created"))), t);
            ReadSteps(w);
            work.Add(w);
            foreach (var id in t.Doc.GetList("waits_for"))
                if (FindTask(id) is { } from)
                    links.Add(new Link(from.Bee, t.Bee, from.Bee + "'s report goes to " + t.Bee, from.Running, from.Id));
        }
        Subagents();
        Talks();
        foreach (var id in stepLogs.Keys.ToList()) if (!work.Exists(w => w.Task?.Id == id)) stepLogs.Remove(id);
        BuildCells();

        // What was saved since the last look lights a cell in the hive.
        var seen = new HashSet<string>();
        var saved = new List<Hit>();
        foreach (var h in activityHits)
        {
            seen.Add(h.Id);
            if (seenHits != null && !seenHits.Contains(h.Id) && DateTime.Now - When(h.Created) < TimeSpan.FromMinutes(10)) saved.Add(h);
        }
        seenHits = seen;

        RefreshInbox();
        RefreshTimeline();
        SyncScene();
        UpdateTaskView();
        UpdateTalk();
        foreach (var h in saved) Deposited(h.Bee, h.Type);
    }

    long entryCount;

    static DateTime When(string stamp) => DateTime.TryParse(stamp, out var t) ? t : DateTime.Now;

    BeeTask? FindTask(string id) => allTasks.Find(w => w.Id == id);

    // A review's tasks say what is reviewed; other tasks, the first line of what was asked.
    static string TitleOf(BeeTask t) => t.Doc.Get("focus") is { Length: > 0 } focus ? "review: " + focus : t.Doc.Get("role") == "talk" ? "talk: " + t.Title : t.Title;

    // Subagents running in the user's sessions: SubagentStart writes
    // .state/<agent id> (the bee, when it started, the session that spawned
    // it and its folder), SubagentStop deletes it. One older than 3 hours
    // lost its stop, so it doesn't count. A bee with a task running is
    // already lit by the task (the task's own session has a .state file too).
    void Subagents()
    {
        foreach (var lines in Presence(".state", 3))
        {
            var bee = BeeNamed(lines[0]);
            if (bee == null || work.Exists(w => w.Bee == bee.Name)) continue;
            var folder = lines.Length > 3 ? Hive.Text.Tilde(lines[3]) : "";
            work.Add(new Work(bee.Name, "a subagent in a Claude session" + (folder.Length > 0 ? " in " + folder : ""), Since(lines[1]), null,
                lines.Length > 2 ? lines[2] : "", folder));
        }
    }

    // Chats the user has open with a bee (`hive talk`): SessionStart writes
    // .talk/<session id>, each turn touches it, SessionEnd deletes it.
    void Talks()
    {
        foreach (var lines in Presence(".talk", 12))
            if (BeeNamed(lines[0]) is { } bee && !work.Exists(w => w.Bee == bee.Name))
                work.Add(new Work(bee.Name, "talking with you", Since(lines[1]), null, talk: true));
    }

    Bee? BeeNamed(string name) => bees.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    static DateTime Since(string ticks) => new DateTime(long.Parse(ticks), DateTimeKind.Utc).ToLocalTime();

    // The files in a presence folder written in the last hours, as lines
    // (the bee, then when it started in ticks, then more).
    List<string[]> Presence(string folder, int hours)
    {
        var found = new List<string[]>();
        var dir = Path.Combine(store.Root, folder);
        if (!Directory.Exists(dir)) return found;
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            string[] lines;
            try
            {
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-hours)) continue;
                lines = File.ReadAllLines(f);
            }
            catch (IOException) { continue; } // gone since it was listed
            if (lines.Length >= 2 && long.TryParse(lines[1], out var since) && since > 0 && since <= DateTime.MaxValue.Ticks) found.Add(lines);
        }
        return found;
    }

    long stateSignature;
    long StateSignature() => Mtime(".state") ^ Mtime(".talk");
    long Mtime(string folder) => Directory.Exists(Path.Combine(store.Root, folder)) ? Directory.GetLastWriteTimeUtc(Path.Combine(store.Root, folder)).Ticks : 0;

    // How long something has been going.
    static string Took(DateTime since)
    {
        var m = (int)(DateTime.Now - since).TotalMinutes;
        return m < 1 ? "just started" : m < 60 ? $"{m} min" : $"{m / 60} h {m % 60} min";
    }

    // What a bee is doing, for its cell, its popover and its label: a task's
    // live step (from its stream-json), or what kind of work it is, and how
    // long it has been going.
    static string Doing(Work w) =>
        (w.Talk ? "talking with you" : w.Task == null ? "in a session" : w.Waiting ? "waiting" : w.Step is { } step ? step.Say : "working") + " · " + Took(w.Started);

    Work? WorkOf(string bee)
    {
        Work? first = null;
        foreach (var w in work)
            if (w.Bee == bee && (first == null || first.Waiting && !w.Waiting)) first = w;
        return first;
    }

    // The hive cells' texts and the "N at work · M resting" counter: run
    // again each minute while bees work, for the times.
    void BuildCells()
    {
        occupied = [new Cell("queen", "Queen", talk != null && TalkBusy() != null ? "talking with you" : $"{entryCount} memories")];
        var busy = 0;
        foreach (var b in bees)
        {
            var key = b.Name.ToLowerInvariant();
            var (count, last) = activity.TryGetValue(key, out var a) ? a : (0, "");
            var h = honey.GetValueOrDefault(key);
            var w = WorkOf(b.Name);
            if (w is { Waiting: false }) busy++;
            occupied.Add(new Cell("bee", b.Name, w != null ? Doing(w) : h > 0 ? $"{h} honey · {Ago(last)}" : count == 0 ? "new" : $"{count} · {Ago(last)}"));
        }
        N.gtk_label_set_text(atWork, busy > 0 ? $"{busy} at work" : "");
        var resting = bees.Count - busy;
        N.gtk_label_set_text(restLabel, bees.Count == 0 ? "no bees yet" : busy > 0 ? $" · {resting} resting" : $"{resting} resting");
    }

    void Pop(double x, double y, double w, double h, nint child)
    {
        N.gtk_popover_set_child(popover, child);
        int* rect = stackalloc int[4]; // a GdkRectangle
        (rect[0], rect[1], rect[2], rect[3]) = ((int)x, (int)y, (int)w, (int)h);
        N.gtk_popover_set_pointing_to(popover, rect);
        N.gtk_popover_popup(popover);
    }

    // ---- a bee's popover -------------------------------------------------------

    // Clicking a bee (its sprite, its cell or its line in the work list): a
    // framed panel with what it's doing (or how its last task went), a task
    // box, Stop while it works, and its page. It points at the bee in the scene.
    void ShowBeePop(string name)
    {
        if (bees.FirstOrDefault(b => b.Name == name) is not { } bee) return;
        var box = Box(Vertical, 10);
        var busy = false;
        foreach (var w in work)
        {
            if (w.Bee != bee.Name) continue;
            busy = true;
            var line = Box(Horizontal, 8, Expand(Box(Vertical, 2, Label(w.Title, "heading"), Label(Doing(w), "dim-label")), true, false));
            if (w.Task is { } task)
            {
                var stop = Button("Stop", () =>
                {
                    Tasks.Stop(store, task.Id);
                    N.gtk_popover_popdown(popover);
                    Say("Stopped.");
                    RefreshHive();
                    RefreshTasks();
                }, "destructive-action");
                N.gtk_widget_set_valign(stop, Center);
                N.gtk_box_append(line, stop);
            }
            N.gtk_box_append(box, line);
        }
        if (!busy)
        {
            var last = Label(LastResult(bee), "dim-label");
            N.gtk_label_set_max_width_chars(last, 40); // wraps at the box's 440 px instead of widening the popover
            N.gtk_box_append(box, last);
        }

        var entry = N.gtk_entry_new();
        N.gtk_entry_set_placeholder_text(entry, $"A task for {bee.Name}");
        var choices = TaskChoices(bee, out var projectDrop, out var accessDrop);
        Action start = () =>
        {
            var p = (int)N.gtk_drop_down_get_selected(projectDrop);
            var task = Tasks.Create(store, bee.Name, Text(entry), p > 0 ? choices[p - 1].Name : "", Tasks.Access[N.gtk_drop_down_get_selected(accessDrop)]);
            Tasks.Start(task);
            N.gtk_popover_popdown(popover);
            Say($"{bee.Name} is on it. It runs in the background; the result shows up in the inbox.");
            RefreshHive();
            RefreshTasks();
        };
        On(entry, "activate", start);
        N.gtk_box_append(box, Prompt(entry));
        N.gtk_box_append(box, Box(Horizontal, 8, projectDrop, accessDrop, Expand(N.gtk_box_new(Horizontal, 0), true, false), Button("Start", start, "suggested-action")));
        N.gtk_box_append(box, EndAligned(Button("Open page", () =>
        {
            N.gtk_popover_popdown(popover);
            ShowBee(bee.Name);
            Go("bees");
            Reveal(beeSplit);
        }, "flat")));
        N.gtk_widget_set_size_request(box, 440, -1);
        var (px, py, pw, ph) = SpriteBox(bee.Name);
        var panel = Panel(bee.Name, Margin(Clamp(box, 440), 10)); // and no wider: the drop-downs ellipsize instead
        Pop(px, py, pw, ph, panel);
        Fx.DrawIn(panel); // the popover's frame draws itself in, its title typed
    }

    // How a bee's last task went, or when it last saved something.
    string LastResult(Bee bee)
    {
        foreach (var t in allTasks)
            if (t.Bee == bee.Name && !t.Running)
                return $"Last task {t.Status} {Ago(t.Doc.Get("finished"))}: " + (Hive.Text.OneLine(Md.Unmarked(t.Result)) is var r && r.Length > 160 ? r[..160] + "…" : r);
        return activity.TryGetValue(bee.Name.ToLowerInvariant(), out var a) ? $"Last saved something {Ago(a.Last)}." : "It hasn't worked yet.";
    }

}
