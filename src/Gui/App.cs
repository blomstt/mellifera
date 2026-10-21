using System.Diagnostics;
using static Hive.Gui.Ui;

namespace Hive.Gui;

// The Hive's window: the honeycomb, the Queen, the bees, the memory, the
// projects and the molts waiting for the user. Every change goes through the
// same Store the MCP server uses; file events pick up what bees do.
sealed unsafe partial class App(Store store)
{
    public const string Id = "dev.blomst.Hive";

    nint app, window, stack, hivePage;
    StatusLine status = null!;
    Tabs tabs = null!, bottomTabs = null!;
    List<Bee> bees = [];
    List<Project> projects = [];
    long taskSignature, moltSignature;

    public static int Run(Store store)
    {
        if (!store.Exists) store.Init();
        N.LoadGlobally();
        N.CheckVersions();
        var a = new App(store);
        N.g_set_application_name("Hive");
        // One window per user, except for a hive elsewhere ($HIVE_HOME), which gets its own.
        a.app = N.adw_application_new(Id, Environment.GetEnvironmentVariable("HIVE_HOME") is { Length: > 0 } ? 0x20 : 0); // G_APPLICATION_NON_UNIQUE
        On(a.app, "activate", a.Activate);
        var code = N.g_application_run(a.app, 0, 0);
        // A recording never outlives the window. Its widgets are freed by
        // now, so not StopVoice: only pw-record and the wav.
        if (a.recording is { } r)
        {
            Voice.Stop(r);
            File.Delete(a.recordingWav);
        }
        return code;
    }

    void Activate()
    {
        if (window != 0) { N.gtk_window_present(window); return; }
        // The window's own palette, always dark, above the user's gtk.css.
        N.adw_style_manager_set_color_scheme(N.adw_style_manager_get_default(), 1); // ADW_COLOR_SCHEME_FORCE_DARK
        var css = N.gtk_css_provider_new();
        N.gtk_css_provider_load_from_string(css, Palette.Css());
        N.gtk_style_context_add_provider_for_display(N.gdk_display_get_default(), css, 801);
        window = N.adw_application_window_new(app);
        MotionHost = window;
        Icons.Detect();
        FatCaret();
        N.gtk_window_set_title(window, "Hive");
        N.gtk_window_set_default_size(window, 1280, 820);
        N.gtk_widget_set_size_request(window, 360, 294); // the smallest the narrow layout fits

        stack = N.adw_view_stack_new();
        // Each page's panels draw themselves in when it opens.
        Fx.Page = "hive";
        hivePage = N.adw_view_stack_add_titled_with_icon(stack, BuildHive(), "hive", "Hive", "view-grid-symbolic");
        Fx.Page = "bees";
        N.adw_view_stack_add_titled_with_icon(stack, BuildBees(), "bees", "Bees", "emoji-nature-symbolic");
        Fx.Page = "reviews";
        N.adw_view_stack_add_titled_with_icon(stack, BuildReviews(), "reviews", "Reviews", "edit-find-symbolic");
        Fx.Page = "memory";
        N.adw_view_stack_add_titled_with_icon(stack, BuildMemory(), "memory", "Memory", "system-search-symbolic");
        Fx.Page = "briefs";
        N.adw_view_stack_add_titled_with_icon(stack, BuildBriefs(), "briefs", "Briefs", "starred-symbolic");
        Fx.Page = "";

        // The tabs: in the header, or at the bottom when narrow.
        string[] pages = ["hive", "bees", "reviews", "memory", "briefs"];
        tabs = new Tabs(pages, Go);
        bottomTabs = new Tabs(pages, Go);
        switcher = tabs.Bar;
        switcherBar = Css(bottomTabs.Bar, "bottom-tabs");
        N.gtk_widget_set_halign(switcherBar, Center);
        N.gtk_widget_set_visible(switcherBar, false);
        // The header: ʚ●ɞ hive at the left, the tabs in the middle, the
        // counts, the inbox's ▤ and ✕ at the right (the window's own
        // controls are hidden).
        var header = N.adw_header_bar_new();
        N.adw_header_bar_set_title_widget(header, switcher);
        N.adw_header_bar_set_show_end_title_buttons(header, false);
        N.adw_header_bar_set_show_start_title_buttons(header, false);
        back = TextButton("‹ back", () => N.adw_navigation_split_view_set_show_content(CurrentSplit(), false), "back");
        N.gtk_widget_set_visible(back, false);
        var brand = Box(Horizontal, 10, Css(ArtLabel("ʚ●ɞ"), "brand-sprite"), Css(ArtLabel("hive"), "brand"), back);
        N.gtk_widget_set_size_request(brand, 200, -1);
        N.adw_header_bar_pack_start(header, brand);
        var close = Css(TextButton("✕", () => N.gtk_window_close(window), "close"), "close");
        Said(close, "Close");
        N.gtk_widget_set_tooltip_text(close, "Close the window");
        N.gtk_label_set_xalign(hiveStats, 1); // a short "a new hive" keeps ✕ at the edge
        var right = Box(Horizontal, 16, Expand(hiveStats, true, false), BuildInboxButton(), close);
        N.gtk_widget_set_halign(right, End);
        N.gtk_widget_set_size_request(right, 200, -1);
        N.adw_header_bar_pack_end(header, right);
        status = new StatusLine();
        var toolbar = N.adw_toolbar_view_new();
        N.adw_toolbar_view_add_top_bar(toolbar, header);
        N.adw_toolbar_view_set_content(toolbar, stack);
        N.adw_toolbar_view_add_bottom_bar(toolbar, status.Bar);
        N.adw_toolbar_view_add_bottom_bar(toolbar, switcherBar);
        N.adw_application_window_set_content(window, toolbar);
        OnError = m => status.Say(m, error: true);
        BuildUsage();
        On(stack, "notify::visible-child-name", ShowTab);
        ShowTab();

        // Narrower than 900sp: one pane at a time, the tabs at the bottom and
        // the work pane in a sheet.
        var breakpoint = N.adw_breakpoint_new(N.adw_breakpoint_condition_parse("max-width: 900sp"));
        On(breakpoint, "apply", () => Narrow(true));
        On(breakpoint, "unapply", () => Narrow(false));
        N.adw_application_window_add_breakpoint(window, breakpoint);
        On(stack, "notify::visible-child-name", UpdateBack);

        Refresh();
        ShowBee(null);
        ShowProject(null);
        ShowEntry(null);
        ShowRun(null);
        N.gtk_window_present(window);
        check = Check;
        OnFileChanged = FileChanged;
        WatchFolder(root);
        On(window, "notify::is-active", () =>
        {
            if (!N.gtk_window_is_active(window)) return;
            focusCheck = true;
            QueueCheck();
        });
    }

    void Refresh()
    {
        using var batch = store.Reads();
        store.Changed(); // this shows everything, so every change so far counts as seen
        bees = store.Bees();
        projects = store.Projects();
        molts = store.PendingMolts();
        applied = store.AutoApplied();
        moltSignature = MoltSignature();
        RefreshHive();
        RefreshBees();
        RefreshMemory();
        RefreshProjects();
        RefreshBeeMolts();
        RefreshReviews();
        queen.Update(store.QueenText);
        ShowBriefing();
    }

    // ---- following the files -------------------------------------------------

    // Bees and sessions change the hive only by writing files, so the window
    // watches its folders (the ones Sync walks, plus .tasks and bees/.molts)
    // and looks again ~250 ms after a burst of changes, and once more when it
    // gets focus. A quiet hive costs nothing.
    readonly Dictionary<string, long> watches = []; // folder -> its GFileMonitor
    Action check = null!; // Check, as one delegate for After
    bool checkQueued, filesChanged, moltsChanged, tasksChanged, stepsChanged, focusCheck;

    readonly string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(store.Root));
    readonly string tasksDir = Path.Combine(Path.GetFullPath(store.Root), ".tasks");
    readonly string moltsDir = Path.Combine(Path.GetFullPath(store.Root), "bees", ".molts");
    readonly string stateDir = Path.Combine(Path.GetFullPath(store.Root), ".state"); // subagents at work
    readonly string talkDir = Path.Combine(Path.GetFullPath(store.Root), ".talk");   // chats with a bee

    long MoltSignature() => Directory.Exists(moltsDir) ? Directory.GetLastWriteTimeUtc(moltsDir).Ticks : 0;

    void WatchFolder(string dir)
    {
        if (watches.ContainsKey(dir) || !Directory.Exists(dir)) return;
        if (Path.GetFileName(dir).StartsWith('.') && dir != tasksDir && dir != moltsDir && dir != stateDir && dir != talkDir && dir != root) return;
        var monitor = Watch(dir);
        if (monitor == 0) return;
        watches[dir] = monitor;
        // A folder can be gone (or unreadable) by now: then there's nothing in it to watch.
        try { foreach (var sub in Directory.EnumerateDirectories(dir)) WatchFolder(sub); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    void FileChanged(string path, int kind)
    {
        if (Path.GetFileName(path).StartsWith(".index.db")) return; // the index, which this window writes too
        if (kind == 3) WatchFolder(path); // created: a new folder is watched too
        else if (kind == 2) // deleted (or renamed): the folder and every folder in it
            foreach (var gone in watches.Keys.Where(k => k == path || k.StartsWith(path + "/")).ToList())
            {
                Unwatch((nint)watches[gone]);
                watches.Remove(gone);
            }
        // .tasks and bees/.molts count from the moment they're made: the
        // first task or molt is already in them by the time they're watched.
        var dir = Path.GetDirectoryName(path);
        if (path == stateDir) usageChanged = true; // .state made just now: the usage file may be in it already
        if (path == UsageFile || path.StartsWith(UsageFile + ".")) usageChanged = true; // the status line wrote the usage limits: only the meters change
        else if (dir == tasksDir && path.EndsWith(".steps")) stepsChanged = true; // a bee took a step: only the live steps change
        else if (dir == tasksDir || path == tasksDir || dir == stateDir || path == stateDir || dir == talkDir || path == talkDir) tasksChanged = true;
        else if (dir == moltsDir || path == moltsDir) moltsChanged = true;
        else filesChanged = true;
        QueueCheck();
    }

    void QueueCheck(uint ms = 250)
    {
        if (checkQueued) return;
        checkQueued = true;
        After(ms, check);
    }

    // Sync and the index's data_version run only here, after a file event or
    // on focus. A check that fails (the index is locked, a file went while it
    // was read) keeps what it was to look at and tries again a second later.
    void Check()
    {
        var (anyFile, anyMolt, anyTask, anyStep, focused) = (filesChanged, moltsChanged, tasksChanged, stepsChanged, focusCheck);
        if (usageChanged) { usageChanged = false; RefreshUsage(); }
        checkQueued = filesChanged = moltsChanged = tasksChanged = stepsChanged = focusCheck = false;
        try
        {
            using var batch = store.Reads();
            if (focused)
            {
                anyMolt |= MoltSignature() != moltSignature;
                anyTask |= Tasks.Signature(store) != taskSignature || StateSignature() != stateSignature;
            }
            if ((anyFile || focused) && store.Changed() || anyMolt) Refresh();
            else if (anyTask) { RefreshHive(); RefreshBees(); RefreshReviews(); }
            if (anyTask) RefreshTasks();
            else if (anyStep) RefreshSteps();
        }
        catch
        {
            filesChanged |= anyFile; moltsChanged |= anyMolt; tasksChanged |= anyTask; stepsChanged |= anyStep; focusCheck |= focused;
            QueueCheck(1000);
            throw;
        }
    }

    // What the window just did, typed into the status line.
    void Say(string message) => status.Say(message);

    // Honey gets no effects: said plainly, at once.
    void Honeyed(string message) => status.Say(message, plain: true);

    // What the user decides happens at once; the status line offers to take
    // it back for 10 s, instead of a dialog asking first. Closing the window
    // meanwhile keeps the decision.
    void Undoable(string message, Action undo) => status.Undoable(message, undo);

    // A fat honey caret, like a terminal's block cursor, but thin enough not
    // to hide the letter it sits on in an editor.
    static void FatCaret()
    {
        byte* value = stackalloc byte[24]; // a GValue
        new Span<byte>(value, 24).Clear();
        N.g_value_init(value, 15 << 2); // G_TYPE_DOUBLE
        N.g_value_set_double(value, 0.3);
        N.g_object_set_property(N.gtk_settings_get_default(), "gtk-cursor-aspect-ratio", value);
    }

    // A dialog's content in a panel, so it draws itself in as the dialog opens.
    static nint Framing(nint dialog, string title, nint child)
    {
        var panel = Panel(title, Margin(child, 8));
        N.adw_alert_dialog_set_extra_child(dialog, panel);
        return panel;
    }

    void Present(nint dialog, nint framed)
    {
        N.adw_dialog_present(dialog, window);
        Fx.DrawIn(framed);
    }

    // The tab strips follow the page shown; the page's panels draw themselves in.
    void ShowTab()
    {
        var page = Str(N.adw_view_stack_get_visible_child_name(stack));
        tabs.Show(page);
        bottomTabs.Show(page);
        if (page != shownPage && shownPage != null) Fx.DrawInPage(page);
        if (page == "reviews") StartRunScene(); // it stopped while the tab was away
        shownPage = page;
    }

    string? shownPage;

    // Honey (or a miss) taken back can be given again, with its note and giver.
    void TakeHoneyBack(Action<bool> reward, bool miss = false)
    {
        reward(false);
        Refresh();
        status.Undoable(miss ? "Miss taken back." : "Honey taken back.", () => { reward(true); Refresh(); }, plain: true);
    }

    // Who gave honey or a miss, when it wasn't the user: " from the Queen".
    static string GivenBy(Doc d) => d.Get("reward_by", "user") switch { "queen" => " from the Queen", "hive" => " from the hive", _ => "" };

    void Go(string page) => N.adw_view_stack_set_visible_child_name(stack, page);

    // ---- the adaptive layout -----------------------------------------------

    nint switcher, switcherBar, back, beeSplit, memorySplit, reviewSplit, briefSplit;
    bool narrow;

    nint CurrentSplit() => Str(N.adw_view_stack_get_visible_child_name(stack)) switch
    {
        "bees" => beeSplit, "memory" => memorySplit, "reviews" => reviewSplit, "briefs" => briefSplit, _ => 0,
    };

    // A split view's list beside its detail page; narrow, one at a time.
    // panelled: the list goes in a panel titled sidebarTitle (else it's
    // panels of its own, as Reviews' composer over its list).
    // contentPanel: the detail goes in a panel (else the page is panels of
    // its own, and the caller says which is panels[split]).
    nint Split(nint sidebar, string sidebarTitle, nint content, string contentTitle, int minWidth, int maxWidth, double fraction, bool panelled = true, bool contentPanel = true)
    {
        var split = N.adw_navigation_split_view_new();
        // The page's margins, and 20 px between the columns, as the mockups have them.
        var side = panelled ? sidebars[split] = Panel(sidebarTitle, sidebar) : sidebar;
        N.adw_navigation_split_view_set_sidebar(split, N.adw_navigation_page_new(Margin(side, 24, 10, 20, 20), sidebarTitle));
        var panel = contentPanel ? Panel(contentTitle, content) : content;
        if (contentPanel) panels[split] = panel;
        N.adw_navigation_split_view_set_content(split, N.adw_navigation_page_new(Margin(panel, contentPanel ? 24 : 0, 20, contentPanel ? 20 : 0, 10), contentTitle));
        N.adw_navigation_split_view_set_min_sidebar_width(split, minWidth);
        N.adw_navigation_split_view_set_max_sidebar_width(split, maxWidth);
        N.adw_navigation_split_view_set_sidebar_width_fraction(split, fraction);
        On(split, "notify::show-content", UpdateBack);
        return Expand(split);
    }

    readonly Dictionary<nint, nint> panels = [];   // a split view's detail panel
    readonly Dictionary<nint, nint> sidebars = []; // and its list's

    // The detail panel's title says what it shows: ┤ Mason ├.
    void Titled(nint split, string title) => SetPanelTitle(panels[split], title);

    // What the user picked in a list shows (narrow, in place of the list).
    static void Reveal(nint split) => N.adw_navigation_split_view_set_show_content(split, true);

    // Narrow, the header's Back button returns from a detail page to its list.
    void UpdateBack()
    {
        var split = CurrentSplit();
        N.gtk_widget_set_visible(back, narrow && split != 0 && N.adw_navigation_split_view_get_show_content(split));
    }

    void Narrow(bool on)
    {
        narrow = on;
        foreach (var split in new[] { beeSplit, reviewSplit, memorySplit, briefSplit }) N.adw_navigation_split_view_set_collapsed(split, on);
        N.gtk_widget_set_visible(switcher, !on);
        N.gtk_widget_set_visible(switcherBar, on);
        // The work pane beside the column moves into a sheet under it, and
        // back; its place closes up meanwhile.
        N.g_object_ref(pane);
        if (on)
        {
            N.adw_overlay_split_view_set_show_sidebar(hiveSplit, false);
            N.adw_overlay_split_view_set_sidebar(hiveSplit, 0);
            N.adw_bottom_sheet_set_sheet(hiveSheet, pane);
            N.adw_bottom_sheet_set_bottom_bar(hiveSheet, sheetBar);
        }
        else
        {
            N.adw_bottom_sheet_set_sheet(hiveSheet, 0);
            N.adw_bottom_sheet_set_bottom_bar(hiveSheet, 0);
            N.adw_overlay_split_view_set_sidebar(hiveSplit, pane);
            N.adw_overlay_split_view_set_show_sidebar(hiveSplit, true);
            Fx.DrawIn(pane); // back beside the hive, it draws itself in
        }
        N.g_object_unref(pane);
        N.gtk_widget_set_margin_end(hiveColumn, on ? 0 : 20); // the gap before the pane
        UpdateBack();
    }

    static nint Page(nint content, int max = 860) => Margin(Clamp(content, max), 24, 24, 32, 24);

    // A detail pane: the one scrolled window its pages go in.
    static nint Pane() => Scroll(0);

    // Puts a page in a detail pane. A rebuild of the page that's there keeps
    // how far down it was scrolled (the pane and its adjustment stay);
    // another page starts at the top.
    static void SetPage(nint pane, nint page, bool same = false)
    {
        N.gtk_scrolled_window_set_child(pane, page);
        if (!same) N.gtk_adjustment_set_value(N.gtk_scrolled_window_get_vadjustment(pane), 0);
    }

    static nint Framed(nint textView, int height)
    {
        var sw = Css(Scroll(textView), "frame");
        N.gtk_widget_set_size_request(sw, -1, height);
        return sw;
    }

    // A text to read at its full height: the page scrolls, not the text.
    static nint Framed(nint textView) => Css(textView, "frame");

    // A framed field that keeps its size instead of filling the column.
    static nint Fixed(nint w)
    {
        N.gtk_widget_set_vexpand(w, false);
        return w;
    }

    static nint EndAligned(params nint[] children)
    {
        var box = Box(Horizontal, 12, children);
        N.gtk_widget_set_halign(box, End);
        return box;
    }


    // ---- bees (BeesPage.cs) ------------------------------------------------------

    // ---- memory (MemoryPage.cs) ---------------------------------------------------

    // What a project drop-down says for a project: its name, then its folder.
    static string Choice(Project p) => p.Name + (p.Paths.Count > 0 ? $" ({p.Paths[0]})" : "");
    static string ChoiceName(string choice) => choice.Split(' ')[0];

    // Puts these choices after a drop-down's first item ("All projects", "No
    // project"), unless it already has them. The project picked stays picked.
    void SetChoices(nint drop, nint model, ref List<string> shown, List<string> choices)
    {
        if (choices.SequenceEqual(shown)) return;
        var at = (int)N.gtk_drop_down_get_selected(drop);
        var picked = at > 0 && at <= shown.Count ? ChoiceName(shown[at - 1]) : null;
        updatingChoices = true;
        for (int i = shown.Count; i > 0; i--) N.gtk_string_list_remove(model, (uint)i);
        foreach (var c in choices) N.gtk_string_list_append(model, c);
        shown = choices;
        N.gtk_drop_down_set_selected(drop, (uint)(choices.FindIndex(c => ChoiceName(c) == picked) + 1));
        updatingChoices = false;
    }

    // ---- briefs (BriefsPage.cs) ---------------------------------------------------

    // ---- team reviews (ReviewsPage.cs) -----------------------------------------

    void SetUpTeam()
    {
        var d = N.adw_alert_dialog_new("Set up the review team",
            "Reviewers look at the same thing on their own; the overseer gets all their reports and writes one. Name existing bees, or new names: those become bees with empty cards for you to fill in.");
        var reviewers = EntryRow("Reviewers, separated by commas", "");
        var overseer = EntryRow("Overseer", "");
        var group = Group("", null, reviewers, overseer);
        N.gtk_widget_set_size_request(group, 480, -1);
        var framed = Framing(d, "the review team", group);
        N.adw_alert_dialog_add_response(d, "cancel", "[ Cancel ]");
        N.adw_alert_dialog_add_response(d, "create", "[ Set up the team ]");
        N.adw_alert_dialog_set_response_appearance(d, "create", 1);
        N.adw_alert_dialog_set_close_response(d, "cancel");
        OnArg(d, "response", r =>
        {
            if (Str(r) != "create") return;
            Reviews.SetUp(store, [.. Text(reviewers).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), Text(overseer)]);
            Say("The review team is ready.");
            Refresh();
        });
        Present(d, framed);
    }
}
