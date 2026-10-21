using static Hive.Gui.Ui;

namespace Hive.Gui;

// The Bees page, as the mockup has it: the bees down the left (sprite, name,
// specialty, and whether it works, waits or rests), and the bee picked on
// the right: its header (its cell, large; its specialty and projects; give
// a task; a strip of numbers), its card rendered (edit turns it
// into the editor and the settings), what it does now, how its card
// changed, and its recent work. A card change it proposed waits on top.
sealed unsafe partial class App
{
    nint beeList, beeDetail, beeTasks, beeHeader, beeHeaderBin, beeCount, beeStats, beeNowButtons;
    string? selectedBee;
    Editor? shownCard;
    List<string>? beeRows, taskRows;
    List<BeeTask> tasks = [];         // the shown bee's recent tasks (done, failed, stopped)
    Lines? beeWorking, beeWaiting;    // its tasks at work and waiting, as work lines
    nint beeRecent, workingHead, waitingHead, recentHead;
    string beeNowShown = "", beeStatsShown = "";
    List<Recent> recent = [];

    // A line of a bee's recent work: a finished task or something it saved.
    sealed class Recent(string when, string title, string meta, string glyph, uint color, Action open)
    {
        public readonly string When = when, Title = title, Meta = meta, Glyph = glyph;
        public readonly uint Color = color;
        public readonly Action Open = open;
    }

    nint BuildBees()
    {
        beeList = List();
        OnArg(beeList, "row-activated", row => { ShowBee(bees[N.gtk_list_box_row_get_index(row)].Name); Reveal(beeSplit); });
        N.gtk_list_box_set_placeholder(beeList, Margin(Css(Label("No bees yet.", "dim-label"), "small"), 12, 16, 12, 16));
        var newBee = Expand(Button("+ new bee", NewBee, "wide"), true, false);
        var left = Box(Vertical, 0, Margin(beeList, 4, 0, 0, 0), Css(Margin(Box(Horizontal, 0, newBee), 10, 16, 10, 16), "row-sep"));
        beeDetail = Pane();
        beeSplit = Split(Scroll(left), "bees", beeDetail, "bee", 300, 380, 0.28, contentPanel: false);
        beeCount = PanelRight(sidebars[beeSplit], "");
        // The header panel stays from bee to bee (its frame draws in as the page opens); its inside changes.
        beeHeaderBin = N.adw_bin_new();
        beeHeader = Panel("bee", beeHeaderBin);
        N.g_object_ref(beeHeader);
        panels[beeSplit] = beeHeader;
        return beeSplit;
    }

    // What a bee is doing, for its row: its sprite, the words and their colour.
    (string Sprite, uint SpriteColor, string State, uint StateColor) BeeState(Bee b)
    {
        var w = WorkOf(b.Name);
        if (w == null) return ("ʚ●ɞ", Palette.Ghost, "resting", Palette.Ghost);
        if (w.Talk) return ("ʚ●ɞ", Palette.Pulse, "talking", Palette.Pulse);
        if (w.Waiting) return ("ʚ●ɞ", Palette.Pulse, "waits", Palette.Pulse);
        return ("⠹", Palette.Pulse, Short(w.Started), Palette.Pulse);
    }

    void RefreshBees()
    {
        string Key(Bee b)
        {
            var (sprite, _, state, _) = BeeState(b);
            return $"{b.Name}\n{b.Specialty}\n{sprite}{state}\n{honey.GetValueOrDefault(b.Name.ToLowerInvariant())} honey";
        }
        SetRows(beeList, ref beeRows, [.. bees.Select(Key)], i =>
        {
            var b = bees[i];
            var (sprite, spriteColor, state, stateColor) = BeeState(b);
            var art = ArtLabel();
            N.gtk_label_set_markup(art, Palette.Span(spriteColor, sprite));
            N.gtk_widget_set_size_request(art, 34, -1);
            var name = Css(N.gtk_label_new(b.Name), "bright");
            N.gtk_label_set_xalign(name, 0);
            var spec = Css(N.gtk_label_new(b.Specialty), "tiny", "dim-label");
            N.gtk_label_set_xalign(spec, 0);
            N.gtk_label_set_ellipsize(spec, 3);
            N.gtk_label_set_max_width_chars(spec, 1);
            var said = Css(N.gtk_label_new(state), "tiny");
            N.gtk_label_set_markup(said, Palette.Span(stateColor, state));
            var row = Css(Box(Horizontal, 12, art, Expand(Box(Vertical, 0, name, spec), true, false), said), "item");
            Said(row, $"{b.Name}, {b.Specialty}; {state}");
            return row;
        });
        SetPanelRight(beeCount, bees.Count > 0 ? bees.Count.ToString() : "");
        MarkBee();
        if (selectedBee != null && bees.FirstOrDefault(b => b.Name == selectedBee) is { } shown)
        {
            shownCard?.Update(shown.Doc.Body.Trim());
            ShowStats(shown);
        }
        else if (selectedBee != null) ShowBee(null);
    }

    void MarkBee()
    {
        var i = 0;
        for (var row = N.gtk_widget_get_first_child(beeList); row != 0; row = N.gtk_widget_get_next_sibling(row), i++)
        {
            if (i < bees.Count && bees[i].Name == selectedBee) N.gtk_widget_add_css_class(row, "on");
            else N.gtk_widget_remove_css_class(row, "on");
        }
    }

    // The strip under the header: its tasks from you, its honey, what it saved today, when its card changed.
    void ShowStats(Bee bee)
    {
        if (beeStats == 0) return;
        var fromYou = 0;
        foreach (var t in allTasks) if (t.Bee == bee.Name && t.Doc.Get("run").Length == 0 && t.Doc.Get("led_by") != "session") fromYou++;
        var today = 0;
        var day = DateTime.Now.ToString("yyyy-MM-dd");
        foreach (var h in store.Search("", bee: bee.Name, limit: 200)) if (h.Created.StartsWith(day)) today++;
        var changed = bee.Doc.Get("molted");
        var when = changed.Length >= 16 ? (changed.StartsWith(day) ? changed[11..16] : changed[..10]) : "never";
        string Stat(string n, string what) => Palette.Span(Palette.Bright, n) + Palette.Span(Palette.Dim, " " + what);
        var sep = Palette.Span(Palette.Rule, "   │   ");
        var text = Stat(fromYou.ToString(), fromYou == 1 ? "task from you" : "tasks from you") + sep
            + Stat(honey.GetValueOrDefault(bee.Name.ToLowerInvariant()).ToString(), "honey") + sep
            + Stat(today.ToString(), today == 1 ? "entry today" : "entries today") + sep
            + Palette.Span(Palette.Dim, "card changed ") + Palette.Span(Palette.Bright, when);
        if (text == beeStatsShown) return;
        N.gtk_label_set_markup(beeStats, beeStatsShown = text);
        Said(beeStats, $"{fromYou} tasks from you, {honey.GetValueOrDefault(bee.Name.ToLowerInvariant())} honey, {today} entries today, card changed {when}");
    }

    // Showing the bee that's shown again leaves its page (and unsaved card) alone.
    void ShowBee(string? name)
    {
        if (name != null && name == selectedBee) return;
        selectedBee = name;
        Titled(beeSplit, name ?? "bee");
        beeTasks = beeMolts = beeStats = beeNowButtons = 0;
        beeMoltsKey = null;
        taskRows = null;
        shownCard = null;
        vignette = null;
        beeNowShown = beeStatsShown = "";
        // The old page's work lines go with it.
        if (beeWorking != null) allLines.Remove(beeWorking);
        if (beeWaiting != null) allLines.Remove(beeWaiting);
        beeWorking = beeWaiting = null;
        MarkBee();
        // The header panel moves from the old page to the new one.
        if (N.gtk_widget_get_parent(beeHeader) is var parent and not 0) N.gtk_box_remove(parent, beeHeader);
        if (name == null)
        {
            N.adw_bin_set_child(beeHeaderBin, bees.Count == 0
                ? Status(Art.Bee, "No bees yet",
                         "A bee is a Claude subagent with its own name, specialty and card. Its briefing gives it what the hive knows, and what it saves stays for the others.",
                         Halign(Button("+ new bee", NewBee, "suggested-action"), Center))
                : Status(Art.Bee, "Pick a bee", "Its card, what it does now and what it has done."));
            SetPage(beeDetail, Margin(Box(Vertical, 0, beeHeader), 24, 20, 20, 10));
            return;
        }
        var bee = store.RequireBee(name);

        // The header: its cell, who it is, give a task, the numbers.
        var info = Box(Vertical, 4, Css(Label(bee.Name), "title-1"), Md.Para(bee.Specialty.TrimEnd('.') + "."));
        var chips = Box(Horizontal, 8);
        foreach (var p in bee.Projects) N.gtk_box_append(chips, Css(N.gtk_label_new(p), "chip"));
        N.gtk_box_append(chips, Margin(Css(N.gtk_label_new(bee.MoltsFreely ? "may change its own card" : "card changes wait for your approval"), "small", "dim-label"), 0, 0, 0, 6));
        N.gtk_box_append(info, Margin(chips, 6, 0, 0, 0));
        var taskBox = TaskBox(bee, out var giveTask);
        var buttons = Box(Vertical, 8, giveTask);
        N.gtk_widget_set_valign(buttons, Center);
        beeStats = Css(N.gtk_label_new(""), "small");
        N.gtk_label_set_xalign(beeStats, 0);
        var head = Box(Vertical, 14,
            Box(Horizontal, 24, BuildVignette(bee.Name), Expand(info, true, false), buttons),
            Css(Margin(beeStats, 14, 0, 0, 0), "row-sep"),
            taskBox);
        N.adw_bin_set_child(beeHeaderBin, Margin(head, 14, 24, 18, 24));
        ShowStats(bee);

        // Its card, rendered; edit turns it into the editor and the settings.
        var card = shownCard = new Editor(bee.Doc.Body.Trim(), "card");
        var rendered = Md.Render(bee.Doc.Body, skipTitle: true, rules: false);
        var editing = BeeEditor(bee, card);
        N.gtk_widget_set_visible(editing, false);
        var cardPanel = Panel("card", Margin(Box(Vertical, 0, rendered, editing), 12, 24, 18, 24));
        var edit = Button("edit", () => { }, "flat");
        On(edit, "clicked", () =>
        {
            var open = !N.gtk_widget_get_visible(editing);
            N.gtk_widget_set_visible(editing, open);
            N.gtk_widget_set_visible(rendered, !open);
            Said(edit, open ? "done" : "edit");
        });
        PanelRight(cardPanel, edit);
        Expand(cardPanel, true, false);
        N.gtk_widget_set_valign(cardPanel, Start);

        // What it does now, how its card changed, what it did lately.
        beeWorking = new Lines(this, Box(Vertical, 0), [], row => ShowTask(row.Work.Task!.Id));
        beeWaiting = new Lines(this, Box(Vertical, 0), [], row => ShowTask(row.Work.Task!.Id));
        allLines.Add(beeWorking);
        allLines.Add(beeWaiting);
        workingHead = Css(N.gtk_label_new("working"), "caps");
        waitingHead = Css(N.gtk_label_new("waiting on another bee"), "caps");
        recentHead = Css(N.gtk_label_new("recent"), "caps");
        foreach (var h in new[] { workingHead, waitingHead, recentHead }) { N.gtk_label_set_xalign(h, 0); N.gtk_widget_set_visible(h, false); Margin(h, 4, 16, 2, 16); }
        var resting = Margin(Css(Label($"{bee.Name} is resting in its cell."), "small", "dim-label"), 8, 16, 10, 16);
        beeNowButtons = Margin(Box(Horizontal, 8), 0, 16, 10, 38);
        beeTasks = Box(Vertical, 0, workingHead, beeWorking.Box, waitingHead, beeWaiting.Box, resting, beeNowButtons);
        var now = Panel("now", Margin(beeTasks, 8, 0, 4, 0));
        beeRecent = List();
        OnArg(beeRecent, "row-activated", row => recent[N.gtk_list_box_row_get_index(row)].Open());
        var right = Box(Vertical, 22, now, CardHistory(bee), Panel("recent work", Margin(Box(Vertical, 0, recentHead, beeRecent), 8, 0, 4, 0)));
        N.gtk_widget_set_size_request(right, 320, -1);
        N.gtk_widget_set_valign(right, Start);
        var columns = Box(Horizontal, 20, cardPanel, Clamp(right, 420));

        beeMolts = N.adw_bin_new();
        var page = Box(Vertical, 22, beeMolts, beeHeader, columns);
        SetPage(beeDetail, Margin(page, 24, 20, 20, 10));
        RefreshBeeMolts();
        RefreshTasks();
    }

    // "give a task" opens a box under the header: what to do, where, how
    // much it may do, and Start.
    nint TaskBox(Bee bee, out nint toggle)
    {
        var taskText = TextView("", editable: true);
        var choices = TaskChoices(bee, out var projectDrop, out var accessDrop);
        var revealer = N.gtk_revealer_new();
        var start = Button("Start", () =>
        {
            var p = (int)N.gtk_drop_down_get_selected(projectDrop);
            var task = Tasks.Create(store, bee.Name, GetText(taskText), p > 0 ? choices[p - 1].Name : "",
                                    Tasks.Access[N.gtk_drop_down_get_selected(accessDrop)]);
            Tasks.Start(task);
            SetText(taskText, "");
            N.gtk_revealer_set_reveal_child(revealer, false);
            Say($"{bee.Name} is on it. It runs in the background; the result shows up in the inbox.");
            RefreshTasks();
            RefreshHive();
        }, "suggested-action");
        var box = Box(Vertical, 10,
            Css(Label($"{bee.Name} works on it in the background as its own Claude session, in the project's folder, and saves what it finds to the hive. Closing this window doesn't stop it."), "small", "dim-label"),
            Framed(taskText, 80),
            Box(Horizontal, 8, projectDrop, accessDrop, Expand(N.gtk_box_new(Horizontal, 0), true, false), start));
        N.gtk_revealer_set_child(revealer, Margin(box, 4, 0, 0, 0));
        N.gtk_revealer_set_transition_type(revealer, 4); // slide down
        toggle = Button("give a task", () =>
        {
            var open = !N.gtk_revealer_get_reveal_child(revealer);
            N.gtk_revealer_set_reveal_child(revealer, open);
            if (open) N.gtk_widget_grab_focus(taskText);
        });
        return revealer;
    }

    // The card's editor and the bee's settings, for edit.
    nint BeeEditor(Bee bee, Editor card)
    {
        var specialty = EntryRow("Specialty", bee.Specialty);
        On(specialty, "apply", () => { store.UpdateBee(bee.Name, null, Text(specialty), null); Say("Specialty saved."); Refresh(); });
        N.adw_entry_row_set_show_apply_button(specialty, true);
        var beeProjects = EntryRow("Projects, separated by commas" + (projects.Count > 0 ? $" ({string.Join(", ", projects.Select(p => p.Name))})" : ""), string.Join(", ", bee.Projects));
        N.adw_entry_row_set_show_apply_button(beeProjects, true);
        On(beeProjects, "apply", () =>
        {
            store.UpdateBee(bee.Name, null, null, [.. Text(beeProjects).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)]);
            Say("Projects saved.");
            Refresh();
        });
        var beeSkills = EntryRow("Skills it starts with, separated by commas (like my-plugin:my-skill)", string.Join(", ", bee.Doc.GetList("skills")));
        N.adw_entry_row_set_show_apply_button(beeSkills, true);
        On(beeSkills, "apply", () =>
        {
            store.UpdateBee(bee.Name, null, null, null, [.. Text(beeSkills).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)]);
            Say("Skills saved. They load when its next session starts.");
            Refresh();
        });
        var beeRole = EntryRow($"Role: {string.Join(", ", Store.Roles)}, or empty", bee.Doc.Get("role"));
        N.adw_entry_row_set_show_apply_button(beeRole, true);
        On(beeRole, "apply", () =>
        {
            store.UpdateBee(bee.Name, null, null, null, role: Text(beeRole));
            Say(Text(beeRole).Trim().Length > 0 ? $"{bee.Name}'s role is {Text(beeRole).Trim().ToLowerInvariant()}." : $"{bee.Name} has no role.");
            Refresh();
        });
        N.gtk_widget_set_tooltip_text(beeRole, "reviewer and overseer make up the review team; evolver reviews the other bees' records, and its small rules apply at once; keeper may tidy the hive and set project briefs");
        var deposit = SwitchRow("Sent back once if it stops without saving", "When it saved nothing to the hive in a task or as a subagent", bee.Doc.Get("deposit_before_stop") == "yes");
        On(deposit, "notify::active", () =>
        {
            store.SetSetting(bee.Name, "deposit_before_stop", N.adw_switch_row_get_active(deposit) ? "yes" : "no");
            Refresh();
        });
        var molt = SwitchRow("May change its own card", "Off: card changes it proposes wait for you on this page", bee.MoltsFreely);
        On(molt, "notify::active", () =>
        {
            var free = N.adw_switch_row_get_active(molt);
            store.UpdateBee(bee.Name, free ? "free" : "approve", null, null);
            Say(free ? $"{bee.Name} may change its own card." : $"{bee.Name} now asks you first.");
            Refresh();
        });
        var saveCard = Button("save card", () =>
        {
            var text = card.Text;
            store.EditCard(bee.Name, text);
            card.Settle(text);
            Say("Card saved. The old one is kept in bees/.history.");
        }, "suggested-action");
        var retire = Button("Retire bee", () =>
        {
            store.RetireBee(bee.Name);
            Refresh();
            ShowBee(null);
            Undoable($"{bee.Name} is retired. What it saved stays in the hive.", () =>
            {
                store.RestoreBee(bee.Name);
                Refresh();
                ShowBee(bee.Name);
            });
        }, "destructive-action");
        N.gtk_widget_set_tooltip_text(retire, "Its card moves to bees/.history and its subagent is removed. What it saved stays in the hive.");
        var cardButtons = EndAligned(retire, card.KeepMine, saveCard);
        if (store.LastCardChange(bee.Name) is { } changed)
        {
            var undoCard = Button("Undo last change", () =>
            {
                var before = store.RequireBee(bee.Name).Doc.Body;
                store.UndoCard(bee.Name);
                ShowBeeAgain(bee.Name);
                Undoable($"{bee.Name}'s card is back to how it was before {changed}.", () =>
                {
                    store.EditCard(bee.Name, before);
                    ShowBeeAgain(bee.Name);
                });
            });
            N.gtk_widget_set_tooltip_text(undoCard, $"Its card goes back to how it was before {changed}. The current card is kept in bees/.history.");
            N.gtk_box_prepend(cardButtons, undoCard);
        }
        return Box(Vertical, 14,
            Css(Label("How it works and the rules it has learned. Claude reads this every time the bee starts."), "small", "dim-label"),
            Box(Vertical, 0, card.Banner, Framed(card.View, 260)),
            cardButtons,
            Css(Label("settings"), "caps"),
            Group("", null, specialty, beeProjects, beeSkills, beeRole, deposit, molt));
    }

    // How its card changed: each proposed change and what became of it, newest first.
    nint CardHistory(Bee bee)
    {
        var box = Box(Vertical, 0);
        var decided = store.CardDecisions(bee);
        for (int i = decided.Count - 1, n = 0; i >= 0 && n < 5; i--, n++)
        {
            var d = decided[i];
            var approved = d.Get("decided") == "approved";
            var by = d.Get("by", d.Get("bee"));
            var mark = Css(ArtLabel(approved ? "✓" : "✗"), "small", approved ? "good" : "dim-label");
            N.gtk_widget_set_valign(mark, Start);
            var created = d.Get("created");
            var when = created.Length >= 16 && created.StartsWith(DateTime.Now.ToString("yyyy-MM-dd")) ? created[11..16] : created;
            var text = Box(Vertical, 0, Css(Label(d.Get("reason")), "small"),
                Css(Label($"{(d.Get("applied") == "auto" ? "applied at once (rules backed by outcomes)" : approved ? "approved" : "rejected")} · from {(by == bee.Name ? bee.Name : by == "queen" ? "the Queen" : by)} · {when}"), "small", "faint"));
            N.gtk_box_append(box, Css(Box(Horizontal, 12, mark, Expand(text, true, false)), "card-change", n > 0 ? "row-sep" : ""));
        }
        if (decided.Count == 0)
            N.gtk_box_append(box, Margin(Css(Label(bee.Doc.Get("molted") is { Length: > 0 } m ? $"Last changed {m}, by you." : "Its card hasn't changed yet."), "small", "dim-label"), 6, 16, 10, 16));
        return Panel("card history", Margin(box, 8, 0, 4, 0));
    }

    // The project and access drop-downs for a bee's task, set to its own
    // project and access. Returns the projects the first one lists, which
    // stay what it lists even after a refresh replaces projects.
    List<Project> TaskChoices(Bee bee, out nint projectDrop, out nint accessDrop)
    {
        var choices = projects;
        (projectDrop, var projectModel) = StringDropDown("No project (home folder)");
        foreach (var p in choices) N.gtk_string_list_append(projectModel, Choice(p));
        var home = bee.Projects.Count > 0 ? choices.FindIndex(p => p.Name == bee.Projects[0]) : -1;
        N.gtk_drop_down_set_selected(projectDrop, (uint)(home + 1));
        (accessDrop, var accessModel) = StringDropDown(Tasks.AccessLabels[0]);
        foreach (var label in Tasks.AccessLabels.Skip(1)) N.gtk_string_list_append(accessModel, label);
        N.gtk_drop_down_set_selected(accessDrop, (uint)Math.Max(0, Array.IndexOf(Tasks.Access, bee.Doc.Get("access", "read"))));
        N.gtk_widget_set_tooltip_text(accessDrop, "Read and research: files, the web, the hive and read-only git. Can edit files and build: also changes files in the project's folder, uses skills, and builds, tests and runs it. Anything: no permission checks at all.");
        return choices;
    }

    // Builds the bee's page again (if it's the one shown), with its card as it is now.
    void ShowBeeAgain(string name)
    {
        if (selectedBee != name) return;
        selectedBee = null;
        ShowBee(name);
    }

    void RefreshTasks()
    {
        if (beeTasks == 0 || selectedBee == null || beeWorking == null || beeWaiting == null) return;
        var all = Tasks.List(store, selectedBee);
        // At work and waiting: as the scene sees them (with their live steps).
        var working = new List<Work>();
        var waiting = new List<Work>();
        foreach (var t in all)
        {
            if (!t.Running) continue;
            var w = work.Find(x => x.Task?.Id == t.Id) ?? new Work(t.Bee, TitleOf(t), When(t.Doc.Get("started", t.Doc.Get("created"))), t);
            (t.Status == "waiting" ? waiting : working).Add(w);
        }
        beeWorking.Update(working);
        beeWaiting.Update(waiting);
        N.gtk_widget_set_visible(workingHead, working.Count > 0 && waiting.Count > 0);
        N.gtk_widget_set_visible(waitingHead, waiting.Count > 0);
        // "resting" when it does nothing; open and stop for its first task at work.
        N.gtk_widget_set_visible(N.gtk_widget_get_prev_sibling(beeNowButtons), working.Count + waiting.Count == 0);
        var first = working.Count > 0 ? working[0].Task : waiting.Count > 0 ? waiting[0].Task : null;
        if ((first?.Id ?? "") != beeNowShown)
        {
            beeNowShown = first?.Id ?? "";
            Clear(beeNowButtons);
            if (first != null)
            {
                var id = first.Id;
                N.gtk_box_append(beeNowButtons, Button("open", () => ShowTask(id)));
                N.gtk_box_append(beeNowButtons, Button("stop", () =>
                {
                    Tasks.Stop(store, id);
                    Say("Stopped.");
                    RefreshHive();
                    RefreshTasks();
                }));
            }
        }
        // Recent work: its finished tasks and what it saved, newest first.
        tasks = all.Where(t => !t.Running).Take(8).ToList();
        // Newest first (kept in order as they're added: no sort over a new type).
        var items = new List<Recent>();
        void Add(Recent r)
        {
            var at = 0;
            while (at < items.Count && string.CompareOrdinal(items[at].When, r.When) >= 0) at++;
            items.Insert(at, r);
        }
        foreach (var t in tasks)
        {
            var finished = t.Doc.Get("finished");
            var (glyph, color) = t.Status switch { "done" => ("✓", Palette.Green), "stopped" => ("■", Palette.Dim), _ => ("✗", Palette.Red) };
            var id = t.Id;
            Add(new Recent(finished, t.Title, $"task · {t.Status}" + (t.Doc.Get("took") is { Length: > 0 } took ? $" in {took}" : "") + (t.Rewarded ? " · earned honey" : "") + " · " + HourOf(finished), glyph, color, () => ShowTask(id)));
        }
        foreach (var h in store.Search("", bee: selectedBee, limit: 8))
        {
            var id = h.Id;
            var (glyph, color) = TypeGlyph(h.Type);
            var meta = h.Type + (h.Project is "global" or "" ? "" : " · " + h.Project) + " · " + HourOf(h.Created);
            Add(new Recent(h.Created, h.Title, meta, glyph, color, () => OpenEntry(id)));
        }
        if (items.Count > 10) items.RemoveRange(10, items.Count - 10);
        recent = items;
        var keys = new List<string>();
        foreach (var r in items) keys.Add($"{r.When}\n{r.Title}\n{r.Meta}");
        SetRows(beeRecent, ref taskRows, keys, i =>
        {
            var r = recent[i];
            var mark = ArtLabel();
            N.gtk_label_set_markup(mark, Palette.Span(r.Color, r.Glyph));
            N.gtk_widget_set_valign(mark, Start);
            var row = Css(Box(Horizontal, 12, Css(mark, "small"), Expand(Box(Vertical, 0, Css(Label(r.Title), "small"), Css(Label(r.Meta), "small", "faint")), true, false)), "card-change");
            Said(row, $"{r.Title}, {r.Meta}");
            return row;
        });
        N.gtk_widget_set_visible(recentHead, false);
        N.gtk_widget_set_visible(beeRecent, items.Count > 0);
    }

    // "17:25" today, the date before.
    static string HourOf(string stamp) =>
        stamp.Length >= 16 && stamp.StartsWith(DateTime.Now.ToString("yyyy-MM-dd")) ? stamp[11..16] : stamp.Length >= 10 ? stamp[..10] : stamp;

    // An entry's type as a glyph and its colour (as Memory's list shows it).
    static (string Glyph, uint Color) TypeGlyph(string type) => type switch
    {
        "worklog" => ("▤", Palette.Dim), "lesson" => ("!", Palette.Pulse), "decision" => ("◆", Palette.Text), "fact" => ("●", Palette.Dim),
        "review" => ("✓", Palette.Green), "preference" => ("★", Palette.Pulse), _ => ("≡", Palette.Dim),
    };

    void NewBee()
    {
        var d = N.adw_alert_dialog_new("New bee", "Name it and give it a specialty. It joins the hive as a Claude subagent.");
        var name = EntryRow("Name", "");
        var specialty = EntryRow("Specialty", "");
        var beeProjects = EntryRow("Projects" + (projects.Count > 0 ? $" ({string.Join(", ", projects.Select(p => p.Name))})" : ""), "");
        var molt = SwitchRow("May change its own card", "Off: changes it proposes wait for you", false);
        var how = EntryRow("How it works (optional)", "");
        var group = Group("", null, name, specialty, beeProjects, molt, how);
        N.gtk_widget_set_size_request(group, 460, -1);
        var body = group;
        var framed = Framing(d, "new bee", body);
        N.adw_alert_dialog_add_response(d, "cancel", "Cancel");
        N.adw_alert_dialog_add_response(d, "create", "Create");
        N.adw_alert_dialog_set_response_appearance(d, "create", 1);
        N.adw_alert_dialog_set_default_response(d, "create");
        N.adw_alert_dialog_set_close_response(d, "cancel");
        N.adw_alert_dialog_set_response_enabled(d, "create", false);
        Action check = () => N.adw_alert_dialog_set_response_enabled(d, "create", Text(name).Trim().Length > 0 && Text(specialty).Trim().Length > 0);
        On(name, "changed", check);
        On(specialty, "changed", check);
        OnArg(d, "response", r =>
        {
            if (Str(r) != "create") return;
            List<string> chosen = [.. Text(beeProjects).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
            var free = N.adw_switch_row_get_active(molt);
            var bee = store.CreateBee(Text(name), Text(specialty), chosen, Text(how), free ? "free" : "approve");
            Say($"{bee.Name} joined the hive. Its subagent is ready in new Claude sessions.");
            Refresh();
            ShowBee(bee.Name);
            Go("bees");
        });
        Present(d, framed);
    }

    // ---- molts: card changes waiting for the user ---------------------------

    // They wait on their bee's page and in the Hive's inbox.
    List<(string Id, Doc Doc)> molts = [];
    nint beeMolts; // on the bee's page
    string? beeMoltsKey;

    bool WaitsFor(Doc molt, string bee) => molt.Get("bee").Equals(bee, StringComparison.OrdinalIgnoreCase);

    // The shown bee's waiting card changes, each as a lit panel with the diff
    // against its card now; rebuilt only when one of them or the card changes.
    void RefreshBeeMolts()
    {
        if (beeMolts == 0 || selectedBee == null) return;
        var card = bees.FirstOrDefault(b => b.Name == selectedBee)?.Doc.Body ?? "";
        var key = card;
        foreach (var (id, doc) in molts) if (WaitsFor(doc, selectedBee)) key += $"\n{id}\n{doc}";
        if (key == beeMoltsKey) return;
        beeMoltsKey = key;
        var box = Box(Vertical, 22);
        foreach (var (id, doc) in molts)
        {
            if (!WaitsFor(doc, selectedBee)) continue;
            var beeName = doc.Get("bee");
            var by = doc.Get("by", beeName);
            var inside = Box(Vertical, 10,
                Css(Label(by == beeName ? $"{beeName} wants to change its card" : $"{by} wants to change {beeName}'s card"), "bright"),
                Css(Label("Why: " + doc.Get("reason")), "small"),
                Framed(TextView(store.MoltDiff(doc), editable: false, mono: true)),
                EndAligned(
                    Button("Reject", () => DecideMolt(id, false, $"Rejected; {beeName}'s card stays as it was."), "destructive-action"),
                    Button("Approve", () => DecideMolt(id, true, $"{beeName}'s card is changed."), "suggested-action")));
            var panel = Panel("card change waiting", Margin(inside, 10, 20, 16, 20), "lit");
            PanelRight(panel, Ago(doc.Get("created")));
            N.gtk_box_append(box, panel);
        }
        N.adw_bin_set_child(beeMolts, box);
        N.gtk_widget_set_visible(beeMolts, key != card);
    }

    void DecideMolt(string id, bool approve, string said)
    {
        store.DecideMolt(id, approve);
        Refresh();
        Undoable(said, () =>
        {
            store.ReopenMolt(id);
            Refresh();
        });
    }
}
