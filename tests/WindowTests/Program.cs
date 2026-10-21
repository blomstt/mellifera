// Checks for the Hive window (src/Gui), run headless.
// Run: dotnet run --project tests/WindowTests [-v] [name filter]
// It starts its own gtk4-broadwayd (unless GDK_BACKEND=broadway is already
// set), then runs each check in a child process of its own: a fresh hive in a
// temp folder (absolute HIVE_HOME), the real App class with its window, and a
// second Store on the same folder standing in for another process (a bee, a
// Claude session, the CLI). HOME and HIVE_AGENTS_DIR point into the temp
// folder too, so nothing touches ~/Hive or ~/.claude.
//
// The checks drive the window through its own private methods and fields
// (reflection, no test hooks in src/) and read GTK widget state. Each prints
// PASS, FAIL (the behaviour is wrong) or ERROR (the setup didn't get where
// the check needs; that says nothing about the bug). The exit code is 1 if
// any check doesn't pass.
//
// "#n" in a name is the finding number in team review review rv-c
// (the window, stage 2). A "control" is the same setup without the bug's
// trigger, to show the harness sees what it should. "rv-b #n" is a finding
// of team review review rv-b (the terminal redesign).
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Hive;
using Hive.Gui;

// Tasks.Start runs `<this program> run-task <id>` in a new session; here that
// does nothing, so no claude ever starts.
if (args is ["run-task", ..]) return 0;
if (args is ["one", var only]) return Harness.RunOne(only);
// A dictation check runs the real App.Run in a process of its own (see VoiceApp).
if (args is ["voice-app", var how]) return Harness.VoiceApp(how);
return Harness.RunAll(args);

static class Harness
{
    static readonly List<(string Name, Action<Ctx> Run)> tests = [];
    static void Test(string name, Action<Ctx> run) => tests.Add((name, run));

    static Harness()
    {
        // ---- harness controls ----

        Test("control: an entry written while the window is open shows in the activity list", c =>
        {
            c.Start();
            c.WriteEntry("global", "outside-entry", "Written by another program");
            c.Expect(c.PumpUntil(() => c.ActivityHas("outside-entry")), "the new entry in the honeycomb's activity list");
        });

        // ---- #1 the first molt / task in a hive is missed ----

        Test("#1 control: a molt shows when bees/.molts already exists", c =>
        {
            Directory.CreateDirectory(c.Path("bees", ".molts"));
            c.Start();
            c.Outside.Molt("Worker", "a sharper card", "# Worker\n\nA new card.\n", "queen");
            c.Expect(c.PumpUntil(() => c.MoltBadge() == 1), $"the Molts badge to be 1, got {c.MoltBadge()}");
        });

        Test("#1 the first molt shows when bees/.molts is created for it", c =>
        {
            c.Pre(!Directory.Exists(c.Path("bees", ".molts")), "no bees/.molts yet");
            c.Start();
            c.Outside.Molt("Worker", "a sharper card", "# Worker\n\nA new card.\n", "queen");
            c.Expect(c.PumpUntil(() => c.MoltBadge() == 1), $"the Molts badge to be 1, got {c.MoltBadge()}");
        });

        Test("#1 control: a task shows when .tasks already exists", c =>
        {
            Directory.CreateDirectory(c.Path(".tasks"));
            c.Start();
            Tasks.Create(c.Outside, "Worker", "Look into it", "", "read");
            c.Expect(c.PumpUntil(() => c.Working("Worker")), "the honeycomb to show Worker as working…");
        });

        Test("#1 the first task shows when .tasks is created for it", c =>
        {
            c.Pre(!Directory.Exists(c.Path(".tasks")), "no .tasks yet");
            c.Start();
            Tasks.Create(c.Outside, "Worker", "Look into it", "", "read");
            c.Expect(c.PumpUntil(() => c.Working("Worker")), "the honeycomb to show Worker as working…");
        });

        // #2 (Changed() missing a change another read synced) lives in the
        // Store, so it is tested in tests/StoreTests.

        // ---- #3 the project drop-downs index into the live projects list ----

        Test("#3 a bee's task runs in the project picked, after a new project shifts the list", c =>
        {
            c.Start();
            c.Call("ShowBee", "Worker");
            c.Call("Go", "bees");
            c.Pump(300);
            var detail = c.Get<nint>("beeDetail");
            var drop = G.OfType(detail, "GtkDropDown").First();
            c.Pre(G.Items(drop) is [_, "alpha", "zeta"], "the bee's project drop-down to list alpha, zeta; got " + string.Join(", ", G.Items(drop)));
            N.gtk_drop_down_set_selected(drop, 2); // zeta
            c.Outside.SetProject("beta", "Beta project", null, null);
            c.Pre(c.PumpUntil(() => c.Get<List<Project>>("projects").Count == 3), "the window to pick up project beta");
            c.Click(G.Button(detail, "give a task")); // rebuilt to the mockups: the task box opens under the header
            Ui.SetText(G.OfType(detail, "GtkTextView").First(), "Do the thing");
            c.Pre(c.PumpUntil(() => N.gtk_widget_get_mapped(G.Button(detail, "Start")), 4000), "the task box open (broadway draws a frame a second)");
            c.Click(G.Button(detail, "Start"));
            var task = Tasks.List(c.Store, "Worker").FirstOrDefault();
            c.Pre(task != null, "Start to make a task; errors: " + string.Join(" | ", c.Errors));
            c.Expect(task!.Doc.Get("project") == "zeta", $"the task in project zeta, got \"{task.Doc.Get("project")}\"");
        });

        Test("#3 a review runs in the project picked, after a new project shifts the list", c =>
        {
            Reviews.SetUp(c.Store, new[] { "Cedar", "Birch", "Ash", "Arbiter" });
            c.Start();
            c.Call("Go", "reviews");
            c.Pump(300);
            var drop = c.Get<nint>("reviewProject");
            c.Pre(G.Items(drop) is [_, "alpha", "zeta"], "the review drop-down to list alpha, zeta; got " + string.Join(", ", G.Items(drop)));
            N.gtk_drop_down_set_selected(drop, 2); // zeta
            c.Outside.SetProject("beta", "Beta project", null, null);
            c.Pre(c.PumpUntil(() => c.Get<List<Project>>("projects").Count == 3), "the window to pick up project beta");
            var left = c.Get<nint>("reviewsLeft");
            Ui.SetText(G.OfType(left, "GtkTextView").First(), "src/");
            c.Click(G.Button(left, "Review ▸"));
            var run = Reviews.Runs(c.Store).FirstOrDefault();
            c.Pre(run != null, "Review to start a run; errors: " + string.Join(" | ", c.Errors));
            c.Expect(run!.Project == "zeta", $"the review in project zeta, got \"{run.Project}\"");
        });

        Test("#3 the Reviews drop-down lists a project made after the window opened", c =>
        {
            Reviews.SetUp(c.Store, new[] { "Cedar", "Birch", "Ash", "Arbiter" });
            c.Start();
            c.Outside.SetProject("beta", "Beta project", null, null);
            c.Pre(c.PumpUntil(() => c.Get<List<Project>>("projects").Count == 3), "the window to pick up project beta");
            var items = G.Items(c.Get<nint>("reviewProject"));
            c.Expect(items.Contains("beta"), "beta in the Reviews drop-down; it lists " + string.Join(", ", items));
        });

        // ---- #4 showing the same bee or project again drops unsaved edits ----
        // ShowBee is what activating the bee's row (and clicking its honeycomb
        // cell) calls; ShowProject is what activating the project's row calls.

        Test("#4 showing the same bee again keeps unsaved card edits", c =>
        {
            c.Start();
            c.Call("ShowBee", "Worker");
            var card = c.Get<Editor>("shownCard");
            Ui.SetText(card.View, card.Text + "\nMy unsaved rule.");
            c.Call("ShowBee", "Worker");
            var now = c.Get<Editor?>("shownCard");
            c.Expect(now != null && now.Text.Contains("My unsaved rule."), "the card editor to still hold the unsaved line; it has: " + now?.Text.Trim().Replace("\n", " / "));
        });

        Test("#4 showing the same project again keeps unsaved brief edits", c =>
        {
            c.Start();
            c.Call("ShowProject", "alpha");
            var brief = c.Get<Editor>("shownBrief");
            Ui.SetText(brief.View, brief.Text + "\nMy unsaved note.");
            c.Call("ShowProject", "alpha");
            var now = c.Get<Editor?>("shownBrief");
            c.Expect(now != null && now.Text.Contains("My unsaved note."), "the brief editor to still hold the unsaved line; it has: " + now?.Text.Trim().Replace("\n", " / "));
        });

        // ---- #5 a page rebuild resets each report's own scroll ----

        // Where a long text view on a detail page is scrolled to, measured as the
        // first of its lines that shows in the pane, so it holds whether the
        // view scrolls in its own frame (today) or with the page (the fix the
        // review suggests).
        // Allocation follows the frame clock, which is irregular headless, so
        // positions are read once the layout has caught up (or the time is up).
        void KeepsTopLine(Ctx c, string page, string pane, Action change)
        {
            c.Call("Go", page);
            var detail = c.Get<nint>(pane);
            nint View() => G.OfType(detail, "GtkTextView").FirstOrDefault();
            c.Pre(c.PumpUntil(() => View() != 0 && G.ScrollAround(View(), 600)), "the text to be taller than what shows of it");
            var view = View();
            var line = -1;
            c.Pre(c.PumpUntil(() => (line = G.TopLine(view, detail)) > 3), $"the text scrolled down a few lines, at line {line}");
            change();
            c.Pre(c.PumpUntil(() => View() != view), "the page to be rebuilt");
            var now = -1;
            c.Expect(c.PumpUntil(() => (now = G.TopLine(View(), detail)) == line, 3000), $"the text still at line {line} after the rebuild, got line {now}");
        }

        Test("#5 a molt page rebuild keeps the diff where it was scrolled", c =>
        {
            var lines = string.Join("\n", Enumerable.Range(1, 150).Select(i => $"- rule {i}: something the bee learned"));
            c.Store.EditCard("Worker", "# Worker\n\n" + lines);
            c.Store.Molt("Worker", "a sharper card", "# Worker\n\n" + lines.Replace("learned", "knows"), "queen");
            c.Start();
            c.Call("ShowBee", "Worker"); // since stage 6, molts wait on their bee's page (the Molts page is gone)
            KeepsTopLine(c, "bees", "beeDetail", () => c.Outside.EditCard("Worker", "# Worker\n\n" + lines + "\n- one more rule"));
        });

        // Rebuilt to the mockups: the report is rendered (no text view of its
        // own), so the review page keeps the scroll of the pane it's in.
        Test("#5 a review page rebuild keeps the Arbiter's report where it was scrolled", c =>
        {
            Reviews.SetUp(c.Store, new[] { "Cedar", "Birch", "Ash", "Arbiter" });
            const string run = "review-20261018-120000";
            var reviewer = Tasks.Create(c.Store, "Cedar", "Review src/", "", "read", run: run, role: "reviewer");
            reviewer.Doc.Set("status", "running");
            reviewer.Doc.Set("pid", Runner(reviewer.Id));
            reviewer.Doc.Save(reviewer.Path);
            var overseer = Tasks.Create(c.Store, "Arbiter", "Oversee", "", "read", run: run, role: "overseer");
            overseer.Doc.Set("status", "done");
            overseer.Doc.Save(overseer.Path);
            File.WriteAllText(overseer.ResultPath, string.Join("\n", Enumerable.Range(1, 150).Select(i => $"{i}. **[bug low]** finding {i}")) + "\n");
            c.Start();
            c.Call("ShowRun", run);
            c.Call("Go", "reviews");
            var pane = c.Get<nint>("runDetail");
            var adjustment = N.gtk_scrolled_window_get_vadjustment(pane);
            // The page, in the viewport the pane keeps.
            nint Page() => G.gtk_widget_get_first_child(G.gtk_scrolled_window_get_child(pane));
            var page = Page();
            c.Pre(c.PumpUntil(() => G.ScrollAround(Page(), 600)), "the report taller than the pane, scrolled down");
            page = Page();
            var before = N.gtk_adjustment_get_value(adjustment);
            reviewer.Doc.Set("status", "done");
            File.WriteAllText(reviewer.ResultPath, "Cedar's report.\n");
            reviewer.Doc.Save(reviewer.Path);
            c.Pre(c.PumpUntil(() => Page() != page), "the page to be rebuilt; errors: " + string.Join(" | ", c.Errors));
            var after = 0.0;
            c.Expect(c.PumpUntil(() => Math.Abs((after = N.gtk_adjustment_get_value(adjustment)) - before) < 1, 3000), $"the pane still scrolled to {before}, got {after}");
        });

        // #6 (a tick that throws runs again) went with Ui.Timer: bloat 2 made
        // After(ms, Action) one-shot, so a tick can't run again at all.

        // ---- #7 a check that throws loses its change ----

        Test("#7 a check that fails (the index is locked) is tried again, so the new entry still shows", c =>
        {
            c.Start();
            var other = new Db(c.Path(".index.db"));
            other.Exec("BEGIN IMMEDIATE");
            var locked = true;
            c.OnError = _ =>
            {
                if (!locked) return;
                other.Exec("ROLLBACK");
                other.Dispose();
                locked = false;
            };
            c.WriteEntry("global", "while-locked", "Written while the index was locked");
            c.Pre(c.PumpUntil(() => !locked, 9000), "the window's check to fail on the locked index (5 s busy timeout)");
            c.Expect(c.PumpUntil(() => c.ActivityHas("while-locked")), "the entry in the activity list once the index is free; it never shows");
        });

        // ---- #8 Directory.EnumerateDirectories throws in WatchFolder ----
        // An unreadable folder makes the same call throw that a folder removed
        // right after it was made does. Under .tasks, Sync (which skips dot
        // folders) never reads it, so only WatchFolder can be the one throwing.

        Test("#8 a new unreadable folder doesn't make the window toast an error", c =>
        {
            Directory.CreateDirectory(c.Path(".tasks"));
            c.Start();
            var odd = c.Path(".tasks", "odd");
            Directory.CreateDirectory(odd);
            File.SetUnixFileMode(odd, UnixFileMode.UserExecute);
            c.Pump(1500);
            c.Expect(c.Errors.Count == 0, "no error; got: " + string.Join(" | ", c.Errors));
        });

        Test("#8 an unreadable folder at start doesn't stop the window from showing", c =>
        {
            var odd = c.Path(".tasks", "odd");
            Directory.CreateDirectory(odd);
            File.SetUnixFileMode(odd, UnixFileMode.UserExecute);
            var threw = c.Start(mayThrow: true);
            c.Expect(G.gtk_widget_get_visible(c.Get<nint>("window")) != 0, "the window to be presented; Activate threw: " + threw?.Message);
        });

        // ---- #9 DELETED unwatches only the folder itself ----

        Test("#9 a folder made again where a renamed folder's subfolder was is watched", c =>
        {
            Directory.CreateDirectory(c.Path("comb", "alpha", "a", "sub"));
            c.Start();
            Directory.Move(c.Path("comb", "alpha", "a"), c.Path("comb", "alpha", "b"));
            c.Pump(800);
            Directory.CreateDirectory(c.Path("comb", "alpha", "a"));
            c.Pump(800);
            Directory.CreateDirectory(c.Path("comb", "alpha", "a", "sub"));
            c.Pump(800);
            c.WriteEntry("alpha/a/sub", "in-the-new-sub", "Written in the folder made again");
            var stale = c.Get<Dictionary<string, long>>("watches").Keys.Where(k => k.StartsWith(c.Path("comb", "alpha", "a") + "/")).ToList();
            c.Expect(c.PumpUntil(() => c.ActivityHas("in-the-new-sub"), 6000),
                     "the entry in the activity list; it never shows (watched paths under the old name: " + string.Join(", ", stale) + ")");
        });

        // ---- #10 honey given from outside doesn't reach the Bees list ----

        // Rebuilt to the mockups: a bee's honey is in the strip under its page's header.
        Test("#10 honey given from the CLI shows on the bee's page", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            task.Doc.Set("status", "done");
            task.Doc.Set("finished", Text.Now);
            task.Doc.Save(task.Path);
            c.Start();
            c.Call("ShowBee", "Worker");
            var stats = c.Get<nint>("beeStats");
            c.Pre(G.Text(stats).Contains("0 honey"), "no honey on Worker's page yet; it says " + G.Text(stats));
            Tasks.Reward(c.Outside, task.Id, "", true);
            c.Pre(c.PumpUntil(() => c.Get<Dictionary<string, int>>("honey").GetValueOrDefault("worker") == 1), "the window to count the honey (RefreshHive ran)");
            c.Pump(100);
            c.Expect(G.Text(stats).Contains("1 honey"), "\"1 honey\" on Worker's page; it says: " + G.Text(stats));
        });

        // ---- #11 a new project resets the Memory project filter ----

        Test("#11 the Memory project filter stays on its project when another project is made", c =>
        {
            c.Start();
            var drop = c.Get<nint>("projectDrop");
            N.gtk_drop_down_set_selected(drop, (uint)c.Get<List<string>>("projectChoices").IndexOf("alpha") + 1);
            c.Pump(200);
            c.Outside.SetProject("beta", "Beta project", null, null);
            c.Pre(c.PumpUntil(() => c.Get<List<string>>("projectChoices").Contains("beta")), "the window to pick up project beta");
            var selected = N.gtk_drop_down_get_selected(drop);
            var now = selected == 0 ? "All projects" : c.Get<List<string>>("projectChoices")[(int)selected - 1];
            c.Expect(now == "alpha", $"the filter still on alpha, got {now}");
        });

        // ---- #12 an open page whose project or review is gone stays live ----

        Test("#12 the project page goes away when its project is removed", c =>
        {
            c.Start();
            c.Call("ShowProject", "alpha");
            var detail = c.Get<nint>("projectDetail");
            c.Pre(G.Button(detail, "save") != 0, "alpha's page with its save button");
            Directory.Delete(c.Path("projects", "alpha"), true);
            c.Pre(c.PumpUntil(() => !c.Get<List<Project>>("projects").Any(p => p.Name == "alpha")), "the window to see alpha is gone");
            c.Pump(100);
            c.Expect(G.Button(detail, "save") != 0 && c.Get<Editor?>("shownBrief") == null, "alpha's page gone (the Queen's, with its own save, shows)");
        });

        Test("#12 the review page goes away when its run is removed", c =>
        {
            Reviews.SetUp(c.Store, new[] { "Cedar", "Birch", "Ash", "Arbiter" });
            var task = Tasks.Create(c.Store, "Cedar", "Review src/", "", "read", run: "review-20261018-120000", role: "reviewer");
            task.Doc.Set("status", "done");
            task.Doc.Set("focus", "src/");
            task.Doc.Save(task.Path);
            File.WriteAllText(task.ResultPath, "One finding.\n");
            c.Start();
            c.Call("ShowRun", "review-20261018-120000");
            var detail = c.Get<nint>("runDetail");
            c.Pre(G.Button(detail, "Cedar") != 0, "the review's page with Cedar's honey button");
            File.Delete(task.Path);
            File.Delete(task.ResultPath);
            c.Pre(c.PumpUntil(() => c.Get<List<ReviewRun>>("runs").Count == 0), "the window to see the run is gone");
            c.Pump(100);
            c.Expect(G.Button(detail, "Cedar") == 0, "the review's page (and its honey button) gone");
        });

        // ---- #13 an unedited editor taking the disk's text loses its place ----

        void BriefChangesOnDisk(Ctx c, Action<nint> before, Action<nint> after)
        {
            var brief = "# alpha\n\n" + string.Join("\n", Enumerable.Range(1, 200).Select(i => $"Line {i} of the brief, long enough to need scrolling."));
            c.Store.SetProject("alpha", null, null, brief);
            c.Start();
            c.Call("ShowProject", "alpha");
            c.Call("Go", "briefs"); // the Projects page became Briefs in stage 6
            var editor = c.Get<Editor>("shownBrief");
            before(editor.View);
            c.Outside.SetProject("alpha", null, null, brief + "\nA line added on disk.");
            c.Pre(c.PumpUntil(() => editor.Text.Contains("A line added on disk.")), "the unedited brief to take the text from disk");
            after(editor.View);
        }

        Test("#13 an unedited brief that takes the disk's text stays where it was scrolled", c =>
        {
            var line = -1;
            BriefChangesOnDisk(c,
                view =>
                {
                    c.Pre(c.PumpUntil(() => G.ScrollAround(view, 600)), "the brief to be taller than what shows of it");
                    c.Pre(c.PumpUntil(() => (line = G.TopLine(view, c.Get<nint>("projectDetail"))) > 3), $"the brief scrolled down a few lines, at line {line}");
                },
                view =>
                {
                    var now = -1;
                    c.Expect(c.PumpUntil(() => (now = G.TopLine(view, c.Get<nint>("projectDetail"))) == line, 3000), $"the brief still at line {line}, got line {now}");
                });
        });

        // The review's suggested fix (keep the vadjustment) covers the scroll
        // only; this one needs the cursor kept as well.
        Test("#13 an unedited brief that takes the disk's text keeps its cursor", c =>
            BriefChangesOnDisk(c,
                view => G.PlaceCursor(view, 1500),
                view =>
                {
                    c.Pump(300);
                    c.Expect(G.Cursor(view) == 1500, $"the cursor still at offset 1500, got {G.Cursor(view)}");
                }));

        // ---- #14 taskSignature is read before Tasks.List writes .tasks ----

        Test("#14 after a refresh, the window's task signature matches .tasks", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            task.Doc.Set("status", "running");
            task.Doc.Set("pid", DeadPid().ToString());
            task.Doc.Save(task.Path);
            Thread.Sleep(20);
            c.Start(pumpMs: 0); // before any focus check could fix it up
            c.Pre(Tasks.Load(c.Store, task.Id).Status == "stopped", "the window's Tasks.List to mark the dead task stopped");
            c.Expect(c.Get<long>("taskSignature") == Tasks.Signature(c.Store),
                     "taskSignature to be read after Tasks.List wrote .tasks (otherwise the next focus check refreshes again)");
        });

        UndoTests();

        // ---- stage 6: five tabs and the narrow layout ----

        // Work pane wp1: the pane (┤ ♛ queen ├), not the old at-work/inbox
        // column, goes into the bottom sheet, with "♛ queen" on its bar.
        Test("narrow: under 900sp the lists collapse, the tabs move to the bottom, the work pane goes into the sheet with ♛ queen on its bar, and back", c =>
        {
            c.Start();
            var window = c.Get<nint>("window");
            var (pane, split, sheet) = (c.Get<nint>("pane"), c.Get<nint>("hiveSplit"), c.Get<nint>("hiveSheet"));
            c.Pre(G.Inside(pane, split), "the pane in the Hive page's split to start");
            N.gtk_window_set_default_size(window, 800, 700);
            c.Pre(c.PumpUntil(() => c.Get<bool>("narrow"), 4000), "the breakpoint to apply at 800 px wide");
            c.Expect(G.adw_navigation_split_view_get_collapsed(c.Get<nint>("beeSplit")) != 0, "the Bees split view collapsed");
            c.Expect(G.gtk_widget_get_visible(c.Get<nint>("switcher")) == 0, "the header's view switcher hidden");
            c.Expect(!G.Inside(pane, split) && G.Inside(pane, sheet) && G.adw_bottom_sheet_get_sheet(sheet) == pane, "the pane out of the split, as the bottom sheet's sheet");
            var bar = G.adw_bottom_sheet_get_bottom_bar(sheet);
            c.Expect(bar == c.Get<nint>("sheetBar") && G.Text(bar) == "┤ ♛ queen ├", "the sheet's bar says ┤ ♛ queen ├; it says " + (bar != 0 ? G.Text(bar) : "nothing (no bar)"));
            c.Call("ShowBee", "Worker");
            c.Call("Go", "bees");
            N.adw_navigation_split_view_set_show_content(c.Get<nint>("beeSplit"), true); // as activating its row does
            c.Expect(c.PumpUntil(() => G.gtk_widget_get_visible(c.Get<nint>("back")) != 0), "a Back button in the header while the bee's page shows");
            N.gtk_window_set_default_size(window, 1280, 820);
            c.Pre(c.PumpUntil(() => !c.Get<bool>("narrow"), 4000), "the breakpoint to unapply at 1280 px wide");
            c.Expect(G.adw_navigation_split_view_get_collapsed(c.Get<nint>("beeSplit")) == 0 && G.gtk_widget_get_visible(c.Get<nint>("back")) == 0
                     && G.Inside(pane, split) && G.adw_bottom_sheet_get_sheet(sheet) == 0 && G.adw_bottom_sheet_get_bottom_bar(sheet) == 0, "the wide layout back, with the pane in the split and no sheet");
            c.Call("Go", "hive");
            c.Expect(c.PumpUntil(() => N.gtk_widget_get_mapped(pane) && G.gtk_widget_get_width(pane) >= 380, 4000), $"the pane shown beside the column again, at least 380 px; it's {G.gtk_widget_get_width(pane)} px");
        });

        // ---- stage 8: the stage, the inbox, the Timeline and the popover ----

        // Terminal redesign: the glows went with the cairo honeycomb. These
        // three checks were "one glow shown", "a working cell's glow pulses"
        // and "…is steady"; they now check the bee sprite that replaced the glow.
        Test("scene: a running task sends its bee out to work on the frame clock, which stops once it's home", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            task.Doc.Set("status", "running");
            task.Doc.Set("pid", Runner(task.Id));
            task.Doc.Set("started", Text.Now);
            task.Doc.Save(task.Path);
            c.Start();
            c.Expect(c.Working("Worker") && c.Get<bool>("moving") && Ui.Ticking, "Worker working and the scene on the frame clock");
            c.Expect(G.Text(c.Get<nint>("atWork")) == "1 at work", "\"1 at work\"; it says: " + G.Text(c.Get<nint>("atWork")));
            c.Expect(c.SpriteState("Worker") is "work" or "think" && c.AwayFromHome("Worker"), $"Worker's sprite out of its cell, at work; it's {c.SpriteState("Worker")}");
            Tasks.Record(c.Outside, Tasks.Load(c.Outside, task.Id), "Done.", "done");
            c.Expect(c.PumpUntil(() => !c.Get<bool>("moving") && !c.Working("Worker"), 8000), "the scene still once Worker delivered its result and flew home");
            c.Expect(c.SpriteState("Worker") == "rest" && !c.AwayFromHome("Worker"), $"Worker resting in its cell; it's {c.SpriteState("Worker")}");
            c.Expect(G.Text(c.Get<nint>("atWork")) == "", "no \"at work\" counter");
        });

        // The scene drawn at chosen times (DrawAll), as the frame clock would.
        // Rebuilt to the mockups: the sprite is ʚ●ɞ and doesn't flap; it bobs.
        void Flaps(Ctx c, bool animations, Action<bool, HashSet<string>, HashSet<double>> check)
        {
            G.g_object_set(G.gtk_settings_get_default(), "gtk-enable-animations", animations ? 1 : 0, 0);
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            task.Doc.Set("status", "running");
            task.Doc.Set("pid", Runner(task.Id));
            task.Doc.Save(task.Path);
            File.WriteAllText(task.StepsPath, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\tEdit\tsrc/A.cs\n");
            c.Start(pumpMs: 3000); // its flight out is over
            var t0 = Ui.Clock + 5;
            HashSet<string> wings = [];
            HashSet<double> heights = [];
            for (int i = 0; i < 12; i++)
            {
                c.Call("DrawAll", t0 + i * 0.07);
                wings.Add(c.SpriteText("Worker", "Body"));
                heights.Add(Math.Round(c.SpriteY("Worker"), 2));
            }
            check(Ui.Smooth, wings, heights);
        }

        Test("scene control: with animations on, a working bee bobs at its station", c => Flaps(c, true, (smooth, sprite, heights) =>
            c.Expect(smooth && sprite.SetEquals(["ʚ●ɞ"]) && heights.Count > 3, $"the sprite ʚ●ɞ at changing heights; saw {string.Join("|", sprite)} and {heights.Count} heights")));

        Test("scene: with animations off, a working bee doesn't bob", c => Flaps(c, false, (smooth, sprite, heights) =>
            c.Expect(!smooth && sprite.SetEquals(["ʚ●ɞ"]) && heights.Count == 1, $"the sprite ʚ●ɞ at one height; saw {string.Join("|", sprite)} and {heights.Count} heights")));

        Test("scene: a failed task's bee lies under its station until its inbox item is dismissed", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            task.Doc.Set("status", "running");
            task.Doc.Set("pid", Runner(task.Id));
            task.Doc.Save(task.Path);
            File.WriteAllText(task.StepsPath, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\tRead\tsrc/A.cs\n");
            c.Start(pumpMs: 2500);
            Tasks.Record(c.Outside, Tasks.Load(c.Outside, task.Id), "It broke.", "failed");
            c.Expect(c.PumpUntil(() => c.SpriteState("Worker") == "fail" && !c.Get<bool>("moving"), 8000), $"Worker down after its fall, the scene still; it's {c.SpriteState("Worker")}");
            c.Expect(c.SpriteText("Worker", "Prop") == "✗" && c.AwayFromHome("Worker"), "a ✗ beside it, out of its cell");
            c.Call("Dismiss", "task:" + task.Id);
            c.Expect(c.PumpUntil(() => c.SpriteState("Worker") == "rest" && !c.Get<bool>("moving"), 8000) && !c.AwayFromHome("Worker"), $"Worker home once the item is dismissed; it's {c.SpriteState("Worker")}");
        });

        Test("scene: a reviewer's scroll lands on the Arbiter's stack", c =>
        {
            Reviews.SetUp(c.Store, new[] { "Cedar", "Birch", "Ash", "Arbiter" });
            var r1 = Tasks.Create(c.Store, "Cedar", "Review src/", "", "read", run: "review-20261018-120000", role: "reviewer");
            var r2 = Tasks.Create(c.Store, "Birch", "Review src/", "", "read", run: "review-20261018-120000", role: "reviewer");
            foreach (var r in new[] { r1, r2 })
            {
                r.Doc.Set("status", "running");
                r.Doc.Set("pid", Runner(r.Id));
                r.Doc.Save(r.Path);
            }
            var overseer = Tasks.Create(c.Store, "Arbiter", "Oversee", "", "read", run: "review-20261018-120000", role: "overseer", waitsFor: [r1.Id, r2.Id]);
            overseer.Doc.Set("status", "waiting");
            overseer.Doc.Set("pid", Runner(overseer.Id));
            overseer.Doc.Save(overseer.Path);
            c.Start(pumpMs: 2500);
            c.Pre(c.SpriteText("Arbiter", "Prop") == "≡ 0/2", "the stack at 0/2; it says " + c.SpriteText("Arbiter", "Prop"));
            Tasks.Record(c.Outside, Tasks.Load(c.Outside, r1.Id), "Findings.", "done");
            c.Expect(c.PumpUntil(() => c.SpriteText("Arbiter", "Prop") == "≡ 1/2", 8000), "the stack at 1/2 once Cedar's scroll landed; it says " + c.SpriteText("Arbiter", "Prop"));
        });

        Test("scene: once the reviewers are done the Arbiter flies into the library", c =>
        {
            Reviews.SetUp(c.Store, new[] { "Cedar", "Birch", "Ash", "Arbiter" });
            var r = Tasks.Create(c.Store, "Cedar", "Review src/", "", "read", run: "review-20261018-120000", role: "reviewer");
            r.Doc.Set("status", "running");
            r.Doc.Set("pid", Runner(r.Id));
            r.Doc.Save(r.Path);
            var o = Tasks.Create(c.Store, "Arbiter", "Oversee", "", "read", run: "review-20261018-120000", role: "overseer", waitsFor: [r.Id]);
            o.Doc.Set("status", "waiting");
            o.Doc.Set("pid", Runner(o.Id));
            o.Doc.Save(o.Path);
            c.Start(pumpMs: 2500);
            c.Pre(c.SpriteState("Arbiter") == "wait", "the Arbiter waiting at its perch");
            Tasks.Record(c.Outside, Tasks.Load(c.Outside, r.Id), "Findings.", "done");
            c.Expect(c.PumpUntil(() => c.SpriteText("Arbiter", "Prop") == "≡ 1/1", 8000), "the stack at 1/1");
            var running = Tasks.Load(c.Outside, o.Id);
            running.Doc.Set("status", "running");
            running.Doc.Set("started", Text.Now);
            running.Doc.Save(running.Path);
            c.Expect(c.PumpUntil(() => c.SpriteState("Arbiter") is "think" or "work" && (string)c.Field(c.SpriteObject("Arbiter"), "Station")! == Steps.Library), "the Arbiter at the library; it's " + c.SpriteState("Arbiter"));
        });

        Test("scene: with animations off, a bee jumps to its station instead of flying", c =>
        {
            G.g_object_set(G.gtk_settings_get_default(), "gtk-enable-animations", 0, 0);
            c.Start();
            var task = Tasks.Create(c.Outside, "Worker", "Look into it", "", "read");
            task.Doc.Set("status", "running");
            task.Doc.Set("pid", Runner(task.Id));
            task.Doc.Save(task.Path);
            c.Expect(c.PumpUntil(() => c.AwayFromHome("Worker")), "Worker out of its cell");
            c.Expect(!c.SpriteFlying("Worker"), "there, not on its way");
        });

        Test("stage: no timer at all in a quiet hive", c =>
        {
            c.Start();
            c.Expect(!c.Get<bool>("moving"), "the stage's timer not running");
        });

        Test("stage: an Arbiter waiting on a running reviewer is a link with flowing dots", c =>
        {
            Reviews.SetUp(c.Store, new[] { "Cedar", "Birch", "Ash", "Arbiter" });
            var reviewer = Tasks.Create(c.Store, "Cedar", "Review src/", "", "read", run: "review-20261018-120000", role: "reviewer");
            reviewer.Doc.Set("status", "running");
            reviewer.Doc.Set("pid", Runner(reviewer.Id));
            reviewer.Doc.Save(reviewer.Path);
            var overseer = Tasks.Create(c.Store, "Arbiter", "Oversee", "", "read", run: "review-20261018-120000", role: "overseer", waitsFor: [reviewer.Id]);
            overseer.Doc.Set("status", "waiting");
            overseer.Doc.Set("pid", Runner(overseer.Id));
            overseer.Doc.Save(overseer.Path);
            c.Start();
            c.Expect(c.Get<System.Collections.IList>("links").Count == 1, "one link");
            // Rebuilt to the mockups: the Arbiter waits in the library with its stack (the scene draws no reviewer-to-Arbiter path).
            c.Expect(c.SpriteState("Arbiter") == "wait" && c.SpriteText("Arbiter", "Prop") == "≡ 0/1", $"the Arbiter waiting with its stack at 0/1; it says \"{c.SpriteText("Arbiter", "Prop")}\"");
            c.Expect((string)c.Field(c.SpriteObject("Arbiter"), "Station")! == "library", "the Arbiter waiting at the library");
            c.Expect(G.Text(c.Get<nint>("atWork")) == "1 at work", "only the reviewer at work (the Arbiter waits); it says: " + G.Text(c.Get<nint>("atWork")));
        });

        Test("stage: a subagent's .state file lights its bee", c =>
        {
            Directory.CreateDirectory(c.Path(".state"));
            c.Start();
            File.WriteAllText(c.Path(".state", "agent-1"), $"Worker\n{DateTime.UtcNow.Ticks}\n");
            c.Expect(c.PumpUntil(() => c.Get<bool>("moving")), "the timer running");
            c.Expect(c.Sub("Worker").StartsWith("in a session"), "Worker's cell says it's in a session; it says: " + c.Sub("Worker"));
            File.Delete(c.Path(".state", "agent-1"));
            c.Expect(c.PumpUntil(() => !c.Get<bool>("moving")), "the timer stopped once the subagent stopped");
        });

        Test("stage: each cell has a label for screen readers, with the same tooltip", c =>
        {
            c.Start();
            var labels = c.Get<List<long>>("cellLabels").Select(l => (nint)l).Where(l => G.gtk_widget_get_visible(l) != 0).ToList();
            c.Expect(labels.Count == 3, $"3 labels (the Queen, Worker, new bee), got {labels.Count}");
            c.Expect(labels.Any(l => G.Tooltip(l).StartsWith("Worker, ")), "one with the tooltip \"Worker, …\"; tooltips: " + string.Join(" | ", labels.Select(G.Tooltip)));
            c.Expect(labels.All(l => G.gtk_test_accessible_has_property(l, 4) != 0), "each with an accessible label");
        });

        // Rebuilt to the mockups: a bee's cell opens its talk; a line in "at work" still opens the popover.
        Test("popover: a bee's popover has a task box", c =>
        {
            c.Start();
            c.Call("ShowBeePop", "Worker");
            var popover = c.Get<nint>("popover");
            c.Expect(c.PumpUntil(() => G.gtk_widget_get_visible(popover) != 0), "the popover shown");
            c.Expect(G.OfType(popover, "GtkEntry").Any() && G.Button(popover, "Start") != 0 && G.Button(popover, "Open page") != 0, "a task box, Start and Open page");
        });

        Test("inbox: a finished task waits with Give honey; Dismiss takes it off without touching the task", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            Tasks.Record(c.Store, task, "Found it.", "done");
            c.Start();
            c.Expect(c.Inbox() == 1 && c.HiveBadge() == 1, $"one item in the inbox and the badge 1; got {c.Inbox()} and {c.HiveBadge()}");
            var row = c.Get<nint>("inbox");
            c.Expect(G.Button(row, "Give honey") != 0, "a Give honey button");
            c.Call("Dismiss", "task:" + task.Id);
            c.Expect(c.Inbox() == 0 && c.HiveBadge() == 0, "the inbox empty and no badge");
            var doc = Tasks.Load(c.Store, task.Id).Doc;
            c.Expect(doc.Get("reward") == "" && doc.Get("status") == "done", "the task as it was: done, no reward set either way");
        });

        Test("timeline: a task finished in the last 3 hours has a lane and a bar", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            task.Doc.Set("started", Text.Now);
            task.Doc.Save(task.Path);
            Tasks.Record(c.Store, Tasks.Load(c.Store, task.Id), "Found it.", "done");
            c.Start();
            c.Expect(c.Get<List<string>>("lanes") is ["Worker"], "one lane, Worker's; lanes: " + string.Join(", ", c.Get<List<string>>("lanes")));
            c.Expect(c.Get<System.Collections.IList>("marks").Count >= 1, "a bar for the task");
        });

        // ---- terminal redesign, step 2: the skin and the status line ----

        Test("motion: a quiet window has no frame-clock tick at rest", c =>
        {
            c.Start(pumpMs: 1500);
            c.Expect(!Ui.Ticking && Ui.Moving == 0, $"no tick callback and nothing moving; ticking {Ui.Ticking}, {Ui.Moving} steps");
        });

        Test("status line: an error types in red with ✗, and the tick stops after", c =>
        {
            c.Start();
            Ui.OnError!("The index is locked.");
            var status = c.Get<StatusLine>("status");
            c.Expect(c.PumpUntil(() => G.Text(status.Message) == "✗ The index is locked.", 4000), "the message typed out in full; it says: " + G.Text(status.Message));
            c.Expect(G.gtk_widget_has_css_class(status.Message, "error") != 0, "the message styled as an error");
            c.Expect(c.PumpUntil(() => !Ui.Ticking, 4000), "the tick callback gone once the typing and the shake are done");
        });

        Test("status line: Undo counts down, and is gone when its 10 s are up", c =>
        {
            c.Start();
            var undone = false;
            c.Call("Undoable", "Done.", (Action)(() => undone = true));
            var status = c.Get<StatusLine>("status");
            c.Expect(G.gtk_widget_get_visible(status.UndoButton) != 0 && G.gtk_widget_get_visible(status.Countdown) != 0, "Undo and its countdown shown");
            c.Expect(c.PumpUntil(() => G.gtk_widget_get_visible(status.UndoButton) == 0, 13000), "Undo gone after 10 s");
            c.Expect(!undone && c.PumpUntil(() => !Ui.Ticking, 3000), "nothing undone, and the tick stopped");
        });

        // ---- terminal redesign, step 3 (stage 7): live steps and presence ----

        Test("live steps: a task's step log says what its bee is doing, and a new step follows", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            task.Doc.Set("status", "running");
            task.Doc.Set("pid", Runner(task.Id));
            task.Doc.Save(task.Path);
            File.WriteAllText(task.StepsPath, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\tEdit\tsrc/A.cs\n");
            c.Start();
            c.Expect(c.Sub("Worker").StartsWith("editing src/A.cs"), "Worker editing src/A.cs; it says: " + c.Sub("Worker"));
            File.AppendAllText(task.StepsPath, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\tRead\tsrc/B.cs\n");
            c.Expect(c.PumpUntil(() => c.Sub("Worker").StartsWith("reading src/B.cs")), "Worker reading src/B.cs after the new step; it says: " + c.Sub("Worker"));
        });

        Test("presence: a chat with a bee (.talk) shows it talking with you, and ends with its file", c =>
        {
            c.Start();
            Directory.CreateDirectory(c.Path(".talk"));
            File.WriteAllText(c.Path(".talk", "chat-1"), $"Worker\n{DateTime.UtcNow.Ticks}\n");
            c.Expect(c.PumpUntil(() => c.Sub("Worker").StartsWith("talking with you")), "Worker talking with you; it says: " + c.Sub("Worker"));
            File.Delete(c.Path(".talk", "chat-1"));
            c.Expect(c.PumpUntil(() => !c.Sub("Worker").StartsWith("talking")), "Worker no longer talking once the chat ended");
        });

        // ---- terminal redesign, step 5: work list, inbox and status line effects ----

        // Work pane wp1: the Hive page's at-work list is gone; the same work
        // lines live on a bee's page (beeWorking), so this runs there.
        Test("work lines: a new task's line on its bee's page types in with its spinner, and Stop freezes it to ■ before it wipes out", c =>
        {
            c.Start();
            c.Call("ShowBee", "Worker");
            c.Call("Go", "bees");
            c.Pump(300);
            var task = Tasks.Create(c.Outside, "Worker", "Look into it", "", "read");
            task.Doc.Set("status", "running");
            task.Doc.Set("pid", Runner(task.Id));
            task.Doc.Save(task.Path);
            c.Expect(c.PumpUntil(() => c.WorkRow(task.Id) != null), "a line for the task on Worker's page");
            var row = c.WorkRow(task.Id)!;
            c.Expect(c.PumpUntil(() => G.Text(c.RowLabel(row, "Bee")) == "Worker", 4000), "the bee's name typed out in full; it says " + G.Text(c.RowLabel(row, "Bee")));
            c.Expect(G.Text(c.RowLabel(row, "Spinner")) is { Length: 1 } spun && spun != "■", "a spinner");
            Tasks.Stop(c.Outside, task.Id);
            c.Expect(c.PumpUntil(() => G.Text(c.RowLabel(row, "Spinner")) == "■"), "the spinner frozen to ■; it says " + G.Text(c.RowLabel(row, "Spinner")));
            c.Expect(c.PumpUntil(() => G.gtk_widget_get_parent(c.RowOuter(row)) == 0, 6000), "the line wiped out and gone from the list");
        });

        Test("inbox: a finished task's item waits behind black until its bee drops it off, then wipes in", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            task.Doc.Set("status", "running");
            task.Doc.Set("pid", Runner(task.Id));
            task.Doc.Save(task.Path);
            File.WriteAllText(task.StepsPath, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\tEdit\tsrc/A.cs\n");
            c.Start(pumpMs: 2500);
            Tasks.Record(c.Outside, Tasks.Load(c.Outside, task.Id), "Found it.", "done");
            c.Expect(c.PumpUntil(() => c.InboxHeld("task:" + task.Id) == true), "the item there, held until the delivery");
            c.Expect(c.PumpUntil(() => c.InboxHeld("task:" + task.Id) == false, 8000), "the item let in once Worker dropped it off");
            c.Expect(c.Get<Odometer>("inboxCount").Shown == "1", "the count rolled to 1; it shows " + c.Get<Odometer>("inboxCount").Shown);
        });

        Test("inbox: Dismiss wipes the item out, the count rolls down, and the panel's border sparks once", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            Tasks.Record(c.Store, task, "Found it.", "done");
            c.Start();
            c.OpenInbox(); // work pane wp1: the inbox is in the header's ▤ popover
            var outer = c.InboxOuter("task:" + task.Id);
            c.Pre(outer != 0, "the item's row");
            c.Call("Dismiss", "task:" + task.Id);
            c.Expect(Fx.Sparking(c.Get<nint>("inboxPanel")), "a spark running around the inbox's border");
            c.Expect(c.Get<Odometer>("inboxCount").Shown == "0", "the count at 0");
            c.Expect(c.PumpUntil(() => G.gtk_widget_get_parent(outer) == 0, 6000), "the row wiped out and removed");
            c.Expect(c.PumpUntil(() => !Ui.Ticking, 4000), "nothing moving after");
            var doc = Tasks.Load(c.Store, task.Id).Doc;
            c.Expect(doc.Get("reward") == "" && doc.Get("status") == "done", "the task as it was");
        });

        Test("status line: honey is said plainly, at once, with no typing", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            Tasks.Record(c.Store, task, "Found it.", "done");
            c.Start();
            c.OpenInbox(); // work pane wp1: the inbox is in the header's ▤ popover
            c.Click(G.Button(c.Get<nint>("inbox"), "Give honey"));
            var status = c.Get<StatusLine>("status");
            c.Expect(G.Text(status.Message).StartsWith("Worker earned honey"), "\"Worker earned honey\" in full at once; it says " + G.Text(status.Message));
        });

        // ---- terminal redesign, step 6: the Timeline in text ----

        Test("timeline: each lane is a label of text bars that a screen reader reads as a sentence", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            task.Doc.Set("started", Text.Now);
            task.Doc.Save(task.Path);
            Tasks.Record(c.Store, Tasks.Load(c.Store, task.Id), "Found it.", "done");
            c.Start();
            // Rebuilt to the mockups: the bars are drawn; the lane's name is the label a screen reader hears.
            var lane = (nint)c.Get<List<long>>("laneLabels")[0];
            c.Expect(G.Text(lane) == "Worker", "Worker's lane; it reads " + G.Text(lane));
            c.Expect(c.Get<System.Collections.IList>("marks").Cast<object>().Any(m => (string)c.Field(m, "Lane")! == "Worker" && (int)c.Field(m, "Kind")! == 0), "a done bar in Worker's lane");
            c.Expect(G.gtk_test_accessible_has_property(lane, 4) != 0 && !G.Says(lane, "Worker"), "the lane heard as a sentence about its bars, not just the name");
        });

        Test("timeline: a new save pops in as a tick, then nothing moves", c =>
        {
            c.Start();
            File.WriteAllText(c.Path("comb", "global", "a-new-lesson.md"), $"---\ntitle: A lesson\ntype: lesson\nbee: Worker\nproject: global\ncreated: {Text.Now}\nsummary: x\n---\n\nbody\n");
            c.Expect(c.PumpUntil(() => c.Get<System.Collections.IList>("marks").Cast<object>().Any(m => (int)c.Field(m, "Kind")! == 3 && (double)c.Field(m, "Born")! >= 0)), "a save mark that pops in");
            c.Expect(c.PumpUntil(() => G.Text((nint)c.Get<List<long>>("laneLabels")[0]) == "Worker", 4000), "Worker's lane for the tick; it reads " + G.Text((nint)c.Get<List<long>>("laneLabels")[0]));
            c.Expect(c.PumpUntil(() => !Ui.Ticking, 6000), "nothing moving once it popped in (and the cell it lit faded)");
        });

        // ---- terminal redesign, step 7: the other pages ----

        Test("page switch: the new page's panels draw their frames in and type their titles, then nothing moves", c =>
        {
            c.Start();
            c.Call("Go", "bees");
            var panel = c.Get<Dictionary<nint, nint>>("panels")[c.Get<nint>("beeSplit")];
            c.Expect(Fx.Drawing(panel), "the Bees detail panel drawing itself in");
            c.Expect(c.PumpUntil(() => !Fx.Drawing(panel) && !Ui.Ticking, 5000), "done, and no tick after");
        });

        Test("popover: a bee's popover draws its frame in from the corner", c =>
        {
            c.Start();
            c.Call("ShowBeePop", "Worker");
            var popover = c.Get<nint>("popover");
            var panel = G.OfType(popover, "GtkOverlay").First();
            c.Expect(Fx.Drawing(panel), "the popover's panel drawing itself in");
            c.Expect(c.PumpUntil(() => !Fx.Drawing(panel), 5000), "drawn");
        });

        Test("bee page: the vignette shows a working bee at its station and a resting one in its cell", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            task.Doc.Set("status", "running");
            task.Doc.Set("pid", Runner(task.Id));
            task.Doc.Save(task.Path);
            File.WriteAllText(task.StepsPath, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\tEdit\tsrc/A.cs\n");
            c.Start(pumpMs: 2500);
            c.Call("ShowBee", "Worker");
            c.Expect(c.VignetteArt() == "at the bench", "Worker at the bench (it edits) in its vignette; it says " + c.VignetteArt());
            Tasks.Record(c.Outside, Tasks.Load(c.Outside, task.Id), "Done.", "done");
            c.Expect(c.PumpUntil(() => c.VignetteArt() == "in its cell", 8000), "Worker back in its cell once home; it says " + c.VignetteArt());
        });

        Test("memory: a new search's results cascade in, then stay", c =>
        {
            for (int i = 0; i < 6; i++) c.WriteEntry("global", $"note-{i}", $"A note about bees {i}");
            for (int i = 0; i < 3; i++) c.WriteEntry("global", $"other-{i}", $"Something else {i}");
            c.Start();
            c.Call("Go", "memory");
            c.Pump(500);
            N.gtk_editable_set_text(c.Get<nint>("search"), "bees");
            var results = c.Get<nint>("results");
            c.Expect(c.PumpUntil(() => G.Descendants(results).Any(r => G.TypeName(r) == "GtkListBoxRow" && G.gtk_widget_get_opacity(r) < 1)), "rows fading in");
            c.Expect(c.PumpUntil(() => G.Descendants(results).Where(r => G.TypeName(r) == "GtkListBoxRow").All(r => G.gtk_widget_get_opacity(r) >= 1), 5000), "all rows in");
        });

        // ---- terminal redesign, step 8: what screen readers hear ----

        Test("accessibility: a working bee is one sentence (its cell's label); its sprite's art is never heard", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            task.Doc.Set("status", "running");
            task.Doc.Set("pid", Runner(task.Id));
            task.Doc.Set("started", Text.Now);
            task.Doc.Save(task.Path);
            File.WriteAllText(task.StepsPath, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\tEdit\tsrc/A.cs\n");
            c.Start();
            var voice = (nint)c.Get<List<long>>("cellLabels")[1]; // Worker's cell (the Queen's is 0)
            c.Expect(G.Says(voice, "Worker, working: editing src/A.cs, just started"), "Worker heard through its cell as \"Worker, working: editing src/A.cs, just started\"");
            // Rebuilt to the mockups: the sprites are drawn in the scene, not widgets, so there's nothing of them to hear.
            c.Expect(!G.Descendants(c.Get<nint>("scene")).Any(w => G.TypeName(w) == "GtkLabel" && G.Text(w).Contains('ʚ')), "no label with the sprite's art");
        });

        Test("accessibility: a work line is one sentence; its spinner, tree and bar are hidden", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            task.Doc.Set("status", "running");
            task.Doc.Set("pid", Runner(task.Id));
            task.Doc.Set("started", Text.Now);
            task.Doc.Save(task.Path);
            File.WriteAllText(task.StepsPath, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\tRead\tsrc/B.cs\n");
            c.Start();
            c.Call("ShowBee", "Worker"); // work pane wp1: work lines are on the bee's page now
            c.Pre(c.PumpUntil(() => c.WorkRow(task.Id) != null), "the task's line on Worker's page");
            var row = c.WorkRow(task.Id)!;
            var line = (nint)c.Field(c.Field(row, "Wipe")!, "Row")!;
            c.Expect(G.Says(line, "Worker, working: reading src/B.cs, just started"), "the line says \"Worker, working: reading src/B.cs, just started\"");
            c.Expect(G.Unspoken(c.RowLabel(row, "Spinner")) && G.Unspoken(c.RowLabel(row, "Bar")) && G.Unspoken(c.RowLabel(row, "Tree")), "the spinner, the tree line and the bar hidden");
        });

        Test("accessibility: a [ button ] is heard as its label, a panel by its title", c =>
        {
            c.Start();
            c.Call("ShowBee", "Worker");
            var start = G.Button(c.Get<nint>("beeDetail"), "Start");
            c.Expect(G.Says(start, "Start"), "[ Start ] heard as \"Start\"");
            var panel = c.Get<Dictionary<nint, nint>>("panels")[c.Get<nint>("beeSplit")];
            var frame = N.gtk_overlay_get_child(panel);
            c.Expect(G.Says(frame, "Worker") && G.Unspoken(N.gtk_frame_get_label_widget(frame)), "the panel heard as \"Worker\", its ┤ Worker ├ label hidden");
        });

        // ---- a stranger's first run ----

        Test("first run: an empty hive says how to make the first bee, with no zero counts or empty notches", c =>
        {
            File.Delete(c.Path("bees", "Worker.md"));
            Directory.Delete(c.Path("projects"), true);
            Directory.CreateDirectory(c.Path("projects"));
            c.Start();
            var stats = G.Text(c.Get<nint>("hiveStats"));
            var rest = G.Text(c.Get<nint>("restLabel"));
            // Work pane wp1: the at-work panel and its "No bees yet. Click + new…"
            // hint are gone; the honeycomb's "no bees yet" and its + new cell stay.
            c.Expect(stats == "a new hive", "the header to say a new hive, not 0 memories · 0 bees · 0 projects; it says " + stats);
            c.Expect(rest == "no bees yet", "the honeycomb to say no bees yet; it says " + rest);
            c.Expect(c.Get<List<(double X, double Y)>>("slots").Count == 2, "the Queen's cell and + new in the honeycomb");
            c.Expect(G.gtk_widget_has_css_class(c.Get<nint>("inboxButton"), "empty") != 0, "the header's ▤ count dimmed, nothing waiting");
            c.Outside.CreateBee("Helper", "Helps", [], "", "approve");
            c.Expect(c.PumpUntil(() => G.Text(c.Get<nint>("restLabel")) == "1 resting"), "with a bee, the honeycomb to say 1 resting; it says " + G.Text(c.Get<nint>("restLabel")));
            c.Expect(G.Text(c.Get<nint>("hiveStats")).Contains("1 bee"), "the counts once there's a bee; it says " + G.Text(c.Get<nint>("hiveStats")));
        });

        // Since stage 6 there are no ready-made bees: the dialog makes a bee from what you type.
        Test("new bee: the dialog offers no ready-made bees, and a name and specialty make a bee with an empty How I work", c =>
        {
            c.Start();
            c.Call("NewBee");
            c.Pump(1000); // the dialog shown
            var window = c.Get<nint>("window");
            var dialog = G.OfType(window, "AdwAlertDialog").FirstOrDefault();
            c.Pre(dialog != 0, "the new-bee dialog");
            var rows = G.OfType(dialog, "AdwEntryRow").ToList();
            c.Pre(rows.Count >= 2, $"its entry rows; {rows.Count}");
            c.Expect(G.OfType(dialog, "GtkButton").Count(b => G.gtk_widget_has_css_class(b, "pill") != 0 || G.gtk_widget_has_css_class(b, "chip") != 0) == 0, "no ready-made chips");
            N.gtk_editable_set_text(rows[0], "Quill");
            N.gtk_editable_set_text(rows[1], "Writes small things");
            c.Pump(200);
            var create = G.Button(window, "Create");
            c.Pre(create != 0, "the Create button");
            G.g_signal_emit_by_name(create, "clicked", 0);
            c.Pump(600);
            var bee = c.Store.FindBee("Quill");
            c.Expect(bee != null && bee.Specialty == "Writes small things" && bee.Doc.Body.Contains("## How I work\n\n## Rules I've learned") && bee.Doc.Get("role") == "",
                     "Quill with an empty How I work and no role; errors: " + string.Join(" | ", c.Errors) + "; card: " + bee?.Doc.Body);
        });

        // ---- terminal redesign, step 9 (stage 9): tasks ----

        Test("task view: the step log, and Follow up starts a linked task that resumes the session", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            File.WriteAllText(task.StepsPath, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\tEdit\tsrc/A.cs\n{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\t=Edit\terror\n");
            task.Doc.Set("session", "abc-123");
            task.Doc.Save(task.Path);
            Tasks.Record(c.Store, Tasks.Load(c.Store, task.Id), "Found it.", "done");
            c.Start();
            c.Call("ShowTask", task.Id);
            c.Pump(1000); // the dialog shown (a button that isn't realized yet ignores activation)
            var view = c.Get<object?>("taskView")!;
            var log = (nint)c.Field(view, "Log")!;
            c.Expect(G.Text(log).Contains("editing src/A.cs") && G.Text(log).Contains("↳ error"), "the step log with the edit and its error; it reads " + G.Text(log));
            var dialog = (nint)c.Field(view, "Dialog")!;
            var entry = G.OfType(dialog, "GtkEntry").First(e => Marshal.PtrToStringUTF8(G.gtk_entry_get_placeholder_text(e))!.StartsWith("What should Worker do next"));
            N.gtk_editable_set_text(entry, "Now fix it.");
            var follow = G.Button(dialog, "Follow up");
            c.Pre(follow != 0, "a Follow up button");
            G.g_signal_emit_by_name(follow, "clicked", 0); // as a click does (a dialog's buttons ignore activation headless)
            c.Pump(300);
            var next = Tasks.List(c.Store, "Worker").FirstOrDefault(x => x.Id != task.Id);
            c.Expect(next != null && next.Doc.Get("follows") == task.Id && next.Doc.Get("resume") == "abc-123" && next.Prompt == "Now fix it.", "a new task following it, resuming abc-123; errors: " + string.Join(" | ", c.Errors));
        });

        Test("bee page: tasks in groups; a working one is a work line, a finished one under recent", c =>
        {
            var done = Tasks.Create(c.Store, "Worker", "The first thing", "", "read");
            Tasks.Record(c.Store, done, "Done.", "done");
            var running = Tasks.Create(c.Store, "Worker", "The second thing", "", "read");
            running.Doc.Set("status", "running");
            running.Doc.Set("pid", Runner(running.Id));
            running.Doc.Save(running.Path);
            c.Start();
            c.Call("ShowBee", "Worker");
            var working = c.Get<object?>("beeWorking")!;
            var shown = (System.Collections.IDictionary)c.Field(working, "Shown")!;
            c.Expect(shown.Contains(running.Id), "the running task as a work line");
            c.Expect(c.Get<List<BeeTask>>("tasks") is [var t] && t.Id == done.Id, "the finished one under recent");
            c.Expect(G.gtk_widget_get_visible(c.Get<nint>("waitingHead")) == 0, "no waiting group (nothing waits)");
        });

        Test("scene: Hand to flies the scroll to the next bee's cell, then the next bee flies out", c =>
        {
            c.Store.CreateBee("Wren", "Tests the work", [], "", "approve");
            var first = Tasks.Create(c.Store, "Worker", "Find the bug", "", "read");
            Tasks.Record(c.Store, first, "It's in Doc.cs.", "done");
            c.Start(pumpMs: 1500);
            var next = Tasks.HandTo(c.Outside, Tasks.Load(c.Outside, first.Id), "Wren", "Write a test.");
            c.Expect(c.PumpUntil(() => c.SpriteState("Worker") == "deliver" && (bool)c.Field(c.SpriteObject("Wren"), "Held")!), "Worker carrying its scroll to Wren, who waits");
            c.Expect(c.PumpUntil(() => !(bool)c.Field(c.SpriteObject("Wren"), "Held")! && c.AwayFromHome("Wren"), 8000), "Wren flying out with it once it arrived");
        });

        // ---- terminal redesign, step 10 (stage 10): Briefs and Memory ----

        Test("briefs: the meter follows the Queen's page as it's typed, before it's saved", c =>
        {
            c.Start();
            c.Call("Go", "briefs");
            c.Pump(500);
            var view = c.Get<System.Collections.IList>("briefViews")[0]!;
            var meter = c.Field(view, "Meter")!;
            var count = (nint)c.Field(meter, "count")!;
            c.Pre(c.PumpUntil(() => G.Text(count).Contains("/ 9,000")), "the count against 9,000; it says " + G.Text(count));
            var before = int.Parse(G.Text(count).Split(' ')[0].Replace(",", ""));
            var queen = c.Get<Editor>("queen");
            Ui.SetText(queen.View, queen.Text + "\n" + new string('x', 500));
            c.Expect(c.PumpUntil(() => int.Parse(G.Text(count).Split(' ')[0].Replace(",", "")) >= before + 500, 4000), $"the count up by 500 from {before}; it says " + G.Text(count));
            c.Expect(c.Store.QueenText.IndexOf(new string('x', 500)) < 0, "the Queen's file unchanged (nothing saved)");
        });

        Test("briefs: picking a bee shows that bee's exact briefing", c =>
        {
            c.Start();
            c.Call("Go", "briefs");
            c.Pump(500);
            var view = c.Get<System.Collections.IList>("briefViews")[0]!;
            N.gtk_drop_down_set_selected((nint)c.Field(view, "BeeDrop")!, 1); // as Worker
            var text = (nint)c.Field(view, "Text")!;
            c.Expect(c.PumpUntil(() => Ui.GetText(text).StartsWith("# Bee brief: Worker")), "Worker's briefing; it starts " + Ui.GetText(text).Split('\n')[0]);
        });

        Test("memory: an entry's file header has its id and Copy id", c =>
        {
            c.WriteEntry("global", "an-entry", "An entry");
            c.Start();
            c.Call("OpenEntry", "an-entry");
            var detail = c.Get<nint>("entryDetail");
            var copy = G.Button(detail, "copy id");
            c.Pre(copy != 0, "a Copy id button");
            G.g_signal_emit_by_name(copy, "clicked", 0);
            c.Expect(c.PumpUntil(() => G.Text(c.Get<StatusLine>("status").Message) == "Copied an-entry."), "\"Copied an-entry.\" in the status line");
        });

        // ---- team review review rv-b (terminal redesign, steps 2-10) ----
        // "rv-b #n" is the finding number in that review. The use-after-free
        // checks make a GTK CRITICAL fatal: before the fix they end in exit 134
        // (the CRITICAL) or 139 (the segfault).

        Test("rv-b #1: project switches, then a bee saves an entry (Birch's probe5)", c =>
        {
            Ctx.FatalCriticals();
            c.Start();
            c.Call("Go", "briefs");
            c.Pump(300);
            foreach (var p in new[] { "alpha", "zeta", "alpha", "zeta" }) { c.Call("ShowProject", p); c.Pump(100); }
            c.Call("Go", "hive");
            c.Pump(300);
            c.WriteEntry("global", "outside-entry", "Written by a bee");
            c.Pump(2000);
            var views = c.Get<System.Collections.IList>("briefViews");
            c.Expect(views.Count == 2, $"the Queen's brief view and zeta's, got {views.Count}");
        });

        Test("rv-b #2: typing a Memory search letter by letter (Birch's probe4)", c =>
        {
            for (int i = 0; i < 40; i++) c.WriteEntry("global", $"apple-{i}", $"Apple note {i}");
            Ctx.FatalCriticals();
            c.Start();
            c.Call("Go", "memory");
            c.Pump(500);
            var search = c.Get<nint>("search");
            foreach (var s in new[] { "a", "ap", "app", "appl", "apple" }) { N.gtk_editable_set_text(search, s); c.Pump(250); }
            c.Pump(1500);
            c.Expect(c.Get<List<Hit>>("hits").Count == 40, "the 40 apple notes");
        });

        Test("rv-b #3: a step still typing in when its task ends (the Arbiter's probe6)", c =>
        {
            var task = Running(c, "Worker");
            Ctx.FatalCriticals();
            c.Start();
            c.Call("ShowBee", "Worker"); // work pane wp1: work lines are on the bee's page now
            c.Call("Go", "bees");
            c.Pump(500);
            File.AppendAllText(task.StepsPath, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\tthink\t{new string('x', 159)}\n");
            c.Pump(450);
            c.Pre(c.WorkRow(task.Id) is { } row && G.Text(c.RowLabel(row, "Step")).EndsWith('▌'), "the step typing in");
            var outer = Ctx.Watch(c.RowOuter(c.WorkRow(task.Id)!));
            Tasks.Record(c.Outside, Tasks.Load(c.Outside, task.Id), "Done.", "done");
            c.Expect(c.PumpUntil(() => Ctx.Freed(outer), 4000), "the line wiped out and freed");
            c.Pump(2000);
        });

        Test("rv-b #3: a stopped line says stopped while its step was still typing in", c =>
        {
            var task = Running(c, "Worker");
            c.Start();
            c.Call("ShowBee", "Worker"); // work pane wp1: work lines are on the bee's page now
            c.Call("Go", "bees");
            c.Pump(500);
            File.AppendAllText(task.StepsPath, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\tthink\t{new string('x', 159)}\n");
            c.Pump(450);
            var row = c.WorkRow(task.Id)!;
            c.Pre(G.Text(c.RowLabel(row, "Step")).EndsWith('▌'), "the step typing in");
            Tasks.Record(c.Outside, Tasks.Load(c.Outside, task.Id), "", "stopped");
            c.Call("Refresh");
            c.Call("RefreshTasks"); // at once, while the step types on (the bee page's lines follow RefreshTasks, as a .tasks change calls it)
            c.Pre((string)c.Field(row, "Ended")! == "stopped" && G.Text(c.RowLabel(row, "Step")) == "stopped", "the line ended as stopped");
            c.Pump(1000); // it shows how it ended for 1.2 s; the step would type on for more than that
            c.Expect(G.Text(c.RowLabel(row, "Step")) == "stopped", "\"stopped\" on the line; it says " + G.Text(c.RowLabel(row, "Step")));
        });

        Test("rv-b #4: retiring a bee at work drops its work lines with its page (Ash's repro A)", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            Ctx.FatalCriticals();
            c.Start();
            c.Call("ShowBee", "Worker");
            c.Call("Go", "bees");
            c.Pump(500);
            var lines = c.Get<object>("beeWorking");
            var shown = (System.Collections.IDictionary)c.Field(lines, "Shown")!;
            c.Pre(shown.Count == 1, "one work line on Worker's page, got " + shown.Count);
            var box = Ctx.Watch((nint)c.Field(lines, "Box")!);
            c.Click(G.Button(c.Get<nint>("beeDetail"), "edit")); // rebuilt to the mockups: Retire is with the card's editor
            c.Pre(c.PumpUntil(() => N.gtk_widget_get_mapped(G.Button(c.Get<nint>("beeDetail"), "Retire bee")), 4000), "the card's editor open");
            c.Click(G.Button(c.Get<nint>("beeDetail"), "Retire bee"));
            c.Pump(800);
            var all = c.Get<System.Collections.IList>("allLines");
            c.Expect(!all.Contains(lines), $"the old lines gone with their page (box freed: {Ctx.Freed(box)})");
            Tasks.Record(c.Outside, Tasks.Load(c.Outside, task.Id), "ok", "done");
            c.Expect(c.PumpUntil(() => !Ui.Ticking, 13000), $"nothing ticking once the task is over ({Ui.Moving} steps)");
        });

        Test("rv-b #5: a bee page rebuilt while a stopped line waits to wipe out", c =>
        {
            c.Store.CreateBee("Wren", "Tests the work", [], "", "approve");
            var task = Running(c, "Worker");
            Ctx.FatalCriticals();
            c.Start();
            c.Call("ShowBee", "Worker");
            c.Call("Go", "bees");
            c.Pump(500);
            var shown = (System.Collections.IDictionary)c.Field(c.Get<object>("beeWorking"), "Shown")!;
            c.Pre(shown.Count == 1, "one work line on Worker's page");
            Tasks.Record(c.Outside, Tasks.Load(c.Outside, task.Id), "", "stopped");
            c.Pre(c.PumpUntil(() => shown.Count == 0, 2000), "the line stopped, waiting to wipe out");
            c.Call("ShowBee", "Wren");
            c.Pump(2500);
            c.Expect(!Ui.Ticking, "nothing ticking after");
        });

        Test("rv-b #5: a review page closed while its mini scene runs", c =>
        {
            var run = RunningReview(c);
            Ctx.FatalCriticals();
            c.Start();
            c.Call("Go", "reviews");
            c.Call("ShowRun", run);
            c.Pre(c.PumpUntil(() => c.Get<bool>("runMoving")), "the mini scene moving");
            c.Call("ShowRun", (string?)null);
            c.Expect(c.PumpUntil(() => !c.Get<bool>("runMoving")), "the mini scene's step stopped");
            c.Pump(300);
        });

        Test("rv-b #12: panels of pages gone leave no layer behind", c =>
        {
            c.Start();
            c.Call("Go", "briefs");
            c.Call("ShowProject", "alpha");
            c.Pump(800);
            var layers = (System.Collections.IDictionary)typeof(App).Assembly.GetType("Hive.Gui.Fx")!.GetField("layers", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var before = layers.Count;
            foreach (var p in new[] { "zeta", "alpha", "zeta", "alpha", "zeta", "alpha" }) { c.Call("ShowProject", p); c.Pump(100); }
            c.Pump(800);
            c.Expect(layers.Count == before, $"{before} layers as with one project page shown, got {layers.Count}");
        });

        Test("rv-b #8: a running review's mini scene moves again after a tab switch", c =>
        {
            var run = RunningReview(c);
            c.Start();
            c.Call("Go", "reviews");
            c.Call("ShowRun", run);
            c.Pre(c.PumpUntil(() => c.Get<bool>("runMoving")), "the mini scene moving");
            c.Call("Go", "hive");
            c.Pre(c.PumpUntil(() => !c.Get<bool>("runMoving")), "stopped while the Reviews tab is away");
            c.Call("Go", "reviews");
            c.Expect(c.PumpUntil(() => c.Get<bool>("runMoving"), 1000), "moving again on the Reviews tab");
        });

        Test("rv-b #9: Stop in the task view freezes the bee page's line to ■", c =>
        {
            var task = Running(c, "Worker");
            task.Doc.Set("led_by", "session"); // Stop kills no process
            task.Doc.Save(task.Path);
            c.Start();
            c.Call("ShowBee", "Worker");
            c.Call("Go", "bees");
            c.Pump(500);
            var row = ((System.Collections.IDictionary)c.Field(c.Get<object>("beeWorking"), "Shown")!)[task.Id];
            c.Pre(row != null, "the task as a work line on Worker's page");
            c.Call("ShowTask", task.Id);
            c.Pump(1000);
            var stop = G.Button((nint)c.Field(c.Get<object?>("taskView")!, "Dialog")!, "Stop");
            c.Pre(stop != 0, "a Stop button");
            G.g_signal_emit_by_name(stop, "clicked", 0);
            c.Pump(200);
            c.Expect((string)c.Field(row!, "Ended")! == "stopped", $"the line ended as stopped, not {c.Field(row!, "Ended")}");
        });

        Test("rv-b #7: the resume command quotes the task's folder", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            task.Doc.Set("session", "abc-123");
            task.Doc.Set("folder", "/home/u/my project");
            task.Doc.Save(task.Path);
            Tasks.Record(c.Store, Tasks.Load(c.Store, task.Id), "Done.", "done");
            c.Start();
            c.Call("ShowTask", task.Id);
            c.Pump(300);
            var dialog = (nint)c.Field(c.Get<object?>("taskView")!, "Dialog")!;
            var said = G.OfType(dialog, "GtkLabel").Select(G.Text).FirstOrDefault(t => t.Contains("--resume")) ?? "";
            c.Expect(said == "cd '/home/u/my project' && claude --resume abc-123", "the folder quoted; it says " + said);
        });

        Test("rv-b #11: the Queen's preview leaves out the page's front matter", c =>
        {
            c.Start();
            c.Call("Go", "briefs");
            c.Pump(300);
            var queen = c.Get<Editor>("queen");
            Ui.SetText(queen.View, "---\nnote: kept out\n---\n\n" + queen.Text);
            var text = (nint)c.Field(c.Get<System.Collections.IList>("briefViews")[0]!, "Text")!;
            c.Pump(800);
            c.Expect(!Ui.GetText(text).Contains("note: kept out"), "no front matter in the briefing; it starts " + Ui.GetText(text).Split('\n')[0]);
        });

        // ---- rebuilt to the mockups, stage 1: the chrome ----

        // The user's screenshot of Reviews had white bars around the list: the
        // sidebar pane painted with libadwaita's colours while the window
        // wasn't focused. Nothing between the panels may be lighter than the
        // window's black, focused or not.
        Test("chrome: nothing light shows between the panels, even when the window isn't focused", c =>
        {
            c.Start();
            c.Call("Go", "reviews");
            var window = c.Get<nint>("window");
            G.gtk_widget_set_state_flags(window, 1 << 6, 0); // GTK_STATE_FLAG_BACKDROP
            c.Pump(2500); // styles follow on the next frame, and broadway draws one a second
            var (pixels, width) = (Array.Empty<byte>(), 0);
            c.Pre(c.PumpUntil(() => { (pixels, width) = G.Render(window); return width > 0; }, 4000), "the window drawn");
            var height = pixels.Length / 4 / width;
            // The left edge of the page, top to bottom, under the header and above the status line.
            var light = 0;
            for (int y = 60; y < height - 60; y += 10)
            {
                var i = (y * width + 4) * 4;
                if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 3 * 40 && pixels[i + 3] > 0) light++;
            }
            var mid = ((height / 2) * width + 4) * 4;
            c.Expect(light == 0, $"black (or the window showing through) at the left edge; {light} light points (one is {pixels[mid]},{pixels[mid + 1]},{pixels[mid + 2]},{pixels[mid + 3]})");
        });

        Test("chrome: a button is a box with its label, heard as its label", c =>
        {
            c.Start();
            c.Call("ShowBee", "Worker");
            var start = G.Button(c.Get<nint>("beeDetail"), "Start");
            c.Expect(start != 0 && Marshal.PtrToStringUTF8(G.gtk_button_get_label(start)) == "Start" && G.gtk_widget_has_css_class(start, "btn") != 0, "a Start button labelled \"Start\" (no [ ]) in a box");
            c.Expect(G.Says(start, "Start"), "heard as \"Start\"");
        });

        // ---- rebuilt to the mockups, stage 2: the Hive page ----

        Test("hive page: the cells are a honeycomb around the Queen, none overlapping", c =>
        {
            c.Store.CreateBee("Wren", "Tests the work", [], "", "approve");
            c.Start();
            var slots = c.Get<List<(double X, double Y)>>("slots");
            c.Expect(slots.Count == 4, $"the Queen, Worker, Wren and + new: 4 cells, got {slots.Count}");
            c.Expect(slots[0] == (240, 260), $"the Queen at the hive's centre (240, 260), got {slots[0]}");
            var near = slots.Skip(1).Select(p => Math.Sqrt(Math.Pow(p.X - 240, 2) + Math.Pow(p.Y - 260, 2))).ToList();
            c.Expect(near.All(d => Math.Abs(d - 79.7) < 0.5), "every other cell next to the Queen's (79.7 apart); distances " + string.Join(", ", near.Select(d => d.ToString("0.0"))));
            for (int i = 0; i < slots.Count; i++)
                for (int j = i + 1; j < slots.Count; j++)
                    c.Expect(Math.Sqrt(Math.Pow(slots[i].X - slots[j].X, 2) + Math.Pow(slots[i].Y - slots[j].Y, 2)) >= 76, $"cells {i} and {j} apart");
        });

        Test("hive page: a working bee's station lights up, and its cell says where it went", c =>
        {
            var task = Running(c, "Worker"); // an edit: the bench
            c.Start(pumpMs: 2500);
            c.Call("DrawAll", Ui.Clock);
            c.Expect((string)c.Field(c.SpriteObject("Worker"), "Station")! == "bench", "Worker at the bench; it's at " + c.Field(c.SpriteObject("Worker"), "Station"));
            var bench = c.Get<System.Collections.IList>("stations").Cast<object>().First(st => (string)c.Field(st, "Name")! == "bench");
            c.Expect(((System.Collections.IList)c.Field(bench, "Here")!).Contains(c.SpriteObject("Worker")), "the bench lit with Worker there");
            Tasks.Record(c.Outside, Tasks.Load(c.Outside, task.Id), "Done.", "done");
            c.Expect(c.PumpUntil(() => !c.Get<bool>("moving"), 8000), "the scene still once Worker is home");
            c.Expect(((System.Collections.IList)c.Field(bench, "Here")!).Count == 0, "the bench dark again");
        });

        // Work pane wp1: the column beside the scene became the pane, and the
        // inbox (where the long texts are) a 400 px popover.
        Test("hive page: the pane and the inbox popover keep their widths, whatever the texts say", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", string.Concat(Enumerable.Repeat("a long title that goes on ", 40)), "", "read");
            Tasks.Record(c.Store, task, string.Concat(Enumerable.Repeat("and a long result too ", 40)), "done");
            c.Start();
            var pane = c.Get<nint>("pane");
            var expected = c.PaneWidthFor();
            var width = 0;
            c.Expect(c.PumpUntil(() => (width = G.gtk_widget_get_width(pane)) == expected, 3000), $"the pane {expected} px wide, got {width}");
            c.OpenInbox();
            var panel = c.Get<nint>("inboxPanel");
            c.Expect(c.PumpUntil(() => (width = G.gtk_widget_get_width(panel)) == 400, 3000), $"the inbox's panel 400 px wide, got {width}");
        });

        Test("timeline: every lane shows, however many bees worked", c =>
        {
            for (int i = 0; i < 9; i++)
            {
                c.Store.CreateBee($"Bee{i}", "Does things", [], "", "approve");
                var task = Tasks.Create(c.Store, $"Bee{i}", "Look into it", "", "read");
                task.Doc.Set("started", Text.Now);
                task.Doc.Save(task.Path);
                Tasks.Record(c.Store, Tasks.Load(c.Store, task.Id), "Done.", "done");
            }
            c.Start();
            var area = c.Get<nint>("timelineArea");
            c.Expect(c.Get<List<string>>("lanes").Count == 9, "9 lanes");
            var height = 0;
            c.Expect(c.PumpUntil(() => (height = N.gtk_widget_get_height(area)) >= 9 * 22, 3000), $"room for all 9 lanes, got {height} px");
            var clipped = false;
            for (var w = G.gtk_widget_get_parent(area); w != 0 && G.gtk_widget_has_css_class(w, "panel") == 0; w = G.gtk_widget_get_parent(w))
                clipped |= G.TypeName(w) == "GtkScrolledWindow";
            c.Expect(!clipped, "no scrolled window of its own between the lanes and the panel");
        });

        // ---- rebuilt to the mockups, stage 3: Reviews ----

        // The user's screenshot showed the Arbiter's report as raw Markdown.
        Test("reviews: the report is rendered: the verdict box, findings with their tags and file links", c =>
        {
            Reviews.SetUp(c.Store, new[] { "Cedar", "Birch", "Ash", "Arbiter" });
            const string run = "review-20261018-120000";
            var reviewer = Tasks.Create(c.Store, "Cedar", "Review src/", "", "read", run: run, role: "reviewer");
            Tasks.Record(c.Store, reviewer, "Cedar's report.", "done");
            var overseer = Tasks.Create(c.Store, "Arbiter", "Oversee", "", "read", run: run, role: "overseer", waitsFor: [reviewer.Id]);
            Tasks.Record(c.Store, overseer, """
                # Review: src/ (alpha)

                **Verdict: don't merge yet.** One crash.

                ## Confirmed

                1. **[bug high]** `src/A.cs:12`: it crashes on empty input (found by Cedar, Birch).
                   - **What happens:** it divides by zero.
                   - Fix: check for zero.
                2. **[bloat low]** `src/B.cs:3`: an unused `using`.
                """, "done");
            c.Start();
            c.Call("Go", "reviews");
            c.Call("ShowRun", run);
            var detail = c.Get<nint>("runDetail");
            var labels = G.OfType(detail, "GtkLabel").Select(G.Text).ToList();
            c.Expect(labels.Contains("✗ Don't merge yet"), "the verdict as \"✗ Don't merge yet\"");
            c.Expect(G.Descendants(detail).Count(w => G.gtk_widget_has_css_class(w, "finding") != 0) == 2, "two findings");
            c.Expect(labels.Contains("bug · high") && labels.Contains("bloat · low"), "their tags; labels: " + string.Join(" | ", labels.Take(30)));
            c.Expect(G.Button(detail, "src/A.cs:12") != 0, "a link to src/A.cs:12");
            c.Expect(labels.Contains("what happens") && labels.Contains("fix"), "the finding's rows labelled \"what happens\" and \"fix\"");
            c.Expect(labels.Contains("found by Cedar · Birch"), "who found it, under it");
            c.Expect(!labels.Any(l => l.Contains("**") || l.Contains("##")), "no raw Markdown left");
        });

        Test("reviews: a review's row in the list is the column's width, not squeezed", c =>
        {
            Reviews.SetUp(c.Store, new[] { "Cedar", "Birch", "Ash", "Arbiter" });
            var reviewer = Tasks.Create(c.Store, "Cedar", "Review", "", "read", run: "review-20261018-120000", role: "reviewer");
            reviewer.Doc.Set("focus", string.Concat(Enumerable.Repeat("a long description of what to review ", 12)));
            reviewer.Doc.Save(reviewer.Path);
            Tasks.Record(c.Store, reviewer, "Findings.", "done");
            c.Start();
            c.Call("Go", "reviews");
            var list = c.Get<nint>("runList");
            var title = 0;
            c.Pre(c.PumpUntil(() => (title = G.OfType(list, "GtkLabel").Where(l => G.Text(l).StartsWith("a long description")).Select(N.gtk_widget_get_width).FirstOrDefault()) > 0, 3000), "the row's title shown");
            c.Expect(title >= 250, $"the title at least 250 px wide, got {title}");
        });

        // ---- rebuilt to the mockups, stage 4: Bees ----

        Test("bees: a bee's card is rendered, its headings as small caps and its steps numbered", c =>
        {
            c.Store.EditCard("Worker", "# Worker\n\n## How I work\n\n1. Read the hive first.\n2. Do the **smallest** change.\n\n## Rules I've learned\n\n- Measure `size` after each change.\n");
            c.Start();
            c.Call("Go", "bees");
            c.Call("ShowBee", "Worker");
            var labels = G.OfType(c.Get<nint>("beeDetail"), "GtkLabel").Select(G.Text).ToList();
            c.Expect(labels.Contains("HOW I WORK") && labels.Contains("RULES I'VE LEARNED"), "the headings as small caps; labels: " + string.Join(" | ", labels.Take(40)));
            c.Expect(labels.Contains("1") && labels.Contains("2") && labels.Contains("Do the smallest change."), "the steps numbered, without the ** marks");
            c.Expect(!labels.Any(l => l.Contains("## ") || l.Contains("**")), "no raw Markdown");
        });

        Test("bees: card history says what each proposed change was and how it went", c =>
        {
            c.Store.Molt("Worker", "a rule about tests", "# Worker\n\nA new card.\n", "queen");
            c.Store.DecideMolt(c.Store.PendingMolts()[0].Id, true);
            c.Store.Molt("Worker", "a rule nobody wanted", "# Worker\n\nAnother card.\n");
            c.Store.DecideMolt(c.Store.PendingMolts()[0].Id, false);
            c.Start();
            c.Call("ShowBee", "Worker");
            var labels = G.OfType(c.Get<nint>("beeDetail"), "GtkLabel").Select(G.Text).ToList();
            c.Expect(labels.Contains("a rule about tests") && labels.Any(l => l.StartsWith("approved · from the Queen")), "the approved change, from the Queen");
            c.Expect(labels.Contains("a rule nobody wanted") && labels.Any(l => l.StartsWith("rejected · from Worker")), "the rejected one, from Worker");
        });

        Test("bees: the list says which bee works, for how long, and which rests", c =>
        {
            c.Store.CreateBee("Wren", "Tests the work", [], "", "approve");
            Running(c, "Worker");
            c.Start();
            var labels = G.OfType(c.Get<nint>("beeList"), "GtkLabel").Select(G.Text).ToList();
            c.Expect(labels.Contains("0m") && labels.Contains("resting"), "\"0m\" for Worker at work and \"resting\" for Wren; labels: " + string.Join(" | ", labels));
        });

        // ---- rebuilt to the mockups, stage 5: Memory ----

        Test("memory: a chip per type says how many there are, and picking one filters the results", c =>
        {
            for (int i = 0; i < 3; i++) c.WriteEntry("global", $"note-{i}", $"A note {i}");
            File.WriteAllText(c.Path("comb", "global", "a-lesson.md"), $"---\ntitle: A lesson\ntype: lesson\nbee: Worker\nproject: global\ncreated: {Text.Now}\nsummary: x\n---\n\nbody\n");
            c.Start();
            c.Call("Go", "memory");
            c.Pump(300);
            var chips = c.Get<nint>("typeChips");
            c.Expect(G.Button(chips, "all 4") != 0 && G.Button(chips, "≡ note 3") != 0 && G.Button(chips, "! lesson 1") != 0, "chips all 4, ≡ note 3, ! lesson 1; buttons: " + string.Join(" | ", G.OfType(chips, "GtkButton").Select(b => Marshal.PtrToStringUTF8(G.gtk_button_get_label(b)))));
            G.g_signal_emit_by_name(G.Button(chips, "! lesson 1"), "clicked", 0);
            c.Pump(300);
            c.Expect(c.Get<List<Hit>>("hits") is [var h] && h.Id == "a-lesson", "only the lesson listed");
        });

        Test("memory: an entry's text is rendered, not shown as Markdown", c =>
        {
            File.WriteAllText(c.Path("comb", "global", "rich.md"), $"---\ntitle: Rich\ntype: note\nbee: queen\nproject: global\ncreated: {Text.Now}\nsummary: A summary.\n---\n\n## What\n\nSome **bold** and `code`.\n\n- a point\n");
            c.Start();
            c.Call("OpenEntry", "rich");
            var labels = G.OfType(c.Get<nint>("entryDetail"), "GtkLabel").Select(G.Text).ToList();
            c.Expect(labels.Contains("Some bold and code.") && labels.Contains("a point"), "the text without its marks; labels: " + string.Join(" | ", labels));
            c.Expect(!labels.Any(l => l.Contains("**") || l.Contains("## ")), "no raw Markdown");
        });

        // ---- rebuilt to the mockups, stage 6: Briefs ----

        Test("briefs: the editor numbers its lines and lights its Markdown", c =>
        {
            c.Store.SaveQueen("# Queen\n\n<!-- a note -->\n\n## Always\n\n- one rule\n");
            c.Start();
            c.Call("Go", "briefs");
            var view = c.Get<Editor>("queen").View;
            c.Expect(G.gtk_text_view_get_gutter(view, 3) != 0, "a gutter left of the text for the line numbers");
            c.Expect(G.HasTag(view, 0, 0, "md-h1") && G.HasTag(view, 2, 1, "md-comment") && G.HasTag(view, 4, 1, "md-h2") && G.HasTag(view, 6, 0, "md-bullet"), "# lit, the comment faint, ## honey, the bullet's dash");
            c.Expect(!G.HasTag(view, 6, 3, "md-bullet"), "the rule's words plain");
        });

        Test("briefs: ● unsaved shows while the page differs from its file, and revert brings the file's text back", c =>
        {
            c.Start();
            c.Call("Go", "briefs");
            c.Pump(500);
            var queen = c.Get<Editor>("queen");
            var panel = c.Get<nint>("queenEditorPanel");
            nint Unsaved() => G.OfType(panel, "GtkLabel").FirstOrDefault(l => G.Text(l) == "● unsaved");
            c.Pre(Unsaved() != 0 && G.gtk_widget_get_visible(Unsaved()) == 0, "no ● unsaved before typing");
            Ui.SetText(queen.View, queen.Text + "\nA line not saved.");
            c.Expect(G.gtk_widget_get_visible(Unsaved()) != 0, "● unsaved while it differs");
            G.g_signal_emit_by_name(G.Button(panel, "revert"), "clicked", 0);
            c.Pump(200);
            c.Expect(!queen.Text.Contains("A line not saved.") && G.gtk_widget_get_visible(Unsaved()) == 0, "the file's text back, and no ● unsaved");
        });

        // ---- work pane, stage wp2: the Queen's conversation in the pane ----
        // Per-bee talk is
        // gone: the old "talk: clicking a bee's cell opens its terminal…" is
        // dropped (no terminal per bee; a bee's click opens its popover, below),
        // and the message and transcript checks are rewritten for the Queen.
        // Keys and clicks go through the widgets' own controllers.

        Test("queen: a message starts a Queen turn with the mode shown, and shift+tab cycles the mode", c =>
        {
            c.Start();
            var mode = c.Get<nint>("talkMode");
            c.Pre(G.Text(mode).StartsWith("⏵⏵ accept edits on"), "accept edits to start (the default); it says " + G.Text(mode));
            var entry = c.Get<nint>("talkEntry");
            c.Pre(G.PressKey(entry, 0xfe20, 1) == true, "the entry to take shift+tab"); // ISO_Left_Tab, shift
            c.Expect(G.Text(mode).StartsWith("⏵⏵ bypass permissions on"), "bypass permissions after shift+tab; it says " + G.Text(mode));
            G.PressKey(entry, 0xfe20, 1);
            c.Expect(G.Text(mode).StartsWith("⏵⏵ read only"), "read only after another; it says " + G.Text(mode));
            c.Say("What do you know about Doc.cs?");
            var turn = Tasks.List(c.Store, Tasks.Queen).FirstOrDefault();
            c.Expect(turn != null && turn.Doc.Get("role") == "queen" && turn.Doc.Get("access") == "read" && turn.Prompt == "What do you know about Doc.cs?" && turn.Doc.Get("resume") == "",
                     "a fresh Queen turn with read access; errors: " + string.Join(" | ", c.Errors));
            c.Expect(c.TalkLabels().Contains("What do you know about Doc.cs?"), "what you said in the transcript");
            c.Expect(N.gtk_editable_get_text(entry) is var left && Marshal.PtrToStringUTF8(left) == "", "the prompt emptied");
        });

        Test("queen: the transcript is the session's own, as Claude Code shows it", c =>
        {
            QueenSaid(c, "sess-1", "/p", 
                """{"type":"user","message":{"role":"user","content":"fix it"}}""",
                """{"type":"assistant","message":{"model":"claude-opus-5-5","usage":{"output_tokens":2400},"content":[{"type":"text","text":"Fixing it."},{"type":"tool_use","id":"t1","name":"Edit","input":{"file_path":"/p/src/A.cs","old_string":"a();","new_string":"b();"}}]}}""",
                """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""");
            c.Start();
            var labels = c.TalkLabels();
            c.Expect(labels.Contains("fix it") && labels.Contains("Fixing it.") && labels.Contains("Edit(src/A.cs)") && labels.Contains("Updated with 1 addition and 1 removal") && labels.Contains("a();") && labels.Contains("b();"),
                     "❯ fix it, ● Fixing it., ● Edit(src/A.cs), ⎿ its result and the diff; labels: " + string.Join(" | ", labels));
            c.Expect(G.Text(c.Get<nint>("talkTokens")) == "↓ 2.4k tokens" && G.Text(c.Get<nint>("talkModel")) == "Opus 5.5", $"↓ 2.4k tokens and Opus 5.5; it says {G.Text(c.Get<nint>("talkTokens"))} and {G.Text(c.Get<nint>("talkModel"))}");
        });

        Test("queen: the conversation carries on after a restart, and a message resumes it", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"from yesterday"}}""");
            c.Start(); // a new window: the conversation is read from the files
            c.Expect(c.TalkLabels().Contains("from yesterday"), "yesterday's conversation in the pane; labels: " + string.Join(" | ", c.TalkLabels()));
            c.Say("and now?");
            var turn = Tasks.List(c.Store, Tasks.Queen).First();
            c.Expect(turn.Prompt == "and now?" && turn.Doc.Get("resume") == "sess-1", $"the turn resumes sess-1; it resumes \"{turn.Doc.Get("resume")}\"");
        });

        Test("queen: + new starts a fresh conversation, also after a restart", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"the old talk"}}""");
            c.Start();
            c.Pre(c.TalkLabels().Contains("the old talk"), "the old conversation in the pane");
            c.Click(G.Button(c.Get<nint>("talkPanel"), "+ new"));
            c.Expect(!c.TalkLabels().Contains("the old talk"), "the pane cleared; labels: " + string.Join(" | ", c.TalkLabels()));
            c.Expect(Tasks.QueenSession(Tasks.List(c.Outside, Tasks.Queen), Tasks.QueenSince(c.Outside)) == null, "for a new window too, no conversation to carry on (the mark in .tasks/.queen)");
            c.Say("hello again");
            var turn = Tasks.List(c.Store, Tasks.Queen).First();
            c.Expect(turn.Prompt == "hello again" && turn.Doc.Get("resume") == "", $"the turn starts fresh; it resumes \"{turn.Doc.Get("resume")}\"");
        });

        Test("queen: + new is refused while she answers, and esc stops her", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"the old talk"}}""");
            var busy = RunningQueen(c, "sess-1");
            c.Start();
            c.Click(G.Button(c.Get<nint>("talkPanel"), "+ new"));
            c.Expect(c.TalkLabels().Contains("the old talk") && !File.Exists(c.Path(".tasks", ".queen")), "the conversation kept, no new one marked");
            c.Expect(G.Text(c.Get<StatusLine>("status").Message).Contains("still answering") || c.PumpUntil(() => G.Text(c.Get<StatusLine>("status").Message).Contains("still answering"), 1500), "the status line says she's still answering; it says " + G.Text(c.Get<StatusLine>("status").Message));
            c.Pre(G.PressKey(c.Get<nint>("talkEntry"), 0xff1b) == true, "the entry to take esc");
            c.Expect(Tasks.Load(c.Store, busy.Id).Status == "stopped", "her turn stopped; it's " + Tasks.Load(c.Store, busy.Id).Status);
        });

        Test("queen: control: esc with nothing running stops nothing", c =>
        {
            var done = QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"hi"}}""");
            c.Start();
            G.PressKey(c.Get<nint>("talkEntry"), 0xff1b);
            c.Pump(200);
            c.Expect(Tasks.Load(c.Store, done.Id).Status == "done" && c.Errors.Count == 0, "the old turn still done, no error; errors: " + string.Join(" | ", c.Errors));
        });

        Test("queen: the folder picker lists ~ and each project's folder, and a new folder holds from the next message, in the same conversation", c =>
        {
            Directory.CreateDirectory(c.Path("code", "alpha"));
            Directory.CreateDirectory(c.Path("code", "zeta"));
            c.Store.SetProject("alpha", "Alpha project", [c.Path("code", "alpha")], null);
            c.Store.SetProject("zeta", "Zeta project", [c.Path("code", "zeta")], null);
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"hi"}}""");
            c.Start();
            var picker = c.Get<nint>("talkFolder");
            var items = G.Items(picker);
            var (a, z) = (Text.Tilde(c.Path("code", "alpha")), Text.Tilde(c.Path("code", "zeta")));
            c.Expect(items.Count == 3 && items[0] == "~" && items.Contains(a) && items.Contains(z), $"~, {a} and {z}; it lists " + string.Join(", ", items));
            N.gtk_drop_down_set_selected(picker, (uint)items.IndexOf(z));
            c.Pump(200);
            c.Expect(Tasks.List(c.Store, Tasks.Queen).Count == 1, "no turn just for picking a folder");
            c.Say("look around");
            var turn = Tasks.List(c.Store, Tasks.Queen).First();
            c.Expect(turn.Doc.Get("project") == "zeta" && turn.Doc.Get("folder") == z && turn.Doc.Get("resume") == "sess-1",
                     $"the turn in zeta's folder, resuming sess-1; it's project \"{turn.Doc.Get("project")}\", folder {turn.Doc.Get("folder")}, resuming \"{turn.Doc.Get("resume")}\"");
        });

        Test("queen: ❯ talk and the talk chips are gone, and clicking a bee opens its popover", c =>
        {
            Directory.CreateDirectory(c.Path(".talk"));
            File.WriteAllText(c.Path(".talk", "chat-1"), $"Worker\n{DateTime.UtcNow.Ticks}\n"); // `hive talk Worker` running
            c.Start();
            c.Call("ShowBee", "Worker");
            c.Call("Go", "bees");
            c.Pump(300);
            var window = c.Get<nint>("window");
            c.Expect(G.Button(window, "❯ talk") == 0 && !G.OfType(window, "GtkLabel").Select(G.Text).Any(t => t.Contains("❯ talk")), "no ❯ talk anywhere in the window");
            var chips = G.OfType(c.Get<nint>("talkPanel"), "GtkButton").Where(b => G.gtk_widget_has_css_class(b, "talk-chip") != 0).ToList();
            c.Expect(chips.Count == 1 && G.Says(chips[0], "The Queen"), $"one chip in the pane, the Queen's; {chips.Count} chips");
            c.Call("Go", "hive");
            c.Pump(300);
            var (x, y) = c.Slot(c.CellOf("Worker"));
            c.ClickScene(x, y);
            c.Expect(c.PumpUntil(() => G.gtk_widget_get_visible(c.Get<nint>("popover")) != 0), "Worker's popover shown");
            c.Expect(G.Button(c.Get<nint>("popover"), "Start") != 0, "its task box");
        });

        foreach (var (w, h) in new[] { (905, 700), (1024, 768), (1320, 900) })
            Test($"queen: the pane's status lines shorten with … and the pane stays max(380, 38%) ({w}x{h})", c =>
            {
                var model = "claude-" + new string('x', 150); // shown as "Xxx…x": one long word
                QueenSaid(c, "sess-1", "/p", "{\"type\":\"assistant\",\"message\":{\"model\":\"" + model + "\",\"usage\":{\"output_tokens\":123456789},\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}}");
                var busy = RunningQueen(c, "sess-1");
                File.WriteAllText(busy.StepsPath, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\tBash\t{string.Concat(Enumerable.Repeat("make a very long command line ", 10))}\n");
                c.Start(pumpMs: 1500);
                c.Size(w, h);
                var pane = c.Get<nint>("pane");
                var want = c.PaneWidthFor();
                var got = 0;
                c.Expect(c.PumpUntil(() => (got = G.gtk_widget_get_width(pane)) == want, 3000), $"the pane {want} px; it's {got}");
                c.Pre(G.gtk_widget_get_visible(c.Get<nint>("talkBusy")) != 0, "the ✻ line showing while she answers");
                var modelLabel = c.Get<nint>("talkModel");
                c.Expect(c.PumpUntil(() => G.pango_layout_is_ellipsized(G.gtk_label_get_layout(modelLabel)) != 0, 3000), $"the 151-letter model name shortened with …; {G.gtk_widget_get_width(modelLabel)} px");
                // Every status and ✻ label ends inside the pane.
                var right = G.BoxIn(pane, c.Get<nint>("window")).X + got;
                foreach (var name in new[] { "talkMode", "talkModel", "talkTokens", "talkBusyText", "talkBusyMeta" })
                {
                    var l = c.Get<nint>(name);
                    var end = G.BoxIn(l, c.Get<nint>("window")).X + G.gtk_widget_get_width(l);
                    c.Expect(end <= right + 0.5, $"{name} (\"{G.Text(l)}\") inside the pane: it ends at {end}, the pane at {right}");
                }
            });

        // ---- stage 1: the model picker in the pane's status line ----
        // The model label is a menu (default, Opus 5.5, Sonnet 5.5,
        // Haiku 4.5, Fable 5.1); a pick passes --model to each Queen turn and
        // is kept with the conversation. The menu is opened and picked from
        // with mouse clicks (the GtkGestureClick of its button and of each
        // choice), in a tall window, 1280x1440. The menu is found as
        // the GtkMenuButton around the talkModel label, not by its field.

        foreach (var (name, id) in ModelChoices.Skip(1))
            Test($"model: a click on {name} in the menu (1280x1440) shows {name}, keeps it in .tasks/.queen-model, and the next turn runs --model {id}", c =>
            {
                QueenSaid(c, "sess-1", "/p", OpusSaid);
                c.Start();
                c.Size(1280, 1440);
                c.Pre(G.Text(c.Get<nint>("talkModel")) == "Opus 5.5", "Opus 5.5 from the transcript to start; it says " + G.Text(c.Get<nint>("talkModel")));
                OpenModelMenu(c);
                Shots(c, "model-menu-open-" + id);
                ClickModelChoice(c, name);
                c.Expect(c.PumpUntil(() => G.gtk_widget_get_visible(ModelPop(c)) == 0, 2000), "the menu closed after the pick");
                c.Expect(G.Text(c.Get<nint>("talkModel")) == name, $"the status line says {name}; it says {G.Text(c.Get<nint>("talkModel"))}");
                c.Expect(Tasks.QueenModel(c.Outside) == id, $"{id} kept for a restart (read by another Store); it reads \"{Tasks.QueenModel(c.Outside)}\"");
                c.Expect(c.PumpUntil(() => G.Text(c.Get<StatusLine>("status").Message).Contains(name + " from your next message"), 3000), "the status line message names the pick (it types itself out); it says " + G.Text(c.Get<StatusLine>("status").Message));
                c.Expect(Tasks.List(c.Store, Tasks.Queen).Count == 1, "no turn just for picking a model");
                c.Say("next");
                var turn = Tasks.List(c.Store, Tasks.Queen).First();
                var args = Tasks.Args(turn);
                c.Expect(turn.Prompt == "next" && turn.Doc.Get("resume") == "sess-1" && turn.Doc.Get("model") == id, $"the turn resumes sess-1 with model {id}; it has \"{turn.Doc.Get("model")}\"");
                c.Expect(args.IndexOf("--model") is var m and >= 0 && m + 1 < args.Count && args[m + 1] == id, $"--model {id} in its claude args: {string.Join(' ', args)}");
            });

        Test("model: a click on default in the menu (1280x1440) goes back to the transcript's model, forgets the pick, and the next turn has no model", c =>
        {
            QueenSaid(c, "sess-1", "/p", OpusSaid);
            Tasks.SetQueenModel(c.Outside, "claude-haiku-4-5-20251001"); // picked in an earlier window
            c.Start();
            c.Size(1280, 1440);
            c.Pre(G.Text(c.Get<nint>("talkModel")) == "Haiku 4.5", "Haiku 4.5 to start; it says " + G.Text(c.Get<nint>("talkModel")));
            OpenModelMenu(c);
            ClickModelChoice(c, "default");
            c.Expect(c.PumpUntil(() => G.gtk_widget_get_visible(ModelPop(c)) == 0, 2000), "the menu closed after the pick");
            c.Expect(G.Text(c.Get<nint>("talkModel")) == "Opus 5.5", "the status line says the transcript's Opus 5.5; it says " + G.Text(c.Get<nint>("talkModel")));
            c.Expect(Tasks.QueenModel(c.Outside) == "" && !File.Exists(c.Path(".tasks", ".queen-model")), "no pick kept");
            c.Say("next");
            var turn = Tasks.List(c.Store, Tasks.Queen).First();
            c.Expect(turn.Prompt == "next" && turn.Doc.Get("model") == "", $"the turn has no model; it has \"{turn.Doc.Get("model")}\"");
        });

        Test("model: the menu lists default and the four models in order, with ✓ on the pick only, after each click", c =>
        {
            QueenSaid(c, "sess-1", "/p", OpusSaid);
            c.Start();
            c.Size(1280, 1440);
            foreach (var (name, _) in ModelChoices.Skip(1).Append(ModelChoices[0]).Prepend(ModelChoices[0]))
            {
                if (name != "default" || G.Text(c.Get<nint>("talkModel")) != "Opus 5.5")
                {
                    OpenModelMenu(c);
                    ClickModelChoice(c, name);
                    c.Pre(c.PumpUntil(() => G.gtk_widget_get_visible(ModelPop(c)) == 0, 2000), "the menu closed after the pick");
                }
                var menu = ModelMenu(c);
                var names = menu.Select(l => l.TrimStart('✓', ' ')).ToList();
                c.Expect(names.SequenceEqual(ModelChoices.Select(m => m.Name)), $"default, Opus 5.5, Sonnet 5.5, Haiku 4.5, Fable 5.1; it lists " + string.Join(" | ", menu));
                var ticked = menu.Select((l, i) => (l, i)).Where(x => x.l.Contains('✓')).Select(x => names[x.i]).ToList();
                c.Expect(ticked.SequenceEqual([name]), $"✓ on {name} only (picked last); ✓ on " + string.Join(", ", ticked));
            }
        });

        Test("model: the names in the menu line up, with ✓ or without", c =>
        {
            c.Start();
            c.Size(1280, 1440);
            OpenModelMenu(c);
            var pop = ModelPop(c);
            var labels = G.OfType(pop, "GtkLabel").ToList();
            c.Pre(labels.Count == 5, $"five labels in the menu; {labels.Count}");
            var starts = labels.Select(l =>
            {
                var text = G.Text(l);
                var lead = text.Length - text.TrimStart('✓', ' ').Length;
                return (Text: text, X: G.XOfByte(l, System.Text.Encoding.UTF8.GetByteCount(text[..lead])) + G.BoxIn(l, pop).X);
            }).ToList();
            c.Expect(starts.Max(s => s.X) - starts.Min(s => s.X) < 1, "each name starts at the same x; " + string.Join(", ", starts.Select(s => $"\"{s.Text}\" at {s.X:0.#}")));
        });

        Test("model: a pick from before a restart shows in the label and the menu, and the next turn uses it", c =>
        {
            QueenSaid(c, "sess-1", "/p", OpusSaid);
            Tasks.SetQueenModel(c.Outside, "claude-sonnet-5-5"); // what the last window kept
            c.Start();
            c.Expect(G.Text(c.Get<nint>("talkModel")) == "Sonnet 5.5", "Sonnet 5.5 over the transcript's Opus; it says " + G.Text(c.Get<nint>("talkModel")));
            var menu = ModelMenu(c);
            c.Expect(menu.Count(l => l.Contains('✓')) == 1 && menu.First(l => l.Contains('✓')).EndsWith("Sonnet 5.5"), "✓ on Sonnet 5.5; the menu: " + string.Join(" | ", menu));
            c.Say("go on");
            var turn = Tasks.List(c.Store, Tasks.Queen).First();
            c.Expect(turn.Prompt == "go on" && turn.Doc.Get("model") == "claude-sonnet-5-5" && Tasks.Args(turn).Contains("claude-sonnet-5-5"), $"the turn runs on Sonnet; model \"{turn.Doc.Get("model")}\"");
        });

        foreach (var hand in new[] { "claude-gpt-9\n", "Sonnet 5.5\n", "", "claude-opus-5-5 --dangerously-skip-permissions\n" })
            Test($"model: a hand-edited .queen-model (\"{hand.TrimEnd()}\") reads as default: the transcript's model, ✓ default, no model on the turn", c =>
            {
                QueenSaid(c, "sess-1", "/p", OpusSaid);
                Directory.CreateDirectory(c.Path(".tasks"));
                File.WriteAllText(c.Path(".tasks", ".queen-model"), hand);
                c.Start();
                c.Expect(G.Text(c.Get<nint>("talkModel")) == "Opus 5.5", "the transcript's Opus 5.5; it says " + G.Text(c.Get<nint>("talkModel")));
                var menu = ModelMenu(c);
                c.Expect(menu.Count(l => l.Contains('✓')) == 1 && menu.First(l => l.Contains('✓')).EndsWith("default"), "✓ on default; the menu: " + string.Join(" | ", menu));
                c.Say("go on");
                var turn = Tasks.List(c.Store, Tasks.Queen).First();
                c.Expect(turn.Prompt == "go on" && turn.Doc.Get("model") == "" && !Tasks.Args(turn).Contains("--model"), $"no model on the turn; it has \"{turn.Doc.Get("model")}\"");
            });

        Test("model: control: with no transcript and no pick the label says Queen and ✓ is on default", c =>
        {
            c.Start();
            c.Expect(G.Text(c.Get<nint>("talkModel")) == "Queen", "Queen; it says " + G.Text(c.Get<nint>("talkModel")));
            var menu = ModelMenu(c);
            c.Expect(menu.Count(l => l.Contains('✓')) == 1 && menu.First(l => l.Contains('✓')).EndsWith("default"), "✓ on default; the menu: " + string.Join(" | ", menu));
        });

        Test("model: + new forgets the pick: Queen in the label, ✓ default, no .queen-model, and the next turn has no model", c =>
        {
            QueenSaid(c, "sess-1", "/p", OpusSaid);
            c.Start();
            c.Size(1280, 1440);
            OpenModelMenu(c);
            ClickModelChoice(c, "Fable 5.1");
            c.Pre(Tasks.QueenModel(c.Outside) == "claude-fable-5-1" && G.Text(c.Get<nint>("talkModel")) == "Fable 5.1", "Fable 5.1 picked");
            c.Click(G.Button(c.Get<nint>("talkPanel"), "+ new"));
            c.Expect(!File.Exists(c.Path(".tasks", ".queen-model")) && Tasks.QueenModel(c.Outside) == "", "no pick kept after + new");
            c.Expect(G.Text(c.Get<nint>("talkModel")) == "Queen", "the label says Queen (no transcript yet); it says " + G.Text(c.Get<nint>("talkModel")));
            var menu = ModelMenu(c);
            c.Expect(menu.Count(l => l.Contains('✓')) == 1 && menu.First(l => l.Contains('✓')).EndsWith("default"), "✓ on default; the menu: " + string.Join(" | ", menu));
            c.Say("fresh start");
            var turn = Tasks.List(c.Store, Tasks.Queen).First();
            c.Expect(turn.Prompt == "fresh start" && turn.Doc.Get("resume") == "" && turn.Doc.Get("model") == "" && !Tasks.Args(turn).Contains("--model"), $"a fresh turn with no model; model \"{turn.Doc.Get("model")}\", resume \"{turn.Doc.Get("resume")}\"");
        });

        // A message typed while she answers waits (Pending) and goes when her
        // turn ends; it runs on the pick at that moment, not at typing time.
        foreach (var (before, after, want, what) in new[]
        {
            ("", "Sonnet 5.5", "claude-sonnet-5-5", "Sonnet picked after it was queued: it goes with --model Sonnet"),
            ("claude-haiku-4-5-20251001", "default", "", "default picked after it was queued with Haiku: it goes with no model"),
            ("claude-haiku-4-5-20251001", "", "claude-haiku-4-5-20251001", "control: Haiku picked, nothing changed: it goes with Haiku"),
        })
            Test("model: a message queued while she answers — " + what, c =>
            {
                QueenSaid(c, "sess-1", "/p", OpusSaid);
                if (before.Length > 0) Tasks.SetQueenModel(c.Outside, before);
                var busy = RunningQueen(c, "sess-1");
                c.Start();
                c.Size(1280, 1440);
                c.Say("queued words");
                c.Pre((string)c.Field(c.Get<object>("talk"), "Pending")! == "queued words", "the message queued while she answers");
                if (after.Length > 0)
                {
                    OpenModelMenu(c);
                    ClickModelChoice(c, after);
                    c.Pre(c.PumpUntil(() => G.gtk_widget_get_visible(ModelPop(c)) == 0, 2000), "the menu closed after the pick");
                }
                c.Expect(Tasks.Load(c.Outside, busy.Id).Doc.Get("model") == "", "the turn already running untouched by the pick");
                var b = Tasks.Load(c.Outside, busy.Id);
                b.Doc.Set("session", "sess-1");
                b.Doc.Save(b.Path);
                Tasks.Record(c.Outside, Tasks.Load(c.Outside, busy.Id), "Done.", "done"); // her turn ends
                c.Call("RefreshHive");
                BeeTask? sent = null;
                c.Pre(c.PumpUntil(() => (sent = Tasks.List(c.Store, Tasks.Queen).FirstOrDefault(t => t.Prompt == "queued words")) != null, 3000), "the queued message sent once her turn ended; errors: " + string.Join(" | ", c.Errors));
                c.Expect(sent!.Doc.Get("model") == want, $"the sent turn's model \"{want}\"; it has \"{sent.Doc.Get("model")}\"");
                var args = Tasks.Args(sent);
                c.Expect(want.Length > 0 ? args.IndexOf("--model") is var m and >= 0 && args[m + 1] == want : sent.Doc.Get("model") == "", $"its claude args match: {string.Join(' ', args)}");
            });

        // ---- stage 2: honey and misses in the window ----
        // Taking honey or a miss back is one click; the toast's Undo gives it
        // back as it was: the same giver (reward_by), honey or miss, and note.

        foreach (var (bee, label, miss) in new[] { ("Birch", "✗ Birch", true), ("Ash", "⬡ Ash", false) })
            Test($"honey: on a review's page, {label} (the hive's {(miss ? "miss" : "honey")}) says who gave it, a click takes it back, and Undo brings it back from the hive", c =>
            {
                var run = AwardedReview(c);
                c.Start();
                c.Call("Go", "reviews");
                c.Call("ShowRun", run);
                c.Pump(500);
                var button = G.Button(c.Get<nint>("runDetail"), label);
                c.Pre(button != 0, $"a {label} button on the run's page");
                var task = Tasks.List(c.Store, bee).First(t => t.Doc.Get("run") == run);
                var before = RewardOf(task.Doc);
                c.Expect(G.Tooltip(button).Contains("from the hive") && G.Tooltip(button).Contains(task.Doc.Get("reward_note")), "its tooltip names the hive and the evidence; it says " + G.Tooltip(button));
                c.Click(button);
                c.Expect(Tasks.Load(c.Outside, task.Id).Doc.Get("reward") == "", "taken back; " + RewardOf(Tasks.Load(c.Outside, task.Id).Doc));
                c.Expect(c.PumpUntil(() => G.Text(c.Get<StatusLine>("status").Message).Contains(miss ? "Miss taken back." : "Honey taken back."), 3000), "the status line says so; it says " + G.Text(c.Get<StatusLine>("status").Message));
                nint undo = 0;
                c.Pre(c.PumpUntil(() => (undo = G.Button(c.Get<nint>("window"), "Undo")) != 0), "a toast with an Undo button");
                c.Click(undo);
                var after = RewardOf(Tasks.Load(c.Outside, task.Id).Doc);
                c.Expect(after == before, $"back as it was ({before}); now {after}");
            });

        Test("honey: in a task's view, Take it back on the Queen's miss, then Undo, brings back the Queen's miss with its evidence", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            Tasks.Record(c.Store, task, "Found it.", "done");
            c.Store.Award("task:" + task.Id, "miss", "the user asked for a redo (task:x)", "queen");
            var before = RewardOf(Tasks.Load(c.Store, task.Id).Doc);
            c.Start();
            c.Call("ShowTask", task.Id);
            c.Pump(1000); // the dialog shown
            var dialog = (nint)c.Field(c.Get<object?>("taskView")!, "Dialog")!;
            c.Expect(G.OfType(dialog, "GtkLabel").Select(G.Text).Any(t => t.StartsWith("A miss from the Queen") && t.Contains("the user asked for a redo")), "\"A miss from the Queen …\" with the evidence; labels: " + string.Join(" | ", G.OfType(dialog, "GtkLabel").Select(G.Text).Where(t => t.Contains("miss") || t.Contains("honey"))));
            var back = G.Button(dialog, "Take it back");
            c.Pre(back != 0, "a Take it back button");
            G.g_signal_emit_by_name(back, "clicked", 0); // as a click does (a dialog's buttons ignore activation headless)
            c.Pump(300);
            c.Expect(Tasks.Load(c.Outside, task.Id).Doc.Get("reward") == "", "taken back");
            nint undo = 0;
            c.Pre(c.PumpUntil(() => (undo = G.Button(c.Get<nint>("window"), "Undo")) != 0), "a toast with an Undo button");
            c.Click(undo);
            c.Expect(RewardOf(Tasks.Load(c.Outside, task.Id).Doc) == before, $"back as it was ({before}); now {RewardOf(Tasks.Load(c.Outside, task.Id).Doc)}");
        });

        Test("honey: in Memory, ⬡ on an entry's honey from the Queen takes it back, and Undo brings back the Queen's honey with its note", c =>
        {
            var id = c.Store.Deposit("Worker", "worklog", "Stage 3 done", "Stage 3 built", "body", "", [], []);
            c.Store.Award(id, "honey", "the user committed it as is (commit abc123)", "queen");
            var before = RewardOf(Doc.Load(c.Store.Locate(id)!.Value.Path));
            c.Start();
            c.Call("Go", "memory");
            c.Call("ShowEntry", id);
            c.Pump(500);
            var button = G.Button(c.Get<nint>("entryDetail"), "⬡ Worker");
            c.Pre(button != 0, "a ⬡ Worker button on the entry");
            c.Expect(G.Tooltip(button).Contains("from the Queen"), "its tooltip names the Queen; it says " + G.Tooltip(button));
            c.Click(button);
            c.Expect(Doc.Load(c.Outside.Locate(id)!.Value.Path).Get("reward") == "", "taken back");
            nint undo = 0;
            c.Pre(c.PumpUntil(() => (undo = G.Button(c.Get<nint>("window"), "Undo")) != 0), "a toast with an Undo button");
            c.Click(undo);
            var after = RewardOf(Doc.Load(c.Outside.Locate(id)!.Value.Path));
            c.Expect(after == before, $"back as it was ({before}); now {after}");
        });

        Test("honey: old honey without reward_by shows as the user's (no \"from\") in a task's view", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            Tasks.Record(c.Store, task, "Found it.", "done");
            var t = Tasks.Load(c.Store, task.Id);
            t.Doc.Set("reward", "yes");
            t.Doc.Set("rewarded", "2026-10-18 12:00");
            t.Doc.Set("reward_note", "sharp");
            t.Doc.Save(t.Path);
            c.Start();
            c.Call("ShowTask", task.Id);
            c.Pump(1000);
            var dialog = (nint)c.Field(c.Get<object?>("taskView")!, "Dialog")!;
            var line = G.OfType(dialog, "GtkLabel").Select(G.Text).FirstOrDefault(x => x.StartsWith("Earned honey")) ?? "";
            c.Expect(line.Length > 0 && !line.Contains(" from the "), "\"Earned honey …\" with no giver named; it says \"" + line + "\"");
        });

        Test("honey: a task with a miss is still in the inbox with Give honey", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            Tasks.Record(c.Store, task, "Found it.", "done");
            c.Store.Award("task:" + task.Id, "miss", "the build broke (task:x)", "queen");
            c.Start();
            c.OpenInbox();
            var outer = c.InboxOuter("task:" + task.Id);
            c.Expect(outer != 0 && G.Button(outer, "Give honey") != 0, "its inbox item with Give honey");
        });

        // ---- stage 3: a small rule that applied at once, in the inbox ----

        Test("small rule: a rule that applied at once shows in the ▤ inbox, and its Undo puts the old card back with the change waiting", c =>
        {
            var (id, oldCard) = AppliedRule(c);
            c.Start();
            c.OpenInbox();
            var outer = c.InboxOuter("applied:" + id);
            c.Expect(outer != 0 && N.gtk_widget_get_mapped(outer), "the applied change's item in the inbox");
            var labels = G.OfType(outer, "GtkLabel").Select(G.Text).ToList();
            c.Expect(labels.Any(l => l.Contains("Worker's card gained a rule")), "it says Worker's card gained a rule; " + string.Join(" | ", labels));
            var undo = G.Button(outer, "Undo");
            c.Pre(undo != 0, "an Undo button on the item");
            c.Click(undo);
            c.Expect(c.Outside.RequireBee("Worker").Doc.Body.Trim() == oldCard, "the old card back; it is:\n" + c.Outside.RequireBee("Worker").Doc.Body);
            c.Expect(c.Outside.PendingMolts().Any(m => m.Id == id) && c.Outside.AutoApplied().Count == 0, "the change waits for the user, no longer listed as applied");
            c.Expect(c.PumpUntil(() => c.InboxOuter("applied:" + id) == 0 && c.InboxOuter("molt:" + id) != 0, 3000), "the inbox item is now the waiting change");
            c.Expect(c.Errors.Count == 0, "no error; " + string.Join(" | ", c.Errors));
        });

        Test("small rule: control: Undo on an applied rule when the card changed again since says so, and changes nothing", c =>
        {
            var (id, _) = AppliedRule(c);
            var now = c.Store.RequireBee("Worker").Doc.Body.Trim() + "\n- Edited by hand.\n";
            c.Store.EditCard("Worker", now);
            c.Start();
            c.OpenInbox();
            var outer = c.InboxOuter("applied:" + id);
            c.Pre(outer != 0, "the applied change's item in the inbox");
            c.Click(G.Button(outer, "Undo"));
            c.Expect(c.Outside.RequireBee("Worker").Doc.Body.Trim() == now.Trim(), "the card as edited");
            c.Expect(c.Errors.Any(e => e.Contains("changed again")), "an error saying the card changed again; errors: " + string.Join(" | ", c.Errors));
        });

        // ---- stage 4: dictation (pw-record + whisper-cli) ----
        // A real pw-record may be installed where these run. Every check here
        // first calls VoiceFakes: PATH is only a folder of fakes, XDG_DATA_HOME
        // and XDG_RUNTIME_DIR are temp folders, and it's an ERROR if the
        // pw-record dictation would run isn't the fake. The real microphone
        // is never opened.

        foreach (var (missing, pw, whisper, model) in new[] { ("whisper-cli", true, false, true), ("a ggml model", true, true, false), ("pw-record", false, true, true) })
            Test($"voice: without {missing} the mic is hidden, the ❯ tooltip says what to install, and alt+v says it", c =>
            {
                VoiceFakes(c, pw, whisper, model);
                c.Start();
                var mic = c.Get<nint>("talkMic");
                c.Expect(G.gtk_widget_get_visible(mic) == 0, "the mic hidden");
                var glyph = c.Get<nint>("talkGlyph");
                var key = missing == "a ggml model" ? "ggml model" : missing;
                c.Expect(G.Tooltip(glyph).Contains("Voice dictation needs") && G.Tooltip(glyph).Contains(key) && G.Tooltip(glyph).Contains("alt+v"), $"the ❯ tooltip names {key}; it says \"{G.Tooltip(glyph)}\"");
                c.Expect(G.PressKey(c.Get<nint>("talkEntry"), 0x76, 8) == true, "alt+v taken by the prompt");
                c.Expect(c.PumpUntil(() => G.Text(c.Get<StatusLine>("status").Message).Contains("Voice dictation needs") && G.Text(c.Get<StatusLine>("status").Message).Contains(key), 3000), "the status line says what's missing; it says " + G.Text(c.Get<StatusLine>("status").Message));
                c.Expect(VoiceLog(c).Count == 0, "nothing ran; the fakes' log: " + string.Join(" | ", VoiceLog(c)));
            });

        Test("voice: control: with pw-record, whisper-cli and a model the mic shows and the ❯ has no install hint", c =>
        {
            VoiceFakes(c);
            c.Start();
            c.Expect(G.gtk_widget_get_visible(c.Get<nint>("talkMic")) != 0, "the mic shown");
            c.Expect(G.Tooltip(c.Get<nint>("talkGlyph")) == "", "no hint on the ❯; it says " + G.Tooltip(c.Get<nint>("talkGlyph")));
            c.Expect(VoiceLog(c).Count == 0 && FakeProcesses(c).Count == 0, "nothing running before you ask");
        });

        Test("voice: a click on the mic records (● 0:0n), a second click stops it (transcribing…), and the text goes in at the cursor, not sent", c =>
        {
            var rig = VoiceFakes(c, whisperMode: "slow");
            c.Start();
            var entry = c.Get<nint>("talkEntry");
            N.gtk_editable_set_text(entry, "fix the bug");
            N.gtk_editable_set_position(entry, 3); // after "fix"
            var mic = c.Get<nint>("talkMic");
            c.Pre(c.PumpUntil(() => N.gtk_widget_get_mapped(mic) && G.gtk_widget_get_width(mic) > 0), "the mic on screen");
            c.Pre(G.ClickMiddle(mic) > 0, "a click gesture on the mic");
            c.Pump(200);
            var voice = c.Get<nint>("talkVoice");
            c.Expect(G.gtk_widget_get_visible(voice) != 0 && System.Text.RegularExpressions.Regex.IsMatch(G.Text(voice), @"^● 0:0\d$"), $"● 0:0n while recording; it says \"{G.Text(voice)}\"");
            var start = VoiceLog(c).FirstOrDefault(l => l.StartsWith("start ")) ?? "";
            var wav = Path.Combine(rig.Runtime, $"hive-dictation-{Environment.ProcessId}.wav");
            c.Expect(start.EndsWith($"--rate 16000 --channels 1 --format s16 {wav}"), $"pw-record --rate 16000 --channels 1 --format s16 {wav}; it ran: {start}");
            c.Expect(c.PumpUntil(() => G.Text(voice) == "● 0:01", 2500), "● 0:01 a second in; it says " + G.Text(voice));
            c.Pre(G.ClickMiddle(mic) > 0, "a second click on the mic");
            c.Expect(c.PumpUntil(() => G.Text(voice) == "transcribing…", 2000), "transcribing… after the second click; it says " + G.Text(voice));
            c.Expect(c.PumpUntil(() => Marshal.PtrToStringUTF8(N.gtk_editable_get_text(entry)) == "fix Hello from the fake whisper. the bug", 6000),
                     "the text at the cursor, a space either side; the prompt says \"" + Marshal.PtrToStringUTF8(N.gtk_editable_get_text(entry)) + "\"; errors: " + string.Join(" | ", c.Errors));
            var (from, to) = Selection(entry);
            c.Expect(from == to && to == "fix Hello from the fake whisper.".Length, $"the cursor right after what was heard, nothing selected (typing on would replace a selection); the selection is {from}..{to}");
            c.Expect(c.PumpUntil(() => G.gtk_widget_get_visible(voice) == 0, 1500), "the ● label gone");
            c.Expect(Tasks.List(c.Store, Tasks.Queen).Count == 0, "nothing sent to the Queen");
            c.Expect(VoiceLog(c).Any(l => l == $"INT {StartPid(start)}"), "pw-record stopped with SIGINT (so it writes the wav header); log: " + string.Join(" | ", VoiceLog(c)));
            var whisper = VoiceLog(c).FirstOrDefault(l => l.StartsWith("whisper ")) ?? "";
            c.Expect(whisper == $"whisper -m {rig.Model} -f {wav} -nt -np -l auto", "whisper-cli -m <model> -f <wav> -nt -np -l auto; it ran: " + whisper);
            c.Expect(!File.Exists(wav), "the wav deleted");
            c.Expect(c.PumpUntil(() => FakeProcesses(c).Count == 0, 3000), "no pw-record or whisper-cli left: " + string.Join(" | ", FakeProcesses(c)));
        });

        Test("voice: hold alt+v (its repeats don't stop it) and let go after 0.5 s: it stops and the text goes in", c =>
        {
            VoiceFakes(c);
            c.Start();
            var entry = c.Get<nint>("talkEntry");
            c.Pre(G.PressKey(entry, 0x76, 8) == true, "alt+v taken");
            c.Pre(c.PumpUntil(() => VoiceLog(c).Any(l => l.StartsWith("start ")), 2000), "recording started");
            for (int i = 0; i < 4; i++) { c.Pump(150); G.PressKey(entry, 0x76, 8); } // the key repeats while held
            c.Expect(VoiceLog(c).Count(l => l.StartsWith("start ")) == 1 && !VoiceLog(c).Any(l => l.StartsWith("INT")), "still recording, once, through the repeats; log: " + string.Join(" | ", VoiceLog(c)));
            G.ReleaseKey(entry, 0x76, 8);
            c.Expect(c.PumpUntil(() => Marshal.PtrToStringUTF8(N.gtk_editable_get_text(entry)) == "Hello from the fake whisper.", 6000), "the text in the prompt after letting go; it says \"" + Marshal.PtrToStringUTF8(N.gtk_editable_get_text(entry)) + "\"");
            c.Expect(Tasks.List(c.Store, Tasks.Queen).Count == 0, "nothing sent");
        });

        Test("voice: a quick tap of alt+v (let go under 0.5 s) keeps recording until the next alt+v", c =>
        {
            VoiceFakes(c);
            c.Start();
            var entry = c.Get<nint>("talkEntry");
            c.Pre(G.PressKey(entry, 0x76, 8) == true, "alt+v taken");
            c.Pump(100);
            G.ReleaseKey(entry, 0x76, 8);
            c.Pump(900);
            c.Expect(VoiceLog(c).Count(l => l.StartsWith("start ")) == 1 && !VoiceLog(c).Any(l => l.StartsWith("INT")), "still recording a second after a quick tap; log: " + string.Join(" | ", VoiceLog(c)));
            G.PressKey(entry, 0x76, 8);
            G.ReleaseKey(entry, 0x76, 8);
            c.Expect(c.PumpUntil(() => Marshal.PtrToStringUTF8(N.gtk_editable_get_text(entry)) == "Hello from the fake whisper.", 6000), "the next alt+v stops it and the text goes in; it says \"" + Marshal.PtrToStringUTF8(N.gtk_editable_get_text(entry)) + "\"");
        });

        Test("voice: alt+v in the focused prompt: the text goes in, and the cursor is after it with nothing selected", c =>
        {
            VoiceFakes(c);
            c.Start();
            var entry = c.Get<nint>("talkEntry");
            N.gtk_widget_grab_focus(entry);
            N.gtk_editable_set_text(entry, "fix the bug");
            N.gtk_editable_set_position(entry, 3);
            c.Pump(200);
            c.Pre(Selection(entry) == (3, 3), $"the cursor at 3, nothing selected, before; {Selection(entry)}");
            Tap(entry);
            c.Pump(300);
            Tap(entry);
            c.Pre(c.PumpUntil(() => Marshal.PtrToStringUTF8(N.gtk_editable_get_text(entry)) == "fix Hello from the fake whisper. the bug", 6000), "the text in");
            var (from, to) = Selection(entry);
            c.Expect(from == to && to == "fix Hello from the fake whisper.".Length, $"the cursor right after what was heard, nothing selected; the selection is {from}..{to}");
        });

        Test("voice: the mic and alt+v while transcribing do nothing", c =>
        {
            VoiceFakes(c, whisperMode: "slow");
            c.Start();
            var (entry, mic) = (c.Get<nint>("talkEntry"), c.Get<nint>("talkMic"));
            Tap(entry);
            c.Pump(300);
            Tap(entry);
            c.Pre(c.PumpUntil(() => G.Text(c.Get<nint>("talkVoice")) == "transcribing…", 2000), "transcribing");
            G.ClickMiddle(mic);
            Tap(entry);
            c.Pump(300);
            c.Expect(VoiceLog(c).Count(l => l.StartsWith("start ")) == 1, "no second recording; log: " + string.Join(" | ", VoiceLog(c)));
            c.Expect(c.PumpUntil(() => Marshal.PtrToStringUTF8(N.gtk_editable_get_text(entry)) == "Hello from the fake whisper.", 6000), "the one text goes in");
        });

        Test("voice: the text goes in right at the cursor after an emoji, and at the start and the end", c =>
        {
            VoiceFakes(c);
            c.Start();
            var entry = c.Get<nint>("talkEntry");
            foreach (var (text, at, want) in new[]
            {
                ("héllo 🐝world", 7, "héllo 🐝 Hello from the fake whisper. world"),
                ("start", 0, "Hello from the fake whisper. start"),
                ("end ", 4, "end Hello from the fake whisper."),
            })
            {
                N.gtk_editable_set_text(entry, text);
                N.gtk_editable_set_position(entry, at);
                Tap(entry);
                c.Pump(200);
                Tap(entry);
                c.Expect(c.PumpUntil(() => Marshal.PtrToStringUTF8(N.gtk_editable_get_text(entry)) == want, 6000), $"\"{want}\"; it says \"{Marshal.PtrToStringUTF8(N.gtk_editable_get_text(entry))}\"");
                c.Pump(300);
            }
        });

        foreach (var (what, whisperMode, pwMode, said) in new[]
        {
            ("whisper-cli fails: its last error line, in the status line", "fail", "ok", "whisper-cli failed: whisper_init: failed to load model"),
            ("whisper-cli hears nothing ([BLANK_AUDIO], (silence)): Nothing was heard.", "empty", "ok", "Nothing was heard."),
            ("pw-record records nothing (an empty wav): Nothing was recorded", "ok", "empty", "Nothing was recorded; is a microphone connected?"),
        })
            Test("voice: " + what + "; the prompt is left as it was", c =>
            {
                var rig = VoiceFakes(c, whisperMode: whisperMode, pwMode: pwMode);
                c.Start();
                var entry = c.Get<nint>("talkEntry");
                N.gtk_editable_set_text(entry, "as it was");
                Tap(entry);
                c.Pump(300);
                Tap(entry);
                c.Expect(c.PumpUntil(() => G.Text(c.Get<StatusLine>("status").Message).Contains(said), 6000), $"the status line says \"{said}\"; it says \"{G.Text(c.Get<StatusLine>("status").Message)}\"");
                c.Expect(Marshal.PtrToStringUTF8(N.gtk_editable_get_text(entry)) == "as it was", "the prompt unchanged");
                c.Expect(!File.Exists(Path.Combine(rig.Runtime, $"hive-dictation-{Environment.ProcessId}.wav")), "the wav deleted");
                c.Expect(G.gtk_widget_get_visible(c.Get<nint>("talkVoice")) == 0, "the ● label gone");
            });

        Test("voice: idle after a dictation: no pw-record or whisper-cli alive, and the voice tick stops", c =>
        {
            VoiceFakes(c);
            c.Start();
            var entry = c.Get<nint>("talkEntry");
            Tap(entry);
            c.Pump(300);
            Tap(entry);
            c.Pre(c.PumpUntil(() => Marshal.PtrToStringUTF8(N.gtk_editable_get_text(entry)) == "Hello from the fake whisper.", 6000), "a dictation done");
            c.Pump(600);
            // Any VoiceTick still going re-arms through this field: count those.
            var ticks = 0;
            var tick = c.Get<Action>("voiceTick");
            c.SetField("voiceTick", (Action)(() => { ticks++; tick(); }));
            c.Pump(1500);
            c.Expect(ticks == 0, $"no voice tick while idle; {ticks} in 1.5 s");
            c.Expect(FakeProcesses(c).Count == 0, "no fake process alive: " + string.Join(" | ", FakeProcesses(c)));
        });

        // The real App.Run in a process of its own (VoiceApp): it starts a
        // recording with the mic, then closes its window, or is killed.
        Test("voice: closing the window during a recording stops pw-record (SIGINT) and leaves no wav", c =>
        {
            var rig = VoiceFakes(c);
            var (app, pid) = StartVoiceApp(c, "close");
            using var _ = app;
            try
            {
                c.Expect(app.WaitForExit(15000), "the app ends when its window closes");
                c.Expect(c.PumpUntil(() => !Directory.Exists($"/proc/{pid}"), 4000), $"pw-record ({pid}) gone after the window closed; log: " + string.Join(" | ", VoiceLog(c)));
                c.Expect(VoiceLog(c).Contains($"INT {pid}"), "stopped with SIGINT; log: " + string.Join(" | ", VoiceLog(c)));
                c.Expect(!Directory.EnumerateFiles(rig.Runtime, "hive-dictation-*.wav").Any(), "no wav left in the runtime folder");
                app.WaitForExit(); // the output read to its end
                var critical = voiceAppLines.Where(l => l.Contains("CRITICAL")).ToList();
                c.Expect(critical.Count == 0, "no GTK CRITICAL while the window closes (a widget touched after it was freed); got: " + string.Join(" | ", critical));
            }
            finally { KillPid(pid); }
        });

        Test("voice: a killed window (SIGKILL) during a recording leaves no pw-record recording", c =>
        {
            VoiceFakes(c);
            var (app, pid) = StartVoiceApp(c, "hold");
            using var _ = app;
            try
            {
                app.Kill(); // SIGKILL, as a crash or `kill -9` does
                c.Pre(app.WaitForExit(5000), "the app killed");
                c.Expect(c.PumpUntil(() => !Directory.Exists($"/proc/{pid}"), 4000), $"pw-record ({pid}) still recording 4 s after its window was killed");
            }
            finally { KillPid(pid); }
        });

        // ---- stage 6: a bee's role and settings on its page, the team dialog ----

        Test("roles: the Role row on a bee's page sets keeper, refuses an unknown role (saying so), and empty takes it away", c =>
        {
            c.Start();
            var row = BeeRow(c, "Worker", "Role:");
            N.gtk_editable_set_text(row, "keeper");
            G.g_signal_emit_by_name(row, "apply", 0);
            c.Pump(300);
            c.Expect(c.Outside.RequireBee("Worker").Doc.Get("role") == "keeper", "Worker the keeper; errors: " + string.Join(" | ", c.Errors));
            row = BeeRow(c, "Worker", "Role:");
            N.gtk_editable_set_text(row, "boss");
            G.g_signal_emit_by_name(row, "apply", 0);
            c.Pump(300);
            c.Expect(c.Outside.RequireBee("Worker").Doc.Get("role") == "keeper", "still keeper after boss");
            c.Expect(c.Errors.Any(e => e.Contains("role")), "an error naming the roles; errors: " + string.Join(" | ", c.Errors));
            row = BeeRow(c, "Worker", "Role:");
            N.gtk_editable_set_text(row, "");
            G.g_signal_emit_by_name(row, "apply", 0);
            c.Pump(300);
            c.Expect(c.Outside.RequireBee("Worker").Doc.Get("role") == "", "no role after an empty apply");
        });

        Test("settings: the bee page's switch turns deposit_before_stop on and off", c =>
        {
            c.Start();
            var row = BeeRow(c, "Worker", "Sent back once", "AdwSwitchRow");
            N.adw_switch_row_set_active(row, true);
            c.Pump(300);
            c.Expect(c.Outside.RequireBee("Worker").Doc.Get("deposit_before_stop") == "yes", "on: deposit_before_stop: yes");
            row = BeeRow(c, "Worker", "Sent back once", "AdwSwitchRow");
            N.adw_switch_row_set_active(row, false);
            c.Pump(300);
            c.Expect(c.Outside.RequireBee("Worker").Doc.Get("deposit_before_stop") == "", "off: no setting");
        });

        foreach (var (what, reviewers, overseer, ok) in new[] { ("two reviewers and an overseer", "Ash, Birch", "Arbiter", true), ("no overseer", "Ash, Birch", "", false), ("no reviewers", "", "Arbiter", false) })
            Test($"team setup: the Reviews page's dialog with {what} {(ok ? "makes the team" : "is refused and makes nothing")}", c =>
            {
                c.Start();
                c.Call("Go", "reviews");
                c.Pump(300);
                var window = c.Get<nint>("window");
                var open = G.Button(window, "Set up the review team");
                c.Pre(open != 0, "Set up the review team on the empty Reviews page");
                G.g_signal_emit_by_name(open, "clicked", 0);
                c.Pump(1000);
                var dialog = G.OfType(window, "AdwAlertDialog").FirstOrDefault();
                c.Pre(dialog != 0, "the team dialog");
                var rows = G.OfType(dialog, "AdwEntryRow").ToList();
                c.Pre(rows.Count == 2, $"two rows; {rows.Count}");
                N.gtk_editable_set_text(rows[0], reviewers);
                N.gtk_editable_set_text(rows[1], overseer);
                var create = G.Button(window, "Set up the team");
                c.Pre(create != 0, "the Set up the team button");
                G.g_signal_emit_by_name(create, "clicked", 0);
                c.Pump(600);
                if (ok)
                    c.Expect(c.Outside.FindBee("Ash")?.Doc.Get("role") == "reviewer" && c.Outside.FindBee("Birch")?.Doc.Get("role") == "reviewer" && c.Outside.FindBee("Arbiter")?.Doc.Get("role") == "overseer",
                             "Ash and Birch reviewers, Arbiter the overseer; errors: " + string.Join(" | ", c.Errors));
                else
                {
                    c.Expect(c.Outside.Bees().All(b => b.Name == "Worker"), "no bee made: " + string.Join(", ", c.Outside.Bees().Select(b => b.Name)));
                    c.Expect(c.Errors.Count > 0, "an error said");
                }
            });

        Test("queen: a Queen turn and a talk turn (from before the pane) don't wait in the inbox", c =>
        {
            var queen = Tasks.QueenTalk(c.Store, "hello", "", "edit", null);
            Tasks.Record(c.Store, queen, "Hi.", "done");
            var talk = Tasks.Create(c.Store, "Worker", "hello", "", "read", role: "talk"); // as 0.1.02's window left them
            Tasks.Record(c.Store, talk, "Hi.", "done");
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            Tasks.Record(c.Store, task, "Found it.", "done");
            c.Start();
            c.Expect(c.Inbox() == 1, $"only the plain task in the inbox, got {c.Inbox()}");
        });

        // ---- work pane, stage wp3: bee tabs for the pane Queen's subagents ----
        // The files are
        // faked as Claude Code 2.1.284 writes them (the Mason's wp3 worklog):
        // ~/.claude/projects/-p/sess-1.jsonl (the Queen), and
        // sess-1/subagents/agent-<id>.meta.json + agent-<id>.jsonl. Each write
        // gives the folder a new time, as a real write does. c.Poke() calls
        // UpdateTalk, what a refresh or a new step runs (named: a fix keeps it
        // or updates Poke).

        Test("bee tabs: a bee the Queen starts in the foreground gets a tab with its live transcript, watch-only", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"get Worker on it"}}""");
            RunningQueen(c, "sess-1");
            c.Start();
            Subagent("sess-1", "a1", "worker", "Look into A.cs");
            c.Poke();
            var chips = c.BeeChips();
            c.Expect(chips.Count == 1 && G.Says(chips[0], "Watch Worker: Look into A.cs"), $"one tab, \"Watch Worker: Look into A.cs\"; {chips.Count} tabs");
            c.Expect(c.ChipGlyph(chips[0]) is { Length: 1 } g && "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏⠿".Contains(g), "a spinner on it; it shows " + c.ChipGlyph(chips[0]));
            G.g_signal_emit_by_name(chips[0], "clicked", 0);
            c.Pump(300);
            var labels = c.TalkLabels();
            c.Expect(labels.Contains("Look into A.cs") && labels.Contains("Read(src/A.cs)"), "its transcript: ❯ Look into A.cs, ● Read(src/A.cs); labels: " + string.Join(" | ", labels));
            c.Expect(G.gtk_widget_get_visible(c.Get<nint>("talkPrompt")) == 0 && G.gtk_widget_get_visible(c.Get<nint>("talkWatch")) != 0, "no prompt, the watching footer instead");
            c.Expect(G.Text(c.Get<nint>("talkWatchText")).StartsWith("watching Worker"), "the footer says watching Worker; it says " + G.Text(c.Get<nint>("talkWatchText")));
            File.AppendAllText(Path.Combine(SubagentsDir("sess-1"), "agent-a1.jsonl"), """{"type":"assistant","isSidechain":true,"message":{"content":[{"type":"text","text":"Found the cause."}]}}""" + "\n");
            c.Poke();
            c.Expect(c.TalkLabels().Contains("Found the cause."), "a new line in its transcript shows; labels: " + string.Join(" | ", c.TalkLabels()));
        });

        Test("bee tabs: a bee started in the background gets a tab too, working until its notification", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"in the background"}}""");
            RunningQueen(c, "sess-1");
            c.Start();
            Subagent("sess-1", "a1", "worker", "Look into A.cs", shape: "background");
            QueenEnd("sess-1", "a1", "async_launched");
            c.Poke();
            var chips = c.BeeChips();
            c.Expect(chips.Count == 1 && c.ChipGlyph(chips[0]) is not ("✓" or "✗"), $"one tab, still working; {chips.Count} tabs" + (chips.Count > 0 ? ", it shows " + c.ChipGlyph(chips[0]) : ""));
            QueenNotified("sess-1", "a1", "completed");
            c.Poke();
            c.Expect(c.ChipGlyph(c.BeeChips()[0]) == "✓", "✓ once its notification says completed; it shows " + c.ChipGlyph(c.BeeChips()[0]));
        });

        Test("bee tabs: control: a general-purpose or other non-bee agent gets no tab", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"look around"}}""");
            RunningQueen(c, "sess-1");
            c.Start();
            Subagent("sess-1", "g1", "general-purpose", "Search the code");
            Subagent("sess-1", "e1", "Explore", "Find files");
            Subagent("sess-1", "a1", "worker", "Look into A.cs");
            c.Poke();
            var chips = c.BeeChips();
            c.Expect(chips.Count == 1 && G.Says(chips[0], "Watch Worker: Look into A.cs"), $"only Worker's tab; tabs: {chips.Count}");
        });

        Test("bee tabs: ✓ when its Agent call completed, and the footer says it's done", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"go"}}""");
            RunningQueen(c, "sess-1");
            c.Start();
            Subagent("sess-1", "a1", "worker", "Look into A.cs");
            c.Poke();
            QueenEnd("sess-1", "a1", "completed");
            c.Poke();
            var chip = c.BeeChips().Single();
            c.Expect(c.ChipGlyph(chip) == "✓", "✓; it shows " + c.ChipGlyph(chip));
            G.g_signal_emit_by_name(chip, "clicked", 0);
            c.Pump(300);
            c.Expect(G.Text(c.Get<nint>("talkWatchText")) == "Worker is done; its report went to the Queen", "the footer; it says " + G.Text(c.Get<nint>("talkWatchText")));
        });

        foreach (var how in new[] { "failed", "killed" })
            Test($"bee tabs: ✗ when its notification says {how}", c =>
            {
                QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"go"}}""");
                RunningQueen(c, "sess-1");
                c.Start();
                Subagent("sess-1", "a1", "worker", "Look into A.cs", shape: "background");
                QueenEnd("sess-1", "a1", "async_launched");
                c.Poke();
                QueenNotified("sess-1", "a1", how);
                c.Poke();
                var chip = c.BeeChips().Single();
                c.Expect(c.ChipGlyph(chip) == "✗", "✗; it shows " + c.ChipGlyph(chip));
                G.g_signal_emit_by_name(chip, "clicked", 0);
                c.Pump(300);
                c.Expect(G.Text(c.Get<nint>("talkWatchText")) == "Worker failed; the Queen has what went wrong", "the footer; it says " + G.Text(c.Get<nint>("talkWatchText")));
            });

        Test("bee tabs: esc stops the Queen's turn, and a bee still working shows ✗ stopped with her turn", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"go"}}""");
            var turn = RunningQueen(c, "sess-1");
            c.Start();
            Subagent("sess-1", "a1", "worker", "Look into A.cs");
            c.Poke();
            c.Pre(G.PressKey(c.Get<nint>("talkEntry"), 0xff1b) == true, "the entry to take esc");
            c.Pre(Tasks.Load(c.Store, turn.Id).Status == "stopped", "her turn stopped");
            c.Poke();
            var chip = c.BeeChips().Single();
            c.Expect(c.ChipGlyph(chip) == "✗", "✗; it shows " + c.ChipGlyph(chip));
            G.g_signal_emit_by_name(chip, "clicked", 0);
            c.Pump(300);
            c.Expect(G.Text(c.Get<nint>("talkWatchText")) == "Worker stopped with the Queen's turn", "the footer; it says " + G.Text(c.Get<nint>("talkWatchText")));
        });

        Test("bee tabs: a finished tab stays until ✕ close, and doesn't come back", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"go"}}""");
            RunningQueen(c, "sess-1");
            c.Start();
            Subagent("sess-1", "a1", "worker", "Look into A.cs");
            c.Poke();
            QueenEnd("sess-1", "a1", "completed");
            for (int i = 0; i < 3; i++) c.Poke();
            c.Expect(c.BeeChips().Count == 1, "the ✓ tab still there");
            G.g_signal_emit_by_name(c.BeeChips()[0], "clicked", 0);
            c.Pump(300);
            c.Click(G.Button(c.Get<nint>("talkPanel"), "✕ close"));
            c.Expect(c.BeeChips().Count == 0 && G.gtk_widget_get_visible(c.Get<nint>("talkPrompt")) != 0, "the tab closed, back on the Queen's");
            Subagent("sess-1", "b1", "worker", "Another look"); // the folder changes again
            c.Poke();
            c.Expect(c.BeeChips().Count == 1 && G.Says(c.BeeChips()[0], "Watch Worker: Another look"), "only the new bee's tab, not the closed one");
        });

        Test("bee tabs: + new clears the finished tabs", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"go"}}""");
            c.Start();
            Subagent("sess-1", "a1", "worker", "Look into A.cs");
            QueenEnd("sess-1", "a1", "completed");
            c.Poke();
            c.Pre(c.BeeChips().Count == 1, "a ✓ tab");
            c.Click(G.Button(c.Get<nint>("talkPanel"), "+ new"));
            c.Expect(c.BeeChips().Count == 0, $"no bee tabs; {c.BeeChips().Count}");
        });

        Test("bee tabs: closing a working bee's tab doesn't stop it or her turn", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"go"}}""");
            var turn = RunningQueen(c, "sess-1");
            c.Start();
            Subagent("sess-1", "a1", "worker", "Look into A.cs");
            c.Poke();
            G.g_signal_emit_by_name(c.BeeChips()[0], "clicked", 0);
            c.Pump(300);
            c.Click(G.Button(c.Get<nint>("talkPanel"), "✕ close"));
            c.Expect(c.BeeChips().Count == 0, "the tab closed");
            var t = Tasks.Load(c.Store, turn.Id);
            c.Expect(t.Running && t.Status == "running", "her turn still running; it's " + t.Status);
        });

        Test("bee tabs: a working bee's tab is back after a window restart", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"go"}}""");
            RunningQueen(c, "sess-1");
            Subagent("sess-1", "a1", "worker", "Look into A.cs"); // before this window started
            c.Start();
            c.Poke();
            c.Expect(c.BeeChips().Count == 1, $"Worker's tab; {c.BeeChips().Count} tabs");
        });

        Test("bee tabs: a finished tab nobody closed is back after a window restart", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"go"}}""");
            Subagent("sess-1", "a1", "worker", "Look into A.cs");
            QueenEnd("sess-1", "a1", "completed"); // done before this window started, its ✓ tab never closed
            c.Start();
            c.Poke();
            c.Expect(c.BeeChips().Count == 1, $"the ✓ tab kept until closed or \"new\" (decision: kept with ✓/✗ until closed); {c.BeeChips().Count} tabs");
        });

        Test("bee tabs: a bee whose meta file lands in the same tick as the last look still gets a tab", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"go"}}""");
            RunningQueen(c, "sess-1");
            c.Start();
            Subagent("sess-1", "a1", "worker", "Look into A.cs");
            c.Poke();
            var dir = SubagentsDir("sess-1");
            var seen = Directory.GetLastWriteTimeUtc(dir);
            Subagent("sess-1", "a2", "worker", "And B.cs");
            Directory.SetLastWriteTimeUtc(dir, seen); // the folder's time didn't move (a coarse clock tick)
            c.Poke();
            c.Pump(500);
            c.Poke();
            c.Expect(c.BeeChips().Count == 2, $"tabs for both; {c.BeeChips().Count}");
        });

        Test("bee tabs: a meta file read while half written gets its tab once it's whole", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"go"}}""");
            RunningQueen(c, "sess-1");
            c.Start();
            var dir = SubagentsDir("sess-1");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "agent-a1.jsonl"), "");
            File.WriteAllText(Path.Combine(dir, "agent-a1.meta.json"), """{"agentType":"wor""");
            Bump(dir);
            c.Poke();
            c.Pre(c.BeeChips().Count == 0, "no tab from half a file");
            File.WriteAllText(Path.Combine(dir, "agent-a1.meta.json"), """{"agentType":"worker","description":"Look into A.cs","toolUseId":"tu-a1","requestShape":"foreground"}"""); // rewriting a file doesn't change the folder's time
            c.Poke();
            c.Pump(500);
            c.Poke();
            c.Expect(c.BeeChips().Count == 1, $"Worker's tab once its meta is whole; {c.BeeChips().Count} tabs");
        });

        Test("bee tabs: a bee's own subagent (not the Queen's) gets no tab", c =>
        {
            c.Store.CreateBee("Wren", "Tests the work", [], "", "approve");
            QueenSaid(c, "sess-1", "/p", """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"tu-a1","name":"Agent","input":{"subagent_type":"worker","prompt":"Look into A.cs"}}]}}""");
            RunningQueen(c, "sess-1");
            c.Start();
            Subagent("sess-1", "a1", "worker", "Look into A.cs", records: [
                """{"type":"user","isSidechain":true,"message":{"role":"user","content":"Look into A.cs"}}""",
                """{"type":"assistant","isSidechain":true,"message":{"content":[{"type":"tool_use","id":"tu-n1","name":"Agent","input":{"subagent_type":"wren","prompt":"Test A.cs"}}]}}"""]);
            Subagent("sess-1", "n1", "wren", "Test A.cs", toolUse: "tu-n1"); // Worker's Agent call, not the Queen's
            c.Poke();
            var chips = c.BeeChips();
            c.Expect(chips.Count == 1 && G.Says(chips[0], "Watch Worker: Look into A.cs"), "only Worker's tab (the decision: only the pane Queen's subagents get tabs); tabs: " + string.Join(", ", chips.Select(ch => G.Says(ch, "Watch Wren: Test A.cs") ? "Wren" : "Worker")));
        });

        // ---- the Gauge: a long transcript ----
        // The Queen's session resumes until "new", so her transcript only grows.
        // The tab shows the last 300 lines but kept every line (and every tool
        // call) in memory. What must hold: a bounded number of lines kept, the
        // latest lines shown, a tool call still waiting for its result (from
        // long ago) still gets its diff when the result comes.

        static string[] LongTranscript(int n, bool sidechain)
        {
            var side = sidechain ? "\"isSidechain\":true," : "";
            var r = new List<string>
            {
                "{\"type\":\"assistant\"," + side + "\"message\":{\"model\":\"claude-opus-5-5\",\"content\":[{\"type\":\"tool_use\",\"id\":\"t-early\",\"name\":\"Edit\",\"input\":{\"file_path\":\"/p/src/Early.cs\",\"old_string\":\"early();\",\"new_string\":\"later();\"}}]}}",
            };
            for (int i = 0; i < n; i++)
                r.Add(i % 2 == 0
                    ? "{\"type\":\"user\"," + side + "\"message\":{\"role\":\"user\",\"content\":\"question " + i + "\"}}"
                    : "{\"type\":\"assistant\"," + side + "\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"answer " + i + "\"},{\"type\":\"tool_use\",\"id\":\"t" + i + "\",\"name\":\"Bash\",\"input\":{\"command\":\"echo " + i + "\"}}]}}");
            // each Bash call gets its result at once, so only t-early waits
            var withResults = new List<string>();
            foreach (var line in r)
            {
                withResults.Add(line);
                if (line.Contains("\"name\":\"Bash\"") && System.Text.RegularExpressions.Regex.Match(line, "\"id\":\"(t\\d+)\"") is { Success: true } m)
                    withResults.Add("{\"type\":\"user\"," + side + "\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"" + m.Groups[1].Value + "\",\"content\":\"ok\"}]}}");
            }
            return [.. withResults];
        }

        Test("long transcript: the Queen's tab keeps a bounded number of lines, shows the latest, and a long-waiting tool call still gets its result", c =>
        {
            QueenSaid(c, "sess-1", "/p", LongTranscript(5000, false));
            c.Start();
            var talk = c.Get<object>("talk");
            var lines = ((System.Collections.ICollection)c.Field(talk, "Lines")!).Count;
            var tools = ((System.Collections.ICollection)c.Field(talk, "Tools")!).Count;
            var labels = c.TalkLabels();
            c.Pre(labels.Contains("answer 4999"), "the latest line shown");
            c.Expect(lines <= 400 && tools <= 10, $"at most 400 lines and 10 waiting tool calls kept; it keeps {lines} lines and {tools} tool calls (5,000 records)");
            File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("HOME")!, ".claude", "projects", "-p", "sess-1.jsonl"),
                """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t-early","content":"ok"}]}}""" + "\n");
            c.Poke();
            labels = c.TalkLabels();
            c.Expect(labels.Contains("Updated with 1 addition and 1 removal") && labels.Contains("early();") && labels.Contains("later();"), "the early Edit's result with its diff; last labels: " + string.Join(" | ", labels.TakeLast(6)));
        });

        Test("long transcript: a bee's tab keeps a bounded number of lines too", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"go"}}""");
            RunningQueen(c, "sess-1");
            c.Start();
            Subagent("sess-1", "a1", "worker", "Look into A.cs", records: LongTranscript(5000, true));
            c.Poke();
            var tab = c.Get<System.Collections.IList>("beeTabs").Cast<object>().Single();
            G.g_signal_emit_by_name(c.BeeChips()[0], "clicked", 0);
            c.Pump(300);
            c.Pre(c.TalkLabels().Contains("answer 4999"), "the latest line shown in Worker's tab");
            var lines = ((System.Collections.ICollection)c.Field(tab, "Lines")!).Count;
            c.Expect(lines <= 400, $"at most 400 lines kept; it keeps {lines} (5,000 records)");
        });

        // ---- team review review rv-d (the work pane) ----
        // "wp-rv #n" is the finding number in the Arbiter's report.
        // Built on Cedar's
        // and Birch's probes.

        // wp-rv #1: in a fresh conversation, the session's arrival clears the
        // pane, and the transcript's copy of the first message matches Echo.
        Test("wp-rv #1 your first message in a fresh conversation stays once her session comes (alone)", c =>
        {
            c.Start();
            c.Say("first words");
            c.Pre(c.TalkLabels().Contains("first words"), "shown when sent");
            SessionCame(c, "sess-fresh", """{"type":"user","message":{"role":"user","content":"first words\n\n(The user is talking with you, the Queen, x.)"}}""");
            c.Expect(c.TalkLabels().Contains("first words"), "still shown once the session came; labels: " + string.Join(" | ", c.TalkLabels()));
            c.Call("ShowTalkTab"); // as switching to a bee's tab and back does
            c.Pump(200);
            c.Expect(c.TalkLabels().Contains("first words"), "still shown when the tab is drawn again; labels: " + string.Join(" | ", c.TalkLabels()));
        });

        Test("wp-rv #1 your first message in a fresh conversation stays when her session comes with her answer", c =>
        {
            c.Start();
            c.Say("first words");
            SessionCame(c, "sess-fresh", """{"type":"user","message":{"role":"user","content":"first words\n\n(The user is talking with you, the Queen, x.)"}}""",
                """{"type":"assistant","message":{"content":[{"type":"text","text":"Hello there."}]}}""");
            var labels = c.TalkLabels();
            c.Expect(labels.Contains("first words") && labels.Contains("Hello there."), "your message and her answer; labels: " + string.Join(" | ", labels));
        });

        Test("wp-rv #1 a message queued during the first turn of a fresh conversation stays", c =>
        {
            c.Start();
            c.Say("first words");
            var turn = Tasks.List(c.Store, Tasks.Queen).First();
            turn.Doc.Set("status", "running");
            turn.Doc.Set("pid", Runner(turn.Id));
            turn.Doc.Set("started", Text.Now);
            turn.Doc.Save(turn.Path);
            c.Call("RefreshHive");
            c.Say("and then this");
            c.Pre(c.TalkLabels().Any(l => l.StartsWith("and then this") && l.Contains("sent when she's done")), "the queued line; labels: " + string.Join(" | ", c.TalkLabels()));
            SessionCame(c, "sess-fresh", """{"type":"user","message":{"role":"user","content":"first words\n\n(The user is talking with you, the Queen, x.)"}}""");
            c.Call("ShowTalkTab"); // as switching to a bee's tab and back does (until then the old rows stay on screen)
            c.Pump(200);
            c.Expect(c.TalkLabels().Any(l => l.StartsWith("and then this")), "the queued message still shown; labels: " + string.Join(" | ", c.TalkLabels()));
            c.Expect(c.Field(c.Get<object>("talk"), "Pending") is "and then this", "still queued to send");
        });

        // wp-rv #2: Claude Code writes a notification that comes while the session
        // is busy as an attachment (queued_command), not a user record.
        foreach (var asAttachment in new[] { true, false })
            Test(asAttachment ? "wp-rv #2 a background bee whose notification came as a queued_command attachment ends ✓"
                              : "wp-rv #2 control: a background bee whose notification came as a user record ends ✓", c =>
            {
                QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"in the background"}}""");
                var turn = RunningQueen(c, "sess-1");
                c.Start();
                Subagent("sess-1", "a1", "worker", "Look into A.cs", shape: "background");
                QueenEnd("sess-1", "a1", "async_launched");
                c.Poke();
                c.Pre(c.BeeChips().Count == 1, "one tab");
                if (asAttachment)
                    File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("HOME")!, ".claude", "projects", "-p", "sess-1.jsonl"),
                        """{"type":"attachment","isSidechain":false,"attachment":{"type":"queued_command","prompt":"<task-notification>\n<task-id>a1</task-id>\n<tool-use-id>tu-a1</tool-use-id>\n<status>completed</status>\n<summary>Agent \"Look into A.cs\" finished</summary>\n</task-notification>","commandMode":"task-notification"}}""" + "\n");
                else QueenNotified("sess-1", "a1", "completed");
                Tasks.Record(c.Outside, Tasks.Load(c.Outside, turn.Id), "Done.", "done"); // her turn ends after it
                c.Call("RefreshHive");
                c.Pump(300);
                c.Expect(c.ChipGlyph(c.BeeChips()[0]) == "✓", "✓; it shows " + c.ChipGlyph(c.BeeChips()[0]));
            });

        // wp-rv #3: opening an item from ▤ leaves the popover open over the page.
        Test("wp-rv #3 read on a card change in the ▤ popover opens the bee's page and closes the popover", c =>
        {
            c.Store.Molt("Worker", "a sharper card", "# Worker\n\nA new card.\n", "queen");
            c.Start();
            c.OpenInbox();
            c.Click(G.Button(c.Get<nint>("inboxPop"), "read"));
            c.Pre(c.PumpUntil(() => c.Page() == "bees"), "Worker's page; the page is " + c.Page());
            c.Expect(c.PumpUntil(() => G.gtk_widget_get_visible(c.Get<nint>("inboxPop")) == 0, 1500), "the popover closed");
        });

        Test("wp-rv #3 open on a failed task in the ▤ popover shows the task and closes the popover", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            Tasks.Record(c.Store, task, "It broke.", "failed");
            c.Start();
            c.OpenInbox();
            c.Click(G.Button(c.Get<nint>("inboxPop"), "open"));
            c.Pre(c.PumpUntil(() => c.Get<object?>("taskView") != null), "the task's view open");
            c.Expect(c.PumpUntil(() => G.gtk_widget_get_visible(c.Get<nint>("inboxPop")) == 0, 1500), "the popover closed");
        });

        // wp-rv #4: the filter for Claude Code's <task-notification> drops any message starting with <.
        Test("wp-rv #4 a message that starts with < is still in the conversation after a restart", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"<div> is not centred\n\n(The user is talking with you, the Queen, x.)"}}""",
                """{"type":"assistant","message":{"content":[{"type":"text","text":"Let me look."}]}}""");
            c.Start();
            c.Expect(c.TalkLabels().Contains("<div> is not centred"), "your message; labels: " + string.Join(" | ", c.TalkLabels()));
        });

        Test("wp-rv #4 control: a <task-notification> user record is still not shown as yours", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"<task-notification>\n<task-id>a1</task-id>\n<status>completed</status>\n</task-notification>"}}""",
                """{"type":"assistant","message":{"content":[{"type":"text","text":"Worker is done."}]}}""");
            c.Start();
            c.Expect(!c.TalkLabels().Any(l => l.Contains("task-notification") || l.Contains("<task-id>")) && c.TalkLabels().Contains("Worker is done."), "no notification line; labels: " + string.Join(" | ", c.TalkLabels()));
        });

        // wp-rv #5: Tasks.Create refuses a project that's gone; "~" in the picker fires nothing.
        Test("wp-rv #5 a conversation whose project is gone: the next message runs in ~", c =>
        {
            Directory.CreateDirectory(c.Path("code", "gone"));
            c.Store.SetProject("gone", "Gone soon", [c.Path("code", "gone")], null);
            var from = QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"hi"}}""");
            from.Doc.Set("project", "gone");
            from.Doc.Set("folder", Text.Tilde(c.Path("code", "gone")));
            from.Doc.Save(from.Path);
            Directory.Delete(c.Path("projects", "gone"), true); // the project removed since
            c.Pre(c.Store.FindProject("gone") == null, "the project gone");
            c.Start();
            c.Say("still there?");
            var turn = Tasks.List(c.Store, Tasks.Queen).FirstOrDefault(t => t.Prompt == "still there?");
            c.Expect(turn != null && turn.Doc.Get("project") == "" && turn.Doc.Get("folder") == "~", $"a turn in ~ with no project; errors: {string.Join(" | ", c.Errors)}" + (turn != null ? $"; it's project \"{turn.Doc.Get("project")}\", folder {turn.Doc.Get("folder")}" : ""));
        });

        // wp-rv #6: esc empties Pending but the line still promises to send it.
        Test("wp-rv #6 esc with a queued message: the line no longer says it will be sent", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"go"}}""");
            RunningQueen(c, "sess-1");
            c.Start();
            c.Say("and then this");
            c.Pre(c.TalkLabels().Any(l => l.Contains("sent when she's done")), "the queued line");
            c.Pre(G.PressKey(c.Get<nint>("talkEntry"), 0xff1b) == true, "the entry to take esc");
            c.Pump(300);
            c.Expect(!c.TalkLabels().Any(l => l.Contains("sent when she's done")), "no \"(sent when she's done)\" left; labels: " + string.Join(" | ", c.TalkLabels()));
            c.Expect((string)c.Field(c.Get<object>("talk"), "Pending")! == "", "nothing queued");
        });

        // wp-rv #7: a bee's tab shows the Queen's prompt as if you said it.
        Test("wp-rv #7 in a bee's tab, the Queen's prompt is hers, not yours", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"go"}}""");
            RunningQueen(c, "sess-1");
            c.Start();
            Subagent("sess-1", "a1", "worker", "Look into A.cs");
            c.Poke();
            G.g_signal_emit_by_name(c.BeeChips()[0], "clicked", 0);
            c.Pump(300);
            var rows = G.OfType(c.Get<nint>("talkLines"), "GtkBox").Where(b => G.gtk_widget_has_css_class(b, "talk-line") != 0).ToList();
            var first = rows.FirstOrDefault(r => G.OfType(r, "GtkLabel").Select(G.Text).Contains("Look into A.cs"));
            c.Pre(first != 0, "the prompt's line in Worker's tab");
            c.Expect(!G.Says(first, "You: Look into A.cs") && G.Heard(first).Contains("Queen"), "heard as the Queen's; it's heard as \"" + G.Heard(first) + "\"");
            c.Expect(!G.OfType(first, "GtkLabel").Select(G.Text).Contains("❯"), "not drawn with your ❯");
        });

        // The Gauge:
        // startup parses every transcript whole. Bytes read are counted with
        // /proc/self/io (rchar) around opening the window.
        foreach (var big in new[] { false, true })
            Test(big ? "startup: opening the window with a 4.8 MB Queen transcript and 10 bee tabs reads a bounded part of them, and an early waiting tool call still gets its result"
                     : "startup: control: with short transcripts the window reads almost nothing of them", c =>
            {
                var pad = new string('x', 1500);
                string[] queen = big ? LongTranscript(5000, false).Select(l => l.Replace("\"answer ", "\"" + pad + " answer ")).ToArray() : ["""{"type":"user","message":{"role":"user","content":"go"}}"""];
                QueenSaid(c, "sess-1", "/p", queen);
                RunningQueen(c, "sess-1");
                for (int i = 0; i < 10; i++) Subagent("sess-1", "a" + i, "worker", "Look " + i, records: big ? LongTranscript(2000, true) : null);
                var dir = Path.Combine(Environment.GetEnvironmentVariable("HOME")!, ".claude", "projects", "-p");
                var queenSize = new FileInfo(Path.Combine(dir, "sess-1.jsonl")).Length;
                var size = queenSize + Directory.GetFiles(Path.Combine(dir, "sess-1", "subagents")).Sum(f => new FileInfo(f).Length);
                var before = Rchar();
                var sw = Stopwatch.StartNew();
                c.Start(pumpMs: 0);
                var (read, ms) = (Rchar() - before, sw.ElapsedMilliseconds);
                c.Pump(800);
                c.Pre(c.Get<System.Collections.IList>("beeTabs").Count == 10, "10 bee tabs");
                var note = $"transcripts {size / 1e6:0.0} MB (the Queen's {queenSize / 1e6:0.0} MB), opening read {read / 1e6:0.00} MB in {ms} ms";
                Console.WriteLine("      " + note);
                c.Expect(read < (big ? size / 4 : 200_000), (big ? "under a quarter of them read" : "under 0.2 MB read") + "; " + note);
                if (!big) return;
                c.Pre(c.TalkLabels().Contains(pad + " answer 4999"), "the latest line shown");
                File.AppendAllText(Path.Combine(dir, "sess-1.jsonl"), """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t-early","content":"ok"}]}}""" + "\n");
                c.Poke();
                var labels = c.TalkLabels();
                c.Expect(labels.Contains("Updated with 1 addition and 1 removal") && labels.Contains("early();") && labels.Contains("later();"), "the early Edit's result with its diff; last labels: " + string.Join(" | ", labels.TakeLast(5).Select(l => l.Length > 40 ? l[..40] + "…" : l)));
            });

        // ---- work pane, stage wp4: the Queen's cell, the first-run hint, motion off ----

        Test("queen's cell: lit and heard as \"Queen, talking with you\" while her turn runs, off when it ends", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"go"}}""");
            var turn = RunningQueen(c, "sess-1");
            c.Start();
            var label = (nint)c.Get<List<long>>("cellLabels")[0];
            c.Expect(c.Get<bool>("queenLit") && G.Says(label, "Queen, talking with you"), "her cell lit and heard as \"Queen, talking with you\"; it says " + G.Tooltip(label));
            Tasks.Record(c.Outside, Tasks.Load(c.Outside, turn.Id), "Done.", "done");
            c.Expect(c.PumpUntil(() => !c.Get<bool>("queenLit")), "not lit once her turn ended");
            c.Expect(!G.Says(label, "Queen, talking with you"), "no longer heard as talking; it says " + G.Tooltip(label));
        });

        Test("queen's cell: esc puts her cell out", c =>
        {
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"go"}}""");
            RunningQueen(c, "sess-1");
            c.Start();
            c.Pre(c.Get<bool>("queenLit"), "lit while she answers");
            G.PressKey(c.Get<nint>("talkEntry"), 0xff1b);
            c.Expect(c.PumpUntil(() => !c.Get<bool>("queenLit")), "not lit after esc");
            c.Expect(!G.Says((nint)c.Get<List<long>>("cellLabels")[0], "Queen, talking with you"), "not heard as talking");
        });

        Test("first run: the No bees yet hint heads the Queen's tab, and goes with the first bee", c =>
        {
            File.Delete(c.Path("bees", "Worker.md"));
            c.Start();
            var first = c.TalkLabels().FirstOrDefault(l => l.Length > 0) ?? "";
            c.Expect(first.StartsWith("No bees yet") && first.Contains("+ new"), "the hint first in the pane; it says " + first);
            c.Outside.CreateBee("Helper", "Helps", [], "", "approve");
            c.Expect(c.PumpUntil(() => !c.TalkLabels().Any(l => l.StartsWith("No bees yet"))), "gone with a bee; labels: " + string.Join(" | ", c.TalkLabels()));
        });

        Test("motion off: with a bee at work in the pane, nothing moves, and everything shows", c =>
        {
            G.g_object_set(G.gtk_settings_get_default(), "gtk-enable-animations", 0, 0);
            QueenSaid(c, "sess-1", "/p", """{"type":"user","message":{"role":"user","content":"go"}}""");
            RunningQueen(c, "sess-1");
            c.Start();
            Subagent("sess-1", "a1", "worker", "Look into A.cs");
            c.Poke();
            c.Click(G.Button(c.Get<nint>("talkPanel"), "+ new")); // refused while she answers: the panel doesn't draw in either
            c.Pump(1500);
            c.Expect(!Ui.Ticking && !c.Get<bool>("talkMoving"), $"no tick running (Ui.Ticking {Ui.Ticking}, talkMoving {c.Get<bool>("talkMoving")})");
            var chip = c.BeeChips().Single();
            var name = G.OfType(chip, "GtkLabel").Select(G.Text).ToList();
            c.Expect(name.Contains("Worker") && c.ChipGlyph(chip) == "⠿", "the tab's name in full and a still ⠿; it shows " + string.Join(" ", name));
            c.Expect(G.Text(c.Get<nint>("talkChip")) == "♛ ⠿", "♛ ⠿ on her tab; it shows " + G.Text(c.Get<nint>("talkChip")));
            c.Expect(G.OfType(c.Get<nint>("talkLines"), "GtkBox").All(b => G.gtk_widget_get_opacity(b) == 1), "every line fully shown");
        });

        // ---- rebuilt to the mockups, stage 8: Claude usage ----

        Test("usage: with no usage data the footer says so", c =>
        {
            c.Start();
            c.Expect(G.Text(c.Get<nint>("usageLabel")) == "no usage data yet", "\"no usage data yet\"; it says " + G.Text(c.Get<nint>("usageLabel")));
        });

        Test("usage: what the status line saves shows in the footer, without refreshing the hive", c =>
        {
            Directory.CreateDirectory(c.Path(".state")); // as after the first session (a new .state is a task change of its own)
            c.Start();
            var hits = c.Get<List<Hit>>("activityHits");
            var at = DateTimeOffset.Now.ToUnixTimeSeconds() + 3600;
            Usage.StatusLine(c.Outside, "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":42,\"resets_at\":" + (at) + "},\"seven_day\":{\"used_percentage\":23,\"resets_at\":" + (at + 86400 * 3) + "}}}");
            var label = c.Get<nint>("usageLabel");
            c.Expect(c.PumpUntil(() => G.Text(label).StartsWith("session ▰▰▰▰▰▰▰▰▰▰ 42% ↻") && G.Text(label).Contains("week ▰▰▰▰▰▰▰▰▰▰ 23% ↻")), "session and week in the footer; it says " + G.Text(label));
            c.Expect(ReferenceEquals(hits, c.Get<List<Hit>>("activityHits")), "the hive not refreshed for it");
        });

        Test("usage: at 80% the status line warns, once per window", c =>
        {
            c.Start();
            var at = DateTimeOffset.Now.ToUnixTimeSeconds() + 3600;
            Usage.StatusLine(c.Outside, "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":85,\"resets_at\":" + (at) + "}}}");
            var status = c.Get<StatusLine>("status");
            c.Expect(c.PumpUntil(() => G.Text(status.Message).Contains("the session is at 85% of its limit"), 4000), "a warning; it says " + G.Text(status.Message));
            c.Call("Say", "Something else.");
            Usage.StatusLine(c.Outside, "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":86,\"resets_at\":" + (at) + "}}}");
            c.Pump(1500);
            c.Expect(!G.Text(status.Message).Contains("of its limit"), "no second warning for the same window; it says " + G.Text(status.Message));
        });

        // ---- stage 5: the window's own palette ----

        Test("palette: the accent is smoky honey even under a user gtk.css with another accent", c =>
        {
            c.Start();
            var label = N.gtk_label_new("accent");
            N.gtk_widget_add_css_class(label, "accent");
            N.gtk_box_append(G.gtk_widget_get_parent(c.Get<nint>("hiveStats")), label);
            var color = "";
            c.Expect(c.PumpUntil(() => (color = G.Color(label)) == "#B8935A"), $"the accent label's colour #B8935A, got {color}");
        });

        Test("palette: dim text is the palette's gray even where the user gtk.css has a more specific rule", c =>
        {
            c.Start();
            var color = "";
            c.Expect(c.PumpUntil(() => (color = G.Color(c.Get<nint>("hiveStats"))) == "#8E8C88"), $"the dim label's colour #8E8C88, got {color}");
        });
    }

    // ---- stage 3: undo toasts instead of confirm dialogs ----

    static void UndoTests()
    {
        Test("undo: Retire bee acts at once, and the toast's Undo brings the bee back", c =>
        {
            c.Start();
            c.Call("ShowBee", "Worker");
            c.Call("Go", "bees");
            c.Pump(300);
            c.Click(G.Button(c.Get<nint>("beeDetail"), "edit")); // rebuilt to the mockups: Retire is with the card's editor
            c.Pre(c.PumpUntil(() => N.gtk_widget_get_mapped(G.Button(c.Get<nint>("beeDetail"), "Retire bee")), 4000), "the card's editor open");
            c.Click(G.Button(c.Get<nint>("beeDetail"), "Retire bee"));
            c.Expect(c.Store.FindBee("Worker") == null, "Worker retired at once, with no dialog asking first");
            nint undo = 0;
            c.Pre(c.PumpUntil(() => (undo = G.Button(c.Get<nint>("window"), "Undo")) != 0), "a toast with an Undo button");
            c.Click(undo);
            c.Expect(c.PumpUntil(() => c.Get<List<Bee>>("bees").Any(b => b.Name == "Worker")), "Worker back in the window's bees after Undo; errors: " + string.Join(" | ", c.Errors));
        });

        Test("undo: approving a molt acts at once, and Undo puts the molt and the old card back", c =>
        {
            c.Store.Molt("Worker", "a sharper card", "# Worker\n\nA new card.\n", "queen");
            c.Start();
            c.Call("ShowBee", "Worker");
            c.Call("Go", "bees");
            c.Pump(300);
            c.Click(G.Button(c.Get<nint>("beeDetail"), "Approve"));
            c.Expect(c.Store.PendingMolts().Count == 0 && c.Store.RequireBee("Worker").Doc.Body.Contains("A new card."), "the molt approved at once");
            nint undo = 0;
            c.Pre(c.PumpUntil(() => (undo = G.Button(c.Get<nint>("window"), "Undo")) != 0), "a toast with an Undo button");
            c.Click(undo);
            c.Expect(c.Store.PendingMolts().Count == 1 && !c.Store.RequireBee("Worker").Doc.Body.Contains("A new card."),
                     "the molt waiting again and the card as it was; errors: " + string.Join(" | ", c.Errors));
        });

        // ---- work pane, stage wp1: the pane column, the ▤ inbox popover, the narrow sheet ----
        // Broadway's
        // screen is 1024x768: a bigger window is forced with a size request.

        Test("inbox ▤: the header's ▤ opens the inbox in a popover, with its items", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            Tasks.Record(c.Store, task, "Found it.", "done");
            c.Start();
            var button = c.Get<nint>("inboxButton");
            c.Expect(G.Ancestor(button, "AdwHeaderBar") != 0, "▤ in the window's header");
            c.Expect(G.gtk_widget_get_visible(c.Get<nint>("inboxPop")) == 0 && !N.gtk_widget_get_mapped(c.Get<nint>("inboxPanel")), "the inbox not on screen before ▤ is clicked");
            c.OpenInbox();
            var (pop, panel) = (c.Get<nint>("inboxPop"), c.Get<nint>("inboxPanel"));
            c.Expect(G.Inside(panel, pop), "the inbox panel inside ▤'s popover");
            var outer = c.InboxOuter("task:" + task.Id);
            c.Expect(outer != 0 && N.gtk_widget_get_mapped(outer) && G.Inside(outer, pop), "the finished task's item on screen in the popover");
            c.Expect(G.OfType(pop, "GtkLabel").Select(G.Text).Contains("Look into it") && G.Button(pop, "Give honey") != 0, "its title and Give honey in the popover");
        });

        Test("inbox ▤: Give honey in the popover rewards the task, and the ▤ count goes to 0", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            Tasks.Record(c.Store, task, "Found it.", "done");
            c.Start();
            c.Pre(G.Text(c.Get<nint>("inboxBadge")) == "1", "▤ 1 to start; it says " + G.Text(c.Get<nint>("inboxBadge")));
            c.OpenInbox();
            c.Click(G.Button(c.Get<nint>("inboxPop"), "Give honey"));
            c.Expect(Tasks.Load(c.Store, task.Id).Rewarded, "the task rewarded; errors: " + string.Join(" | ", c.Errors));
            c.Expect(c.PumpUntil(() => G.Text(c.Get<nint>("inboxBadge")) == "0"), "▤ 0 after; it says " + G.Text(c.Get<nint>("inboxBadge")));
        });

        Test("inbox ▤: ✕ in the popover dismisses the item without touching the task", c =>
        {
            var task = Tasks.Create(c.Store, "Worker", "Look into it", "", "read");
            Tasks.Record(c.Store, task, "Found it.", "done");
            c.Start();
            c.OpenInbox();
            var dismiss = G.OfType(c.Get<nint>("inboxPop"), "GtkButton").FirstOrDefault(b => G.Says(b, "Dismiss"));
            c.Pre(dismiss != 0, "a ✕ heard as Dismiss in the popover");
            c.Click(dismiss);
            c.Expect(c.PumpUntil(() => c.Inbox() == 0 && G.Text(c.Get<nint>("inboxBadge")) == "0"), $"the inbox empty and ▤ 0; {c.Inbox()} items, ▤ {G.Text(c.Get<nint>("inboxBadge"))}");
            var doc = Tasks.Load(c.Store, task.Id).Doc;
            c.Expect(doc.Get("reward") == "" && doc.Get("status") == "done", "the task as it was");
        });

        Test("inbox ▤: a card change's item in the popover opens its bee's page, where Approve is", c =>
        {
            c.Store.Molt("Worker", "a sharper card", "# Worker\n\nA new card.\n", "queen");
            c.Start();
            c.OpenInbox();
            var pop = c.Get<nint>("inboxPop");
            c.Pre(c.PumpUntil(() => G.OfType(pop, "GtkLabel").Select(G.Text).Contains("A change for Worker's card")), "the card change (by the Queen) in the popover; labels: " + string.Join(" | ", G.OfType(pop, "GtkLabel").Select(G.Text)));
            c.Click(G.Button(pop, "read"));
            c.Expect(c.PumpUntil(() => c.Page() == "bees" && G.Button(c.Get<nint>("beeDetail"), "Approve") != 0), "Worker's page with Approve; the page is " + c.Page());
            c.Click(G.Button(c.Get<nint>("beeDetail"), "Approve"));
            c.Expect(c.Store.PendingMolts().Count == 0 && c.PumpUntil(() => G.Text(c.Get<nint>("inboxBadge")) == "0"), "the molt approved and ▤ 0");
        });

        Test("inbox ▤: the count is honey while something waits and dimmed at 0, and says so", c =>
        {
            c.Start();
            var (button, badge) = (c.Get<nint>("inboxButton"), c.Get<nint>("inboxBadge"));
            c.Expect(G.Text(badge) == "0" && G.Color(badge) == $"#{Palette.Faint:X6}", $"▤ 0 in faint (#{Palette.Faint:X6}); it's {G.Text(badge)} in {G.Color(badge)}");
            c.Expect(G.Says(button, "Inbox, nothing waits"), "heard as \"Inbox, nothing waits\"");
            var task = Tasks.Create(c.Outside, "Worker", "Look into it", "", "read");
            Tasks.Record(c.Outside, task, "Found it.", "done");
            c.Expect(c.PumpUntil(() => G.Text(badge) == "1"), "▤ 1 once a task finished; it says " + G.Text(badge));
            c.Expect(c.PumpUntil(() => G.Color(badge) == $"#{Palette.Pulse:X6}", 3000), $"▤ 1 in honey (#{Palette.Pulse:X6}); it's {G.Color(badge)} (button {(G.gtk_widget_has_css_class(button, "empty") != 0 ? "still" : "not")} .empty)");
            c.Expect(G.Says(button, "Inbox, 1 waiting"), "heard as \"Inbox, 1 waiting\"");
        });

        foreach (var (w, h) in new[] { (1320, 900), (1700, 1000) })
            Test($"pane: the work pane is 38% of the Hive page ({w}x{h})", c =>
            {
                c.Start();
                c.Size(w, h);
                var (pane, split) = (c.Get<nint>("pane"), c.Get<nint>("hiveSplit"));
                var sw = G.gtk_widget_get_width(split);
                c.Pre(sw == w - 40, $"the page's split the window less its 20 px margins ({w - 40}); it's {sw}");
                var want = (int)(0.38 * sw);
                var got = 0;
                c.Expect(c.PumpUntil(() => Math.Abs((got = G.gtk_widget_get_width(pane)) - want) <= 1, 3000), $"the pane 38% of {sw} = {want} px; it's {got} ({100.0 * got / sw:0.0}%)");
                var column = c.Get<nint>("hiveColumn");
                var colRight = G.BoxIn(column, split).X + G.gtk_widget_get_width(column);
                c.Expect(Math.Abs(G.BoxIn(pane, split).X - (colRight + 20)) <= 1 && Math.Abs(G.BoxIn(pane, split).X + got - sw) <= 1, $"the pane at the page's right, 20 px after the column; column ends {colRight}, pane at {G.BoxIn(pane, split).X}");
            });

        foreach (var (w, h) in new[] { (905, 700), (1024, 768) })
            Test($"pane: the work pane is never under 380 px ({w}x{h})", c =>
            {
                c.Start();
                c.Size(w, h);
                c.Pre(!c.Get<bool>("narrow"), $"the wide layout at {w} px");
                var (pane, split) = (c.Get<nint>("pane"), c.Get<nint>("hiveSplit"));
                c.Pre(0.38 * G.gtk_widget_get_width(split) < 380, $"38% of the split ({G.gtk_widget_get_width(split)}) under 380");
                var got = 0;
                c.Expect(c.PumpUntil(() => (got = G.gtk_widget_get_width(pane)) == 380, 3000), $"the pane 380 px; it's {got}");
                c.Expect(N.gtk_widget_get_mapped(pane), "the pane on screen");
            });

        Test("pane: the Hive page has the ┤ ♛ queen ├ pane the page's full height, and no at work panel", c =>
        {
            c.Start();
            var page = c.Get<nint>("hiveSheet");
            var titles = G.OfType(page, "GtkFrame").Where(f => N.gtk_widget_get_mapped(f)).Select(f => N.gtk_frame_get_label_widget(f)).Where(l => l != 0).Select(G.Text).ToList();
            c.Expect(!titles.Any(t => t.Contains("at work")), "no ┤ at work ├ panel; the page's panels: " + string.Join(", ", titles));
            c.Expect(!titles.Any(t => t.Contains("inbox")), "no ┤ inbox ├ panel on the page (it's in ▤)");
            c.Expect(titles.Contains("┤ ♛ queen ├"), "a ┤ ♛ queen ├ panel; the page's panels: " + string.Join(", ", titles));
            var (pane, column, split) = (c.Get<nint>("pane"), c.Get<nint>("hiveColumn"), c.Get<nint>("hiveSplit"));
            var (pTop, cTop) = (G.BoxIn(pane, split).Y, G.BoxIn(column, split).Y);
            var (pH, cH) = (G.gtk_widget_get_height(pane), G.gtk_widget_get_height(column));
            c.Expect(Math.Abs(pTop - cTop) <= 1 && pH >= cH - 1, $"the pane from the column's top down at least as far (pane {pTop}+{pH}, column {cTop}+{cH})");
            c.Expect(G.Says(N.gtk_overlay_get_child(pane), "queen"), "the pane heard as \"queen\"");
        });

        // ---- rv-a #14 / stage 2 (0.1.03): a bee's popover opens out of place ----
        // Brought over with the Smith's patch in wp4; "no terminal" parts dropped (no per-bee terminal now).
        // The user: clicking a bee opens its popover "completely out of place".
        // Decided: clicking a bee's cell opens its popover (not its terminal);
        // the ❯ talk button opens the terminal. Each check clicks the scene as
        // the mouse does (its click gesture, at a cell's centre from the
        // scene's own slots, times its scale) and measures where the popover's
        // surface really lands (GdkPopup) against the clicked cell, both in
        // the window's surface. Broadway's screen is 1024x768, so a bigger
        // window is forced with a size request.

        foreach (var (w, h) in new[] { (1024, 768), (1700, 1000) })
            Test($"popover: rv-a #14 clicking a bee's cell opens its popover next to that cell ({w}x{h})", c =>
            {
                c.Store.CreateBee("Wren", "Tests the work", [], "", "approve");
                c.Start();
                c.Size(w, h);
                foreach (var bee in new[] { "Worker", "Wren" })
                {
                    var (x, y) = c.Slot(c.CellOf(bee));
                    var r = c.Get<double>("hexR");
                    c.ClickScene(x, y);
                    c.Expect(c.PumpUntil(c.Popped), $"{bee}'s popover shown");
                    c.ExpectNextTo(c.PopoverBox(), c.SceneBox(x - r * 0.87, y - r * 0.9, x + r * 0.87, y + r * 0.9), $"{bee}'s popover next to its cell");
                    N.gtk_popover_popdown(c.Get<nint>("popover"));
                    c.Pump(300);
                }
            });

        foreach (var (w, h) in new[] { (1024, 768), (1700, 1000) })
            Test($"popover: rv-a #14 control: clicking a working bee's sprite at its station opens its popover there, with Stop ({w}x{h})", c =>
            {
                Running(c, "Worker");
                c.Start(pumpMs: 2500);
                c.Size(w, h);
                c.Pre(c.PumpUntil(() => { c.Call("DrawAll", Ui.Clock); return c.AwayFromHome("Worker") && !c.SpriteFlying("Worker") && c.SpriteState("Worker") != "rest"; }, 8000), "Worker out at its station; it's " + c.SpriteState("Worker"));
                var sprite = c.SpriteObject("Worker");
                var (x, y) = ((double)c.Field(sprite, "X")!, (double)c.Field(sprite, "Y")!);
                c.ClickScene(x, y - 5); // its body, just above its baseline
                var popover = c.Get<nint>("popover");
                c.Expect(c.PumpUntil(c.Popped), "Worker's popover shown");
                c.Expect(G.Button(popover, "Stop") != 0, "a Stop button for its task");
                c.ExpectNextTo(c.PopoverBox(), c.SceneBox(x - 16, y - 16, x + 16, y + 6), "the popover next to Worker at its station");
            });

        Test("popover: rv-a #14 control: clicking the Queen's cell goes to the Briefs page", c =>
        {
            c.Start();
            c.Pre(c.Page() == "hive", "the Hive page to start; it shows " + c.Page());
            var (x, y) = c.Slot(0);
            c.ClickScene(x, y);
            c.Expect(c.PumpUntil(() => c.Page() == "briefs"), "the Briefs page; it shows " + c.Page());
            c.Expect(!c.Popped(), "no popover");
        });

        Test("popover: rv-a #14 control: clicking \"+ new\" starts a new bee", c =>
        {
            c.Start();
            var window = c.Get<nint>("window");
            c.Pre(G.Button(window, "Create") == 0, "no Create button before the click");
            var (x, y) = c.Slot(c.Get<System.Collections.IList>("bees").Count + 1); // after the Queen and the bees
            c.ClickScene(x, y);
            c.Expect(c.PumpUntil(() => G.Button(window, "Create") != 0, 1500), "the new-bee dialog, with Create");
            c.Expect(!c.Popped(), "no popover");
        });

        // ---- stage 2: the popover is as wide as its bee's last result ----
        // A reviewer's cell clicked in a 1687 px wide window:
        // the popover is 800+ px wide, not the 440 its box asks for, because
        // the last task's result is one long line (up to 160 characters),
        // centred under the bee it runs past the window's left edge (the
        // panel's title cut off), and the line shows raw Markdown (**Which
        // commit:**). A long result here is like a real review report.

        foreach (var (name, result) in new[] { ("control: a short last result", "Found it."), ("a long last result", LongResult) })
            Test($"popover: stage 2 {name}: the popover shows, about 440 px wide", c =>
            {
                Done(c, "Worker", result);
                c.Start();
                var (x, y) = c.Slot(c.CellOf("Worker"));
                c.ClickScene(x, y);
                c.ExpectPopover("Worker");
                var pop = c.PopoverBox();
                c.Expect(pop.R - pop.L <= 520, $"at most 520 px wide (its box asks for 440, plus its frame); it's {pop.R - pop.L:0} px");
            });

        Test("popover: stage 2 the last result shows as text, without raw Markdown", c =>
        {
            Done(c, "Worker", LongResult);
            c.Start();
            var (x, y) = c.Slot(c.CellOf("Worker"));
            c.ClickScene(x, y);
            var labels = G.OfType(c.Get<nint>("popover"), "GtkLabel").Select(G.Text).ToList();
            var line = labels.FirstOrDefault(t => t.StartsWith("Last task")) ?? "";
            c.Pre(line.Contains("Which commit"), "the last result's line in the popover, with \"Which commit\"; labels: " + string.Join(" | ", labels));
            c.Expect(!line.Contains("**") && !line.Contains("## "), "no ** or ## in it; it says " + line);
        });

        // The honeycomb sits at the window's left edge, so a popover centred
        // under a bee in it runs out of the window on the left. A working
        // bee's sprite at a station, mid-scene, has room on both sides (the
        // control). Broadway's screen is 1024x768, so popups are placed (and
        // slid) within that, and the window's 25 px shadow is outside it.
        foreach (var (w, h) in new[] { (1024, 768), (960, 720) })
            foreach (var (who, result) in new[] { ("control: a working bee's popover at its station", "Found it."), ("the leftmost bee's popover, short results", "Found it."), ("the leftmost bee's popover, long results", LongResult) })
                Test($"popover: stage 2 {who}: inside the window ({w}x{h})", c =>
                {
                    foreach (var b in new[] { "Ash", "Mason", "Smith", "Arbiter", "Gauge", "Birch", "Cedar" })
                        c.Store.CreateBee(b, "Works", [], "", "approve");
                    foreach (var b in c.Store.Bees()) Done(c, b.Name, result);
                    var atStation = who.StartsWith("control");
                    if (atStation) Running(c, "Worker");
                    c.Start(pumpMs: atStation ? 2500 : 800);
                    c.Size(w, h);
                    var bees = c.Get<System.Collections.IList>("bees").Cast<object>().Select(b => (string)c.Field(b, "Name")!).ToList();
                    var bee = atStation ? "Worker" : bees.MinBy(b => c.Slot(c.CellOf(b)).X)!;
                    var (x, y) = c.Slot(c.CellOf(bee));
                    if (atStation)
                    {
                        c.Pre(c.PumpUntil(() => { c.Call("DrawAll", Ui.Clock); return c.AwayFromHome("Worker") && !c.SpriteFlying("Worker") && c.SpriteState("Worker") != "rest"; }, 8000), "Worker out at its station; it's " + c.SpriteState("Worker"));
                        var sprite = c.SpriteObject("Worker");
                        (x, y) = ((double)c.Field(sprite, "X")!, (double)c.Field(sprite, "Y")! - 5);
                    }
                    var window = c.Get<nint>("window");
                    double wx, wy;
                    unsafe { G.gtk_native_get_surface_transform(window, &wx, &wy); }
                    var (ww, wh) = (G.gtk_widget_get_width(window), G.gtk_widget_get_height(window));
                    c.ClickScene(x, y);
                    c.ExpectPopover(bee);
                    var pop = c.PopoverBox();
                    c.Expect(pop.L >= wx - 1 && pop.R <= wx + ww + 1 && pop.T >= wy - 1 && pop.B <= wy + wh + 1,
                             $"{bee}'s popover ({pop.L:0},{pop.T:0})-({pop.R:0},{pop.B:0}) inside the window ({wx:0},{wy:0})-({wx + ww:0},{wy + wh:0})");
                });

        // ---- stage 2: on a page switch the panels' lines stretch and resize with the typed title ----
        // The user: "whenever changing the page the lines stretch upwards and
        // resize based on text". On a page switch every panel of the page
        // draws its frame in while its title types (Fx.DrawIn). Broadway's
        // frame clock ticks about once a second, so the typing can't be
        // watched frame by frame: each step is set with Fx.ShowTitle (private,
        // by reflection; the animation's own call) for all panels at once,
        // and the window is laid out at once. What must not depend on how
        // much of the title has typed: each panel's box, its content's box,
        // and the title's height (the top border runs through its middle).

        foreach (var page in new[] { "hive", "bees", "memory", "reviews", "briefs" })
            Test($"page switch: the {page} page's panels keep their size and place while their titles type", c =>
            {
                c.Start();
                c.Call("ShowBee", "Worker");
                c.Call("Go", page);
                c.Pump(3000);
                var window = c.Get<nint>("window");
                var fx = typeof(Fx);
                var layers = (System.Collections.IDictionary)fx.GetField("layers", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                var show = fx.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).SingleOrDefault(m => m.Name == "ShowTitle" && m.GetParameters().Length == 2);
                c.Pre(show != null, "Fx.ShowTitle(state, n)");
                var panels = layers.Values.Cast<object>().Where(st => N.gtk_widget_get_mapped((nint)c.Field(st, "Panel")!) && ((string)c.Field(st, "Title")!).Length > 0).ToList();
                c.Pre(panels.Count > 0, $"panels on the {page} page");
                string Shape(object st)
                {
                    var frame = (nint)c.Field(st, "Frame")!;
                    var label = N.gtk_frame_get_label_widget(frame);
                    return $"panel {G.BoxWH((nint)c.Field(st, "Panel")!, window)}, content {G.BoxWH(N.gtk_frame_get_child(frame), window)}, title {G.BoxWH(label, window).Y}+{G.gtk_widget_get_height(label)} high";
                }
                // Settled first: a text view's height fills in after the page
                // first shows (the Briefs editor grows by 90 px), whatever the title does.
                // Every reading re-measures the panels' frames, as a title change does,
                // so a layout GTK hasn't redone yet doesn't count as settled.
                void Fresh() { foreach (var st in panels) G.gtk_widget_queue_resize(N.gtk_frame_get_child((nint)c.Field(st, "Frame")!)); G.LayOut(window); }
                string All() { Fresh(); return string.Join("\n", panels.Select(Shape)); }
                var last = All();
                c.Pre(c.PumpUntil(() => { c.Pump(1200); var now = All(); var same = now == last; last = now; return same; }, 8000), $"the {page} page settled");
                var settled = panels.ToDictionary(st => st, Shape);
                foreach (var p in new[] { 0.0, 0.25, 0.5, 0.75 })
                {
                    foreach (var st in panels) show!.Invoke(null, [st, (int)(p * ((string)c.Field(st, "Title")!).Length)]);
                    Fresh();
                    foreach (var st in panels)
                        c.Expect(Shape(st) == settled[st], $"\"{c.Field(st, "Title")}\" as it is when typed ({settled[st]}) with {p:P0} typed; it's {Shape(st)}");
                }
                foreach (var st in panels) show!.Invoke(null, [st, ((string)c.Field(st, "Title")!).Length]);
            });

        // ---- team review review rv-a (the whole repo, v0.1.01) ----
        // "rv-a #n" is the finding number in that review; its other bugs
        // are tested in tests/StoreTests.

        // ---- rv-a #8 open file, open in editor and finding links split paths at spaces ----
        // A fake xdg-open on PATH writes down the arguments it was given.

        Test("rv-a #8 control: open file hands xdg-open the entry's path", c =>
        {
            var said = FakeXdgOpen();
            c.WriteEntry("global", "plain-entry", "A plain entry");
            c.Start();
            c.Call("OpenEntry", "plain-entry");
            var open = G.Button(c.Get<nint>("entryDetail"), "open file");
            c.Pre(open != 0, "an open file button");
            G.g_signal_emit_by_name(open, "clicked", 0);
            c.Pre(c.PumpUntil(() => File.Exists(said)), "xdg-open to run; errors: " + string.Join(" | ", c.Errors));
            var got = ReadArgs(said);
            c.Expect(got.SequenceEqual([c.Path("comb", "global", "plain-entry.md")]), "one argument, the entry's path; got " + string.Join(" | ", got));
        });

        Test("rv-a #8 open file hands xdg-open a path with a space as one argument", c =>
        {
            var said = FakeXdgOpen();
            c.WriteEntry("two words", "spaced-entry", "An entry in a folder with a space");
            c.Start();
            c.Call("OpenEntry", "spaced-entry");
            var open = G.Button(c.Get<nint>("entryDetail"), "open file");
            c.Pre(open != 0, "an open file button");
            G.g_signal_emit_by_name(open, "clicked", 0);
            c.Pre(c.PumpUntil(() => File.Exists(said)), "xdg-open to run; errors: " + string.Join(" | ", c.Errors));
            var got = ReadArgs(said);
            c.Expect(got.SequenceEqual([c.Path("comb", "two words", "spaced-entry.md")]), "one argument, the entry's path; got " + string.Join(" | ", got));
        });

        Test("rv-a #8 a finding's file link opens a file in a folder with a space as one argument", c =>
        {
            var said = FakeXdgOpen();
            var folder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(c.Root)!, "my project");
            Directory.CreateDirectory(System.IO.Path.Combine(folder, "src"));
            File.WriteAllText(System.IO.Path.Combine(folder, "src", "A.cs"), "class A { }\n");
            c.Store.SetProject("spaced", "A project in a folder with a space", [folder], null);
            Reviews.SetUp(c.Store, new[] { "Cedar", "Birch", "Ash", "Arbiter" });
            const string run = "review-20261018-120000";
            var reviewer = Tasks.Create(c.Store, "Cedar", "Review src/", "spaced", "read", run: run, role: "reviewer");
            c.Pre(Text.Expand(reviewer.Doc.Get("folder")) == folder, "the review to run in the project's folder; it runs in " + reviewer.Doc.Get("folder"));
            Tasks.Record(c.Store, reviewer, "Cedar's report.", "done");
            var overseer = Tasks.Create(c.Store, "Arbiter", "Oversee", "spaced", "read", run: run, role: "overseer", waitsFor: [reviewer.Id]);
            Tasks.Record(c.Store, overseer, "# Review: src/ (spaced)\n\n## Confirmed\n\n1. **[bug high]** `src/A.cs:12`: it crashes (found by Cedar).\n", "done");
            c.Start();
            c.Call("Go", "reviews");
            c.Call("ShowRun", run);
            var link = G.Button(c.Get<nint>("runDetail"), "src/A.cs:12");
            c.Pre(link != 0, "a link to src/A.cs:12");
            G.g_signal_emit_by_name(link, "clicked", 0);
            c.Pre(c.PumpUntil(() => File.Exists(said)), "xdg-open to run; errors: " + string.Join(" | ", c.Errors));
            var got = ReadArgs(said);
            c.Expect(got.SequenceEqual([System.IO.Path.Combine(folder, "src", "A.cs")]), "one argument, the file's path; got " + string.Join(" | ", got));
        });

        // ---- rv-a #9 some Markdown becomes markup GTK refuses, and the paragraph shows blank ----
        // What the user sees: the paragraph's words in a label of the entry.

        foreach (var (name, body) in new[]
        {
            ("rv-a #9 control: a paragraph with bold shows", "Before **bold** after."),
            ("rv-a #9 a paragraph where ** and * overlap still shows", "Before **a*b** c* after."),
            ("rv-a #9 a paragraph with a \" in a link's URL still shows", "Before [the docs](https://example.org/a\"b) after."),
        })
            Test(name, c =>
            {
                File.WriteAllText(c.Path("comb", "global", "marked.md"), $"---\ntitle: Marked\ntype: note\nbee: queen\nproject: global\ncreated: {Text.Now}\nsummary: A summary.\n---\n\n{body}\n");
                c.Start();
                c.Call("OpenEntry", "marked");
                var detail = c.Get<nint>("entryDetail");
                c.Pre(G.OfType(detail, "GtkLabel").Any(l => G.Text(l) == "A summary." || G.Text(l).Contains("Marked")), "the entry open");
                var labels = G.OfType(detail, "GtkLabel").Select(G.Text).ToList();
                c.Expect(labels.Any(l => l.Contains("Before") && l.Contains("after")), $"a label with the paragraph's words for {body}; labels: " + string.Join(" | ", labels.Where(l => l.Length > 0)));
            });

        // ---- rv-a #15 signal handlers are never released ----
        // Counts the handlers Ui keeps (every closure in its static
        // collections, by reflection, whatever their names) before and after
        // the window rebuilds the same thing over and over.

        Test("rv-a #15 control: refreshing the bees with nothing new keeps no new handlers", c =>
        {
            c.Start();
            c.Call("Go", "bees");
            c.Call("RefreshBees");
            c.Pump(200);
            var before = Handlers();
            for (int i = 0; i < 20; i++) c.Call("RefreshBees");
            c.Pump(300);
            c.Expect(Handlers() - before <= 2, $"no more handlers kept after 20 refreshes; {Handlers() - before} more");
        });

        Test("rv-a #15 refreshing Reviews with no team doesn't keep a handler per refresh", c =>
        {
            c.Start();
            c.Call("Go", "reviews");
            c.Call("RefreshReviews");
            c.Pump(200);
            var before = Handlers();
            for (int i = 0; i < 20; i++) c.Call("RefreshReviews");
            c.Pump(300);
            c.Expect(Handlers() - before <= 2, $"no more handlers kept after 20 refreshes; {Handlers() - before} more");
        });

        Test("rv-a #15 opening one entry after another doesn't keep the old entries' handlers", c =>
        {
            c.WriteEntry("global", "entry-a", "Entry A");
            c.WriteEntry("global", "entry-b", "Entry B");
            c.Start();
            c.Call("OpenEntry", "entry-a");
            c.Call("OpenEntry", "entry-b");
            c.Pump(200);
            var before = Handlers();
            for (int i = 0; i < 10; i++) { c.Call("OpenEntry", "entry-a"); c.Call("OpenEntry", "entry-b"); }
            c.Pump(300);
            c.Expect(Handlers() - before <= 5, $"no more handlers kept after opening 20 entries; {Handlers() - before} more");
        });
    }

    // A fake xdg-open first on PATH; returns the file it writes its arguments to, one per line.
    static string FakeXdgOpen()
    {
        var bin = System.IO.Path.Combine(Environment.GetEnvironmentVariable("HIVE_TESTS_DIR")!, $"check-{Environment.ProcessId}", "bin");
        Directory.CreateDirectory(bin);
        var script = System.IO.Path.Combine(bin, "xdg-open");
        File.WriteAllText(script, "#!/bin/sh\nfor a in \"$@\"; do printf '%s\\n' \"$a\"; done > \"$0.args.tmp\" && mv \"$0.args.tmp\" \"$0.args\"\n");
        File.SetUnixFileMode(script, (UnixFileMode)0b111_101_101);
        Environment.SetEnvironmentVariable("PATH", bin + ":" + Environment.GetEnvironmentVariable("PATH"));
        return script + ".args";
    }

    static List<string> ReadArgs(string file) => File.ReadAllLines(file).ToList();

    static int Handlers() =>
        typeof(Ui).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.FieldType != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(f.FieldType))
            .Sum(f => f.GetValue(null) is System.Collections.IEnumerable e ? e.Cast<object?>().Count(IsHandler) : 0);

    // A delegate, or a dictionary entry holding one.
    static bool IsHandler(object? x) =>
        x is Delegate || x?.GetType().GetProperty("Value")?.GetValue(x) is Delegate;

    // A stand-in for a task's runner, as its pid: a process whose command line
    // holds `run-task <id>` as `hive run-task <id>`'s does, since the window
    // takes any other process for a stranger that got a dead runner's pid. It
    // ends within a second of this check's process.
    static string Runner(string id)
    {
        var info = new ProcessStartInfo("sh", ["-c", "while kill -0 $PPID 2>/dev/null; do sleep 1; done", "run-task", id])
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        return Process.Start(info)!.Id.ToString();
    }

    static int DeadPid()
    {
        for (var pid = 999_999; ; pid++)
            if (!Directory.Exists($"/proc/{pid}")) return pid;
    }

    // The pane's fresh turn gets its session, with these records in its transcript, and the window refreshes.
    static void SessionCame(Ctx c, string session, params string[] records)
    {
        var turn = Tasks.List(c.Store, Tasks.Queen).Last(t => t.Doc.Get("resume") == "");
        turn.Doc.Set("session", session);
        turn.Doc.Save(turn.Path);
        var dir = Path.Combine(Environment.GetEnvironmentVariable("HOME")!, ".claude", "projects", "-p");
        if (Directory.Exists(Path.Combine(dir, session))) Directory.Delete(Path.Combine(dir, session), true);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, session + ".jsonl"), string.Join("\n", records) + "\n");
        c.Call("RefreshHive");
        c.Pump(500);
    }

    // Bytes this process has read so far (any file).
    static long Rchar() => long.Parse(File.ReadAllLines("/proc/self/io").First(l => l.StartsWith("rchar")).Split(' ')[1]);

    // The pane Queen's subagents folder for a session (folder /p).
    static string SubagentsDir(string session) => Path.Combine(Environment.GetEnvironmentVariable("HOME")!, ".claude", "projects", "-p", session, "subagents");
    static int bumps;
    // The folder's time moves on each write (set ahead, so writes in the same tick still differ).
    static void Bump(string dir) => Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddSeconds(++bumps));

    // A subagent of the Queen's session, as Claude Code writes it: its meta and its transcript.
    static void Subagent(string session, string id, string type, string what, string shape = "foreground", string toolUse = "", string[]? records = null)
    {
        var dir = SubagentsDir(session);
        Directory.CreateDirectory(dir);
        records ??= [
            "{\"type\":\"user\",\"isSidechain\":true,\"message\":{\"role\":\"user\",\"content\":\"" + what + "\"}}",
            """{"type":"assistant","isSidechain":true,"message":{"model":"claude-opus-5-5","content":[{"type":"tool_use","id":"s1","name":"Read","input":{"file_path":"/p/src/A.cs"}}]}}"""];
        File.WriteAllText(Path.Combine(dir, $"agent-{id}.jsonl"), string.Join("\n", records) + "\n");
        File.WriteAllText(Path.Combine(dir, $"agent-{id}.meta.json"), $$"""{"agentType":"{{type}}","description":"{{what}}","toolUseId":"{{(toolUse.Length > 0 ? toolUse : "tu-" + id)}}","requestShape":"{{shape}}"}""");
        Bump(dir);
    }

    // The Queen's transcript gets the Agent call's result (completed, async_launched, error).
    static void QueenEnd(string session, string id, string status) =>
        File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("HOME")!, ".claude", "projects", "-p", session + ".jsonl"),
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tu-" + id + "\",\"content\":\"report\"}]},\"toolUseResult\":{\"status\":\"" + status + "\",\"agentId\":\"" + id + "\"}}\n");

    // ... or a background agent's notification (completed, failed, killed).
    static void QueenNotified(string session, string id, string status) =>
        File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("HOME")!, ".claude", "projects", "-p", session + ".jsonl"),
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"<task-notification><task-id>" + id + "</task-id><status>" + status + "</status></task-notification>\"}}\n");

    // A small rule the Sage proposed for Worker that applied at once:
    // its molt id, and Worker's card from before.
    static (string Id, string OldCard) AppliedRule(Ctx c)
    {
        c.Store.CreateBee("Sage", "Reviews other bees", [], "", "approve", role: "evolver");
        var ids = new List<string>();
        for (int i = 0; i < 2; i++)
        {
            var t = Tasks.Create(c.Store, "Worker", "Look into it " + i, "", "read");
            Tasks.Record(c.Store, t, "Found it.", "done");
            c.Store.Award("task:" + t.Id, "honey", "tests green (task:x)", "queen");
            ids.Add(t.Id);
        }
        var old = c.Store.RequireBee("Worker").Doc.Body.Trim();
        // Stage 5: the first rule takes the place of a lone "None yet."
        c.Store.Molt("Worker", "a rule from its outcomes", old.Replace("None yet.", $"- Check twice ([task:{ids[0]}] [task:{ids[1]}])."), "Sage");
        var applied = c.Store.AutoApplied();
        c.Pre(applied.Count == 1, "the rule applied at once");
        return (applied[0].Id, old);
    }

    // A row on a bee's page by the start of its title (an AdwEntryRow unless said).
    static nint BeeRow(Ctx c, string bee, string title, string type = "AdwEntryRow")
    {
        c.Call("ShowBee", bee);
        c.Call("Go", "bees");
        c.Pump(300);
        var detail = c.Get<nint>("beeDetail");
        nint row = G.OfType(detail, type).FirstOrDefault(r => G.RowTitle(r).StartsWith(title));
        if (row == 0 && G.Button(detail, "edit") is var edit and not 0) { c.Click(edit); c.Pump(500); row = G.OfType(c.Get<nint>("beeDetail"), type).FirstOrDefault(r => G.RowTitle(r).StartsWith(title)); }
        c.Pre(row != 0, $"a \"{title}…\" row on {bee}'s page");
        return row;
    }

    // ---- dictation (stage 4) ----

    sealed record VoiceRig(string Bin, string Log, string Runtime, string Data, string Model);

    // Fakes for pw-record and whisper-cli, the only things on PATH, with temp
    // XDG_DATA_HOME (and the model in it) and XDG_RUNTIME_DIR (with a link to
    // broadway's socket, for a child window). An ERROR unless the pw-record
    // that dictation would run is the fake (or, without one, none at all).
    static VoiceRig VoiceFakes(Ctx c, bool pw = true, bool whisper = true, bool model = true, string whisperMode = "ok", string pwMode = "ok")
    {
        var root = Path.GetFullPath(Path.Combine(c.Root, "..", "voice"));
        var (bin, runtime, data) = (Path.Combine(root, "bin"), Path.Combine(root, "run"), Path.Combine(root, "data"));
        foreach (var d in new[] { bin, runtime, data }) Directory.CreateDirectory(d);
        var log = Path.Combine(root, "fakes.log");
        File.WriteAllText(log, "");
        void Script(string name, string text)
        {
            var path = Path.Combine(bin, name);
            File.WriteAllText(path, text);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (pw) Script("pw-record", """
            #!/bin/sh
            # A stand-in for pw-record: logs its start, writes a wav when told to stop (SIGINT), gives up after 2 minutes.
            for a; do out=$a; done
            echo "start $$ $*" >> "$HIVE_FAKE_LOG"
            stop() {
              echo "INT $$" >> "$HIVE_FAKE_LOG"
              if [ "$HIVE_FAKE_PW" = empty ]; then : > "$out"; else /usr/bin/head -c 3044 /dev/zero > "$out"; fi
              exit 0
            }
            trap stop INT
            i=0
            while [ $i -lt 1200 ]; do /usr/bin/sleep 0.1; i=$((i+1)); done
            echo "timeout $$" >> "$HIVE_FAKE_LOG"
            """);
        if (whisper) Script("whisper-cli", """
            #!/bin/sh
            # A stand-in for whisper-cli: what it "hears" depends on HIVE_FAKE_WHISPER.
            echo "whisper $*" >> "$HIVE_FAKE_LOG"
            case "$HIVE_FAKE_WHISPER" in
              fail) echo "loading the model" >&2; echo "whisper_init: failed to load model" >&2; exit 3 ;;
              empty) echo "[BLANK_AUDIO]"; echo "(silence)" ;;
              slow) /usr/bin/sleep 1.5; echo " Hello from the fake whisper." ;;
              *) echo " Hello from the fake whisper."; echo "[BLANK_AUDIO]" ;;
            esac
            """);
        var modelPath = Path.Combine(data, "hive", "whisper", "ggml-base.en.bin");
        if (model) { Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!); File.WriteAllText(modelPath, "not a model"); }
        // A child window (VoiceApp) finds broadway's socket through XDG_RUNTIME_DIR.
        var realRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "";
        if (Environment.GetEnvironmentVariable("BROADWAY_DISPLAY") is { Length: > 1 } display && int.TryParse(display[1..], out var n)
            && File.Exists(Path.Combine(realRuntime, $"broadway{n + 1}.socket")) && !File.Exists(Path.Combine(runtime, $"broadway{n + 1}.socket")))
            File.CreateSymbolicLink(Path.Combine(runtime, $"broadway{n + 1}.socket"), Path.Combine(realRuntime, $"broadway{n + 1}.socket"));
        Environment.SetEnvironmentVariable("PATH", bin);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtime);
        Environment.SetEnvironmentVariable("HIVE_FAKE_LOG", log);
        Environment.SetEnvironmentVariable("HIVE_FAKE_WHISPER", whisperMode);
        Environment.SetEnvironmentVariable("HIVE_FAKE_PW", pwMode);
        string? which = null;
        try { which = Text.Which("pw-record"); } catch (HiveException) { }
        c.Pre(pw ? which == Path.Combine(bin, "pw-record") : which == null, $"the pw-record dictation would run to be {(pw ? "the fake" : "none")}; it's {which ?? "none"}");
        c.Pre(Voice.ModelDir.StartsWith(data), $"the model folder in the temp XDG_DATA_HOME; it's {Voice.ModelDir}");
        c.Pre(Voice.WavPath().StartsWith(runtime), $"the wav in the temp XDG_RUNTIME_DIR; it's {Voice.WavPath()}");
        return new VoiceRig(bin, log, runtime, data, modelPath);
    }

    static List<string> VoiceLog(Ctx c)
    {
        var log = Path.GetFullPath(Path.Combine(c.Root, "..", "voice", "fakes.log"));
        return File.Exists(log) ? File.ReadAllLines(log).Where(l => l.Length > 0).ToList() : [];
    }

    static int StartPid(string start) => start.Split(' ') is [_, var pid, ..] && int.TryParse(pid, out var p) ? p : -1;

    // The fakes' processes still alive (their command lines name the fakes' folder).
    static List<string> FakeProcesses(Ctx c)
    {
        var bin = Path.GetFullPath(Path.Combine(c.Root, "..", "voice", "bin"));
        var alive = new List<string>();
        foreach (var d in Directory.EnumerateDirectories("/proc"))
        {
            try
            {
                var cmd = File.ReadAllText(Path.Combine(d, "cmdline")).Replace('\0', ' ');
                if (cmd.Contains(bin)) alive.Add($"{Path.GetFileName(d)}: {cmd.Trim()}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return alive;
    }

    static void KillPid(int pid)
    {
        try { if (pid > 0) Process.GetProcessById(pid).Kill(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    // Runs this program as `voice-app <how>` (the real App.Run) and waits until it records; its pw-record's pid.
    static (Process App, int Pid) StartVoiceApp(Ctx c, string how)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!, ["voice-app", how]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        var app = Process.Start(info)!;
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        app.OutputDataReceived += (_, e) => { if (e.Data != null) lines.Enqueue(e.Data); };
        app.ErrorDataReceived += (_, e) => { if (e.Data != null) lines.Enqueue("stderr: " + e.Data); };
        app.BeginOutputReadLine();
        app.BeginErrorReadLine();
        var sw = Stopwatch.StartNew();
        string? recording = null;
        while ((recording = lines.FirstOrDefault(l => l.StartsWith("RECORDING "))) == null && sw.ElapsedMilliseconds < 25000 && !app.HasExited) Thread.Sleep(100);
        if (recording == null) { Thread.Sleep(300); recording = lines.FirstOrDefault(l => l.StartsWith("RECORDING ")); }
        if (recording == null)
        {
            if (!app.HasExited) app.Kill();
            foreach (var l in VoiceLog(c)) if (StartPid(l) is var p and > 0) KillPid(p);
            c.Pre(false, "the app to start recording; it said: " + string.Join(" | ", lines.Take(20)));
        }
        voiceAppLines = lines;
        return (app, int.Parse(recording![10..]));
    }

    static System.Collections.Concurrent.ConcurrentQueue<string> voiceAppLines = new();

    // alt+v pressed and let go at once.
    static void Tap(nint entry)
    {
        G.PressKey(entry, 0x76, 8);
        G.ReleaseKey(entry, 0x76, 8);
    }

    // The prompt's selection (from == to: just the cursor).
    static unsafe (int From, int To) Selection(nint entry)
    {
        int a, b;
        G.gtk_editable_get_selection_bounds(entry, &a, &b);
        return (Math.Min(a, b), Math.Max(a, b)) is var r && a == b ? (N.gtk_editable_get_position(entry), N.gtk_editable_get_position(entry)) : r;
    }

    // `voice-app close|hold`: the real App.Run on a hive of its own; once its
    // window is up, a click on the mic, "RECORDING <pw-record pid>" on
    // stdout, then (close) the window closes, or (hold) it waits to be killed.
    public static int VoiceApp(string how)
    {
        // Never the real microphone: only ever with VoiceFakes' PATH.
        string? which = null;
        try { which = Text.Which("pw-record"); } catch (HiveException) { }
        if (which == null || !which.EndsWith("/voice/bin/pw-record") || Environment.GetEnvironmentVariable("HIVE_TESTS_DIR") is not { Length: > 0 })
        {
            Console.WriteLine("FAILED not run: pw-record here is " + (which ?? "none") + ", not the test's fake");
            return 3;
        }
        var dir = Path.Combine(Environment.GetEnvironmentVariable("HIVE_TESTS_DIR")!, $"voice-app-{Environment.ProcessId}", "hive");
        Environment.SetEnvironmentVariable("HIVE_HOME", dir);
        var store = new Store(dir);
        store.Init();
        var log = Environment.GetEnvironmentVariable("HIVE_FAKE_LOG")!;
        var sw = Stopwatch.StartNew();
        var (window, clicked) = ((nint)0, false);
        Action poll = null!;
        poll = () =>
        {
            if (sw.ElapsedMilliseconds > 20000) { Console.WriteLine("FAILED no recording in 20 s"); Environment.Exit(2); }
            var tops = G.gtk_window_get_toplevels();
            window = G.g_list_model_get_n_items(tops) > 0 ? G.g_list_model_get_item(tops, 0) : 0;
            if (window != 0) G.g_object_unref(window);
            if (!clicked && window != 0 && G.Descendants(window).FirstOrDefault(w => G.gtk_widget_has_css_class(w, "talk-mic") != 0) is var mic and not 0
                && N.gtk_widget_get_mapped(mic) && G.gtk_widget_get_visible(mic) != 0)
                clicked = G.gtk_widget_activate(mic) != 0;
            if (clicked && File.Exists(log) && File.ReadAllLines(log).FirstOrDefault(l => l.StartsWith("start ")) is { } start)
            {
                Console.WriteLine("RECORDING " + StartPid(start));
                Console.Out.Flush();
                if (how == "close") N.gtk_window_close(window);
                return; // hold: wait to be killed
            }
            Ui.After(200, poll);
        };
        N.LoadGlobally();
        Ui.After(500, poll);
        var code = App.Run(store);
        Console.WriteLine("CLOSED " + code);
        return code;
    }

    // ---- honey and misses (stage 2) ----

    // A review whose Arbiter counted Ash 3, Birch 0, Cedar 1, awarded
    // by the hive: honey, a miss, honey. Its run id.
    static string AwardedReview(Ctx c)
    {
        Reviews.SetUp(c.Store, new[] { "Cedar", "Birch", "Ash", "Arbiter" });
        const string run = "review-20261021-120000";
        var reviewers = new[] { "Ash", "Birch", "Cedar" }.Select(b =>
        {
            var t = Tasks.Create(c.Store, b, "Review src/", "", "read", run, "reviewer");
            Tasks.Record(c.Store, t, b + "'s findings", "done");
            return t;
        }).ToList();
        var over = Tasks.Create(c.Store, "Arbiter", "Oversee", "", "read", run, "overseer", reviewers.Select(t => t.Id));
        Tasks.Record(c.Store, over, "# Review\n## Reviewers\n| reviewer | confirmed | rejected | found alone |\n|---|---|---|---|\n| Ash | 3 | 0 | x |\n| Birch | 0 | 2 | y |\n| Cedar | 1 | 0 | z |\n", "done");
        Reviews.Award(c.Store, Reviews.Find(c.Store, run));
        c.Pre(reviewers.All(t => Tasks.Load(c.Store, t.Id).Doc.Get("reward_by") == "hive"), "the hive's awards in place");
        return run;
    }

    static string RewardOf(Doc d) => $"reward={d.Get("reward")} by={d.Get("reward_by")} note={d.Get("reward_note")}";

    // ---- the model picker (stage 1) ----

    // The menu's choices as the design names them, and the ids --model gets.
    static readonly (string Name, string Id)[] ModelChoices =
        [("default", ""), ("Opus 5.5", "claude-opus-5-5"), ("Sonnet 5.5", "claude-sonnet-5-5"), ("Haiku 4.5", "claude-haiku-4-5-20251001"), ("Fable 5.1", "claude-fable-5-1")];

    // The Queen's transcript, on the user's default model.
    const string OpusSaid = """{"type":"assistant","message":{"model":"claude-opus-5-5","usage":{"output_tokens":10},"content":[{"type":"text","text":"ok"}]}}""";

    // The menu button around the status line's model label, and its popover.
    static nint ModelButton(Ctx c)
    {
        var b = G.Ancestor(c.Get<nint>("talkModel"), "GtkMenuButton");
        c.Pre(b != 0, "the model label inside a menu button");
        return b;
    }
    static nint ModelPop(Ctx c) => G.gtk_menu_button_get_popover(ModelButton(c));
    static List<string> ModelMenu(Ctx c) => G.OfType(ModelPop(c), "GtkLabel").Select(G.Text).ToList();

    // Opens the model menu as a mouse click on the label does.
    static void OpenModelMenu(Ctx c)
    {
        var toggle = G.OfType(ModelButton(c), "GtkToggleButton").FirstOrDefault();
        c.Pre(toggle != 0 && c.PumpUntil(() => N.gtk_widget_get_mapped(toggle) && G.gtk_widget_get_width(toggle) > 0), "the model button on screen");
        c.Pre(G.ClickGestures(toggle, G.gtk_widget_get_width(toggle) / 2.0, G.gtk_widget_get_height(toggle) / 2.0) > 0, "a click gesture on the model button");
        c.Expect(c.PumpUntil(() => G.gtk_widget_get_visible(ModelPop(c)) != 0, 3000), "the model menu open after a click on the model");
    }

    // A mouse click on a choice in the open menu, by the name it shows.
    static void ClickModelChoice(Ctx c, string name)
    {
        var pop = ModelPop(c);
        var button = G.OfType(pop, "GtkButton").FirstOrDefault(b => G.OfType(b, "GtkLabel").Any(l => G.Text(l).TrimStart('✓', ' ') == name));
        c.Pre(button != 0, $"\"{name}\" in the model menu; it has " + string.Join(" | ", ModelMenu(c)));
        c.Pre(c.PumpUntil(() => N.gtk_widget_get_mapped(button) && G.gtk_widget_get_width(button) > 0, 3000), $"\"{name}\" laid out on screen ({G.gtk_widget_get_width(button)} px wide)");
        c.Pre(G.ClickGestures(button, G.gtk_widget_get_width(button) / 2.0, G.gtk_widget_get_height(button) / 2.0) > 0, "a click gesture on the choice");
        c.Pump(300);
    }

    // With HIVE_SHOTS set, the open model menu as a PNG there, for a look by
    // eye. (The window itself only draws from the frame clock's after-paint.)
    static void Shots(Ctx c, string name)
    {
        if (Environment.GetEnvironmentVariable("HIVE_SHOTS") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var pop = ModelPop(c);
        G.SavePng(G.gtk_popover_get_child(pop), pop, Path.Combine(dir, name + "-menu.png"));
    }

    // A finished Queen turn with this session, and its transcript under
    // ~/.claude/projects (HOME is the check's own).
    static BeeTask QueenSaid(Ctx c, string session, string folder, params string[] records)
    {
        var task = Tasks.QueenTalk(c.Store, "earlier", "", "edit", null);
        task.Doc.Set("session", session);
        task.Doc.Set("folder", folder);
        task.Doc.Save(task.Path);
        Tasks.Record(c.Store, Tasks.Load(c.Store, task.Id), "Done.", "done");
        var dir = Path.Combine(Environment.GetEnvironmentVariable("HOME")!, ".claude", "projects", folder.Replace('/', '-'));
        // HOME is shared by the checks of a run: a session's subagents from an earlier check go first.
        if (Directory.Exists(Path.Combine(dir, session))) Directory.Delete(Path.Combine(dir, session), true);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, session + ".jsonl"), string.Join("\n", records) + "\n");
        return Tasks.Load(c.Store, task.Id);
    }

    // A Queen turn the window sees answering (a Runner stands in for its runner).
    static BeeTask RunningQueen(Ctx c, string session)
    {
        var task = Tasks.QueenTalk(c.Store, "keep going", "", "edit", null);
        task.Doc.Set("resume", session);
        task.Doc.Set("status", "running");
        task.Doc.Set("pid", Runner(task.Id));
        task.Doc.Set("started", Text.Now);
        task.Doc.Save(task.Path);
        return task;
    }

    // A task the bee finished, with this result.
    static void Done(Ctx c, string bee, string result)
    {
        var task = Tasks.Create(c.Store, bee, "Review the whole repo", "", "read");
        task.Doc.Set("started", Text.Now);
        task.Doc.Save(task.Path);
        Tasks.Record(c.Store, Tasks.Load(c.Store, task.Id), result, "done");
    }

    // A result like a real review report: Markdown, many lines, long.
    const string LongResult = "## Review of ~/Projects/lantern\n\nReviewed example/lantern (~/Projects/lantern), the whole repo before its first release.\n\n**Which commit:** the brief says 1a2b3c4, but the repo has a single commit, 9f8e7d6, so I reviewed that.\n\n- **#1** Pages.cs:120 drops the first page.\n- **#2** Feeds.cs:252 writes the wrong date.\n\nEverything else held up under the tests.";

    // A task the window sees running (a Runner stands in for its runner),
    // at the flower bed.
    static BeeTask Running(Ctx c, string bee)
    {
        var task = Tasks.Create(c.Store, bee, "Look into it", "", "read");
        task.Doc.Set("status", "running");
        task.Doc.Set("pid", Runner(task.Id));
        task.Doc.Set("started", Text.Now);
        task.Doc.Save(task.Path);
        File.WriteAllText(task.StepsPath, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\tEdit\tsrc/A.cs\n");
        return task;
    }

    // A review with a reviewer at work; its run id.
    static string RunningReview(Ctx c)
    {
        Reviews.SetUp(c.Store, new[] { "Cedar", "Birch", "Ash", "Arbiter" });
        const string run = "review-20261018-120000";
        var reviewer = Tasks.Create(c.Store, "Cedar", "Review src/", "", "read", run: run, role: "reviewer");
        reviewer.Doc.Set("status", "running");
        reviewer.Doc.Set("pid", Runner(reviewer.Id));
        reviewer.Doc.Save(reviewer.Path);
        return run;
    }

    // ---- the parent: a display, then each check in a process of its own ----

    public static int RunAll(string[] args)
    {
        var verbose = args.Contains("-v");
        var filter = args.FirstOrDefault(a => a != "-v");
        // The display first, so a failure to start it leaves nothing behind.
        Process? broadway = null;
        string? display = null;
        if (Environment.GetEnvironmentVariable("GDK_BACKEND") != "broadway") (broadway, display) = StartBroadway();
        var scratch = Path.Combine(Path.GetTempPath(), $"hive-windowtests-{Environment.ProcessId}");
        Directory.CreateDirectory(scratch);
        var env = new Dictionary<string, string>
        {
            ["HIVE_TESTS_DIR"] = scratch,
            ["HOME"] = Path.Combine(scratch, "home"),
            ["HIVE_AGENTS_DIR"] = Path.Combine(scratch, "agents"),
            ["XDG_CONFIG_HOME"] = Path.Combine(scratch, "config"), // not the user's own gtk.css
            ["GSETTINGS_BACKEND"] = "memory",
            ["NO_AT_BRIDGE"] = "1",
        };
        Directory.CreateDirectory(env["HOME"]);
        // A user gtk.css like the one matugen writes from the wallpaper (GTK
        // loads it at priority 800): another accent, a specific rule for dim
        // labels, and old-style named colours (with them, libadwaita paints a
        // sidebar that isn't focused light).
        Directory.CreateDirectory(Path.Combine(env["XDG_CONFIG_HOME"], "gtk-4.0"));
        File.WriteAllText(Path.Combine(env["XDG_CONFIG_HOME"], "gtk-4.0", "gtk.css"),
            "@define-color accent_color #ff0000;\n@define-color sidebar_bg_color #000000;\n@define-color headerbar_backdrop_color #000000;\n"
            + ":root { --accent-color: #ff0000; }\nwindow:not(.bar) .dim-label { color: #626261; }\n");
        if (display != null)
        {
            env["GDK_BACKEND"] = "broadway";
            env["BROADWAY_DISPLAY"] = display;
        }
        int failed = 0, ran = 0;
        try
        {
            foreach (var (name, _) in tests)
            {
                if (filter != null && !name.Contains(filter)) continue;
                ran++;
                var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                info.ArgumentList.Add("one");
                info.ArgumentList.Add(name);
                foreach (var (k, v) in env) info.Environment[k] = v;
                using var p = Process.Start(info)!;
                var stderr = p.StandardError.ReadToEndAsync();
                var stdout = p.StandardOutput.ReadToEndAsync();
                var line = "FAIL timed out after 60 s";
                if (p.WaitForExit(60_000)) line = stdout.Result.Split('\n').LastOrDefault(l => l.StartsWith("RESULT ")) is { } r ? r[7..] : $"FAIL no result (exit {p.ExitCode})";
                else p.Kill(true);
                var (status, message) = line.IndexOf(' ') is var sp and > 0 ? (line[..sp], line[(sp + 1)..]) : (line, "");
                if (status != "PASS") failed++;
                Console.WriteLine($"{status,-5} {name}" + (message.Length > 0 ? $"\n      {message}" : ""));
                if (verbose || status == "FAIL" && message.StartsWith("no result"))
                    foreach (var l in (stdout.Result + stderr.Result).Split('\n').Where(l => l.Length > 0 && !l.StartsWith("RESULT "))) Console.WriteLine("      | " + l);
            }
        }
        finally
        {
            if (broadway != null)
            {
                // broadwayd leaves its socket behind when it's stopped; remove the one this run made.
                if (!broadway.HasExited) broadway.Kill();
                broadway.WaitForExit(2000);
                broadway.Dispose();
                File.Delete(Socket(int.Parse(env["BROADWAY_DISPLAY"][1..])));
            }
            Clean(scratch);
        }
        Console.WriteLine($"\n{ran - failed} passed, {failed} failed or couldn't run");
        return failed == 0 ? 0 : 1;
    }

    // Display :n listens on $XDG_RUNTIME_DIR/broadway<n+1>.socket.
    static string Socket(int display) =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath(), $"broadway{display + 1}.socket");

    static (Process, string) StartBroadway()
    {
        for (int n = 140; n < 240; n++)
        {
            if (File.Exists(Socket(n)) || File.Exists(Socket(n - 1))) continue;
            var info = new ProcessStartInfo("gtk4-broadwayd") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add($":{n}");
            var p = Process.Start(info)!;
            Thread.Sleep(400);
            if (!p.HasExited) return (p, $":{n}");
            p.Dispose();
        }
        throw new Exception("couldn't start gtk4-broadwayd");
    }

    // The checks leave unreadable folders behind; make them readable to delete them.
    static void Clean(string dir)
    {
        if (Environment.GetEnvironmentVariable("HIVE_TESTS_KEEP") is { Length: > 0 }) { Console.WriteLine($"(kept {dir})"); return; }
        try
        {
            Readable(dir);
            Directory.Delete(dir, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine($"(couldn't remove {dir}: {e.Message})"); }

        static void Readable(string d)
        {
            File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var sub in Directory.GetDirectories(d)) Readable(sub);
        }
    }

    // ---- the child: one check, inside the GTK main loop ----

    public static int RunOne(string name)
    {
        var test = tests.FirstOrDefault(t => t.Name == name);
        if (test.Run == null) { Console.WriteLine("RESULT ERROR no such check"); return 1; }
        // Only ever under the parent, which points HOME and the bees' subagent files into its temp folder.
        if (Environment.GetEnvironmentVariable("HIVE_TESTS_DIR") is not { Length: > 0 } || Environment.GetEnvironmentVariable("HIVE_AGENTS_DIR") is not { Length: > 0 })
        { Console.WriteLine("RESULT ERROR run the checks without `one`; they need the temp HOME and HIVE_AGENTS_DIR the parent sets"); return 1; }
        var dir = Path.Combine(Environment.GetEnvironmentVariable("HIVE_TESTS_DIR")!, $"check-{Environment.ProcessId}", "hive");
        Environment.SetEnvironmentVariable("HIVE_HOME", dir);
        N.LoadGlobally();
        var app = N.adw_application_new("dev.blomst.HiveTests", 0x20); // G_APPLICATION_NON_UNIQUE
        var code = 1;
        Ui.On(app, "activate", () =>
        {
            string result;
            try
            {
                var c = new Ctx(dir, app);
                test.Run(c);
                result = "PASS";
                code = 0;
            }
            catch (Ctx.Failed e) { result = "FAIL " + e.Message; }
            catch (Ctx.SetupError e) { result = "ERROR setup: " + e.Message; }
            catch (Exception e) { result = "ERROR " + (e is TargetInvocationException { InnerException: { } inner } ? inner : e).ToString().Replace("\n", " / "); }
            Console.WriteLine("RESULT " + result);
            Console.Out.Flush();
            Environment.Exit(code);
        });
        N.g_application_run(app, 0, 0);
        return code;
    }
}

// One check's world: a fresh hive (projects alpha and zeta, a bee Worker),
// the window's Store, a second Store for "another process", and the App.
sealed class Ctx
{
    public sealed class Failed(string m) : Exception(m);
    public sealed class SetupError(string m) : Exception(m);

    public readonly string Root;
    public readonly Store Store, Outside;
    public readonly List<string> Errors = [];
    public Action<string>? OnError;
    readonly nint application;
    App? app;

    public Ctx(string root, nint application)
    {
        Root = root;
        this.application = application;
        Store = new Store(root);
        Store.Init();
        Store.SetProject("alpha", "Alpha project", null, "# alpha\n\nThe alpha brief.");
        Store.SetProject("zeta", "Zeta project", null, "# zeta\n\nThe zeta brief.");
        Store.CreateBee("Worker", "Does the work", [], "", "approve");
        Outside = new Store(root);
    }

    public string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    // Opens the window as `hive app` would (App.Run's activate handler), then
    // lets it settle: its first focus check, the first file events.
    public Exception? Start(int pumpMs = 800, bool mayThrow = false)
    {
        app = new App(Store);
        Set("app", application);
        Exception? threw = null;
        try { Call("Activate"); }
        catch (TargetInvocationException e) when (mayThrow) { threw = e.InnerException; }
        var toast = Ui.OnError;
        Ui.OnError = m => { Errors.Add(m); OnError?.Invoke(m); toast?.Invoke(m); };
        Pump(pumpMs);
        return threw;
    }

    public void Pump(int ms)
    {
        var end = Stopwatch.GetTimestamp() + ms * Stopwatch.Frequency / 1000;
        while (Stopwatch.GetTimestamp() < end)
            if (G.g_main_context_iteration(0, 0) == 0) Thread.Sleep(2);
    }

    // Runs the main loop until done() or the time is up; says whether done().
    public bool PumpUntil(Func<bool> done, int ms = 2500)
    {
        var end = Stopwatch.GetTimestamp() + ms * Stopwatch.Frequency / 1000;
        while (!done())
        {
            if (Stopwatch.GetTimestamp() > end) return false;
            if (G.g_main_context_iteration(0, 0) == 0) Thread.Sleep(2);
        }
        return true;
    }

    // As a keyboard activation does: GTK shows the button pressed for a
    // moment, then emits clicked. The button has to be on screen.
    public void Click(nint button)
    {
        Pre(button != 0, "the button to be there");
        Pre(G.gtk_widget_activate(button) != 0, "the button to take the activation");
        Pump(600);
    }

    // A GTK CRITICAL (GTK_IS_LABEL failing on a freed widget, say) aborts the
    // check, so it ends as "no result (exit 134)" with the CRITICAL printed.
    // A use-after-free doesn't always crash; its CRITICALs always show.
    public static void FatalCriticals() => G.g_log_set_always_fatal(8); // G_LOG_LEVEL_CRITICAL

    // A slot GTK zeroes when the widget is freed (a weak pointer).
    public static nint Watch(nint widget)
    {
        var slot = Marshal.AllocHGlobal(8);
        Marshal.WriteIntPtr(slot, widget);
        G.g_object_add_weak_pointer(widget, slot);
        return slot;
    }
    public static bool Freed(nint slot) => Marshal.ReadIntPtr(slot) == 0;

    public void Expect(bool ok, string what) { if (!ok) throw new Failed("expected " + what); }
    public void Pre(bool ok, string what) { if (!ok) throw new SetupError("expected " + what); }

    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    public T Get<T>(string field) => (T)typeof(App).GetField(field, Private)!.GetValue(app)!;
    void Set(string field, object value) => typeof(App).GetField(field, Private)!.SetValue(app, value);
    public void SetField(string field, object value) => Set(field, value);
    // The method of that name taking these arguments (a fix may add an overload).
    public object? Call(string method, params object?[] args)
    {
        var m = typeof(App).GetMethods(Private).Where(m => m.Name == method && m.GetParameters().Length == args.Length).ToList();
        if (m.Count != 1) throw new SetupError($"one App.{method} with {args.Length} parameter(s); found {m.Count}");
        return m[0].Invoke(app, args);
    }

    // An entry written straight to the comb, as a file sync or an editor would.
    public void WriteEntry(string folder, string id, string title)
    {
        var path = Path("comb", folder, id + ".md");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var project = folder.Split('/')[0];
        File.WriteAllText(path, $"---\ntitle: {title}\ntype: note\nbee: queen\nproject: {project}\ncreated: {Text.Now}\nsummary: {title}\n---\n\nbody\n");
    }

    // The window has the entry among its newest (since stage 8 they feed the
    // stage's pops and the Timeline, not a "Latest in the hive" list).
    public int Inbox() => Get<System.Collections.IList>("items").Count;
    public uint HiveBadge() => G.adw_view_stack_page_get_badge_number(Get<nint>("hivePage"));

    // What the honeycomb cell for this bee says under its name.
    public string Sub(string bee)
    {
        foreach (var cell in (System.Collections.IList)typeof(App).GetField("occupied", Private)!.GetValue(app)!)
            if ((string)cell.GetType().GetProperty("Name")!.GetValue(cell)! == bee) return (string)cell.GetType().GetProperty("Sub")!.GetValue(cell)!;
        return "";
    }

    public bool ActivityHas(string id) => Get<List<Hit>>("activityHits").Any(h => h.Id == id);

    // A bee's sprite in the scene (the terminal redesign), read by reflection.
    public object SpriteObject(string bee) => Sprite(bee);
    object Sprite(string bee) => ((System.Collections.IDictionary)typeof(App).GetField("sprites", Private)!.GetValue(app)!)[bee]!;
    public object? Field(object o, string name) => F(o, name);
    static object? F(object o, string name) => o.GetType().GetField(name, Private)?.GetValue(o) ?? o.GetType().GetProperty(name, Private)?.GetValue(o);
    public string SpriteState(string bee) => (string)F(Sprite(bee), "State")!;
    public string SpriteText(string bee, string piece) => (string)F(F(Sprite(bee), piece)!, "Text")!;
    public double SpriteY(string bee) => (double)F(F(Sprite(bee), "Body")!, "Y")!;
    public bool SpriteFlying(string bee) => (bool)F(Sprite(bee), "Flying")!;
    public string VignetteArt() => typeof(App).GetField("vignette", Private)!.GetValue(app) is { } v ? (string)F(F(v, "Art")!, "Text")! : "";

    // A work line for a task, and its labels. Since the work pane (wp1)
    // the Hive page has no at-work list; the lines are on the shown bee's
    // page (beeWorking).
    public object? WorkRow(string key) => typeof(App).GetField("beeWorking", Private)!.GetValue(app) is { } lines ? ((System.Collections.IDictionary)F(lines, "Shown")!)[key] : null;

    // A refresh's look at the pane: its transcripts, bee tabs and how they ended.
    public void Poke() { Call("UpdateTalk"); Pump(200); }

    // The bee tabs' chips after ♛ Queen, and a chip's glyph (spinner, ✓ or ✗).
    public List<nint> BeeChips() => G.OfType(Get<nint>("talkTabs"), "GtkButton").Where(b => G.gtk_widget_has_css_class(b, "talk-chip") != 0).ToList();
    public string ChipGlyph(nint chip) => G.OfType(chip, "GtkLabel").Where(l => G.gtk_widget_has_css_class(l, "talk-glyph") != 0).Select(G.Text).FirstOrDefault() ?? "";

    // What the Queen's pane shows, line by line.
    public List<string> TalkLabels() => G.OfType(Get<nint>("talkLines"), "GtkLabel").Select(G.Text).ToList();

    // Types a message in the pane's prompt and presses Enter.
    public void Say(string text)
    {
        var entry = Get<nint>("talkEntry");
        N.gtk_editable_set_text(entry, text);
        G.g_signal_emit_by_name(entry, "activate", 0);
        Pump(300);
    }

    // A cell's centre in scene units, from the scene's own slots (the Queen
    // is 0, then the bees in order, then "+ new").
    public (double X, double Y) Slot(int i)
    {
        var slots = Get<List<(double X, double Y)>>("slots");
        Pre(i < slots.Count, $"cell {i} in the scene; it has {slots.Count}");
        return slots[i];
    }

    public int CellOf(string bee)
    {
        var bees = Get<System.Collections.IList>("bees");
        for (int i = 0; i < bees.Count; i++) if ((string)F(bees[i]!, "Name")! == bee) return i + 1;
        throw new SetupError($"{bee} among the hive's bees");
    }

    // A mouse click at (x, y) in scene units, through the scene's click gesture.
    public void ClickScene(double x, double y)
    {
        Pre(PumpUntil(() => Get<double>("sceneScale") > 0 && N.gtk_widget_get_mapped(Get<nint>("scene"))), "the scene laid out");
        var scale = Get<double>("sceneScale");
        Pre(G.ClickGestures(Get<nint>("scene"), x * scale, y * scale) > 0, "a click gesture on the scene");
        Pump(300);
    }

    // The page the window shows: hive, bees, reviews, memory or briefs.
    public string Page() => Marshal.PtrToStringUTF8(N.adw_view_stack_get_visible_child_name(Get<nint>("stack"))) ?? "";

    // Opens the header's ▤ inbox popover, as a click on ▤ does, and waits
    // until its panel is on screen (buttons in it only act once realized).
    public void OpenInbox()
    {
        var button = Get<nint>("inboxButton");
        Pre(PumpUntil(() => N.gtk_widget_get_mapped(button)), "the header's ▤ button on screen");
        Pre(G.gtk_widget_activate(button) != 0, "▤ to take the activation");
        Pre(PumpUntil(() => G.gtk_widget_get_visible(Get<nint>("inboxPop")) != 0 && N.gtk_widget_get_mapped(Get<nint>("inboxPanel")), 3000), "the inbox popover open with its panel on screen");
        Pump(300);
    }

    // The window at this size, laid out (forced: broadway's screen is 1024x768).
    public void Size(int w, int h)
    {
        var window = Get<nint>("window");
        N.gtk_widget_set_size_request(window, w, h);
        N.gtk_window_set_default_size(window, w, h);
        Pre(PumpUntil(() => G.gtk_widget_get_width(window) == w && G.gtk_widget_get_height(window) == h && Math.Abs(Get<double>("sceneScale") * 880 - G.gtk_widget_get_width(Get<nint>("scene"))) < 1, 5000),
            $"the window {w}x{h} with the scene laid out; it's {G.gtk_widget_get_width(window)}x{G.gtk_widget_get_height(window)}");
        Pump(300);
    }

    // A box in scene units (left, top, right, bottom), in the window's surface.
    public (double L, double T, double R, double B) SceneBox(double l, double t, double r, double b)
    {
        var s = Get<double>("sceneScale");
        var (window, area) = (Get<nint>("window"), Get<nint>("sceneArea"));
        Pre(G.InSurface(area, window, l * s, t * s) is not null, "the scene placed in the window");
        var a = G.InSurface(area, window, l * s, t * s)!.Value;
        var z = G.InSurface(area, window, r * s, b * s)!.Value;
        return (a.X, a.Y, z.X, z.Y);
    }

    // Where the bee's popover is on screen, in the window's surface.
    public (double L, double T, double R, double B) PopoverBox()
    {
        (double X, double Y, double W, double H)? box = null;
        Pre(PumpUntil(() => (box = G.PopoverBox(Get<nint>("popover"))) is { W: > 0 }), "the popover placed");
        var (x, y, w, h) = box!.Value;
        return (x, y, x + w, y + h);
    }

    // The bee's popover up: a FAIL if it never shows (GTK can't place a
    // popover wider than the screen, so it stays hidden), saying how wide it asks to be.
    public void ExpectPopover(string bee)
    {
        if (PumpUntil(Popped, 1500)) return;
        var child = G.gtk_popover_get_child(Get<nint>("popover"));
        int min = 0, nat = 0, mb, nb;
        unsafe { if (child != 0) G.gtk_widget_measure(child, 0, -1, &min, &nat, &mb, &nb); }
        Expect(false, $"{bee}'s popover shown; it isn't (its panel asks to be {nat} px wide, at least {min})");
    }

    // The popover beside what was clicked, on any side: touching it or at
    // most 24 px from it, lined up with it the other way, and not covering
    // its middle (where the bee is).
    public void ExpectNextTo((double L, double T, double R, double B) pop, (double L, double T, double R, double B) at, string what)
    {
        double dx = Math.Max(0, Math.Max(at.L - pop.R, pop.L - at.R)), dy = Math.Max(0, Math.Max(at.T - pop.B, pop.T - at.B));
        double mx = (at.L + at.R) / 2, my = (at.T + at.B) / 2;
        bool covers = pop.L < mx && mx < pop.R && pop.T < my && my < pop.B;
        Expect((dx == 0 && dy <= 24 || dy == 0 && dx <= 24) && !covers, $"{what}: the clicked spot ({at.L:0},{at.T:0})-({at.R:0},{at.B:0}), the popover ({pop.L:0},{pop.T:0})-({pop.R:0},{pop.B:0})");
    }

    public bool Popped() => G.gtk_widget_get_visible(Get<nint>("popover")) != 0;


    // What the design says the pane's width is for the split's present
    // width: 38% of it, never under 380 px (the split is the page less its margins).
    public int PaneWidthFor() => Math.Max(380, (int)(0.38 * G.gtk_widget_get_width(Get<nint>("hiveSplit"))));
    public nint RowLabel(object row, string name) => (nint)F(row, name)!;
    public nint RowOuter(object row) => (nint)F(F(row, "Wipe")!, "Outer")!;
    // An inbox item's row: whether it's held until its bee drops it off (null: no row), and its outer widget.
    object? InboxRow(string id) => ((System.Collections.IDictionary)typeof(App).GetField("inboxShown", Private)!.GetValue(app)!)[id];
    public bool? InboxHeld(string id) => InboxRow(id) is { } r ? (double)F(r, "Held")! >= 0 : null;
    public nint InboxOuter(string id) => InboxRow(id) is { } r ? (nint)F(F(r, "Wipe")!, "Outer")! : 0;
    // Off its cell: more than a cell's width or height away from home.
    public bool AwayFromHome(string bee)
    {
        var s = Sprite(bee);
        var home = ((double, double))typeof(App).GetMethod("Home", Private)!.Invoke(app, [bee])!;
        return Math.Abs((double)F(s, "X")! - home.Item1) > 20 || Math.Abs((double)F(s, "Y")! - home.Item2) > 20;
    }
    // Since stage 6 the waiting molts count on the Hive tab (the Molts page is gone).
    public uint MoltBadge() => G.adw_view_stack_page_get_badge_number(Get<nint>("hivePage"));
    public string BeeRow(string bee) => Get<List<string>?>("beeRows")?.FirstOrDefault(k => k.StartsWith(bee + "\n")) ?? "";

    // The honeycomb cell for this bee says it's working ("working · 2 min"
    // since stage 8, "working…" before).
    public bool Working(string bee)
    {
        foreach (var cell in (System.Collections.IList)typeof(App).GetField("occupied", Private)!.GetValue(app)!)
        {
            var t = cell.GetType();
            if ((string)t.GetProperty("Name")!.GetValue(cell)! == bee) return ((string)t.GetProperty("Sub")!.GetValue(cell)!).StartsWith("working");
        }
        return false;
    }
}
