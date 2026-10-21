using static Hive.Gui.Ui;

namespace Hive.Gui;

// The Briefs page, as the mockup has it: the Queen and the projects down
// the left with how long each is; the page picked in an editor with line
// numbers and its Markdown lit (headings, comments, bullets), "● unsaved"
// while it differs from the file, revert and save; and beside it "what
// Claude sees": the briefing a session would get, as 48 cells against the
// 9,000-character cap, the count, its sections as a stacked bar and a
// legend, and which section is the largest.
sealed unsafe partial class App
{
    // What sessions are told: the Queen (every session), then each project's
    // brief (sessions in its folders).
    nint projectList, projectDetail;
    string? selectedProject; // null: the Queen
    bool queenShown;
    Editor? shownBrief;
    List<string>? projectRows;
    Editor queen = null!;
    nint queenPage, queenEditorPanel;

    nint BuildBriefs()
    {
        projectList = List();
        OnArg(projectList, "row-activated", row =>
        {
            var i = N.gtk_list_box_row_get_index(row);
            if (i == 1) return; // the PROJECTS heading
            ShowProject(i == 0 ? null : projects[i - 2].Name);
            Reveal(briefSplit);
        });
        var newProject = Expand(Button("+ new project", NewProject, "wide"), true, false);
        var left = Box(Vertical, 0, Margin(projectList, 4, 0, 0, 0), Css(Margin(Box(Horizontal, 0, newProject), 10, 16, 10, 16), "row-sep"));
        projectDetail = Pane();
        BuildQueen();
        briefSplit = Split(Scroll(left), "briefs", projectDetail, "the queen", 260, 300, 0.24, contentPanel: false);
        panels[briefSplit] = queenEditorPanel;
        return briefSplit;
    }

    // The Queen's page: built once and kept, since Briefs swaps it with the project pages.
    void BuildQueen()
    {
        queen = new Editor(store.QueenText, "Queen");
        queenEditorPanel = EditorPanel("queen.md", queen, Hive.Text.Tilde(Path.Combine(store.Root, "queen.md")),
            "Empty. Every Claude session reads this page first: write who you are and what every session must know.", "save", () =>
        {
            var text = queen.Text;
            store.SaveQueen(text);
            queen.Settle(text);
            ShowBriefing();
            Say("The Queen is saved. New sessions get it.");
        });
        queenPage = Margin(Box(Horizontal, 20, queenEditorPanel, BriefingPanel(queen, true, null)), 24, 20, 20, 10);
        N.g_object_ref(queenPage);
    }

    // Every "what Claude sees" panel shown, after a change on disk.
    void ShowBriefing()
    {
        foreach (var v in briefViews) ShowBrief(v);
    }

    // A page's editor in a panel: line numbers, its Markdown lit, "● unsaved"
    // in the border while it differs from the file, and revert and save under it.
    nint EditorPanel(string title, Editor editor, string path, string emptyHint, string saveLabel, Action save)
    {
        Highlighter.Attach(editor);
        var empty = Margin(Css(Label(emptyHint), "small", "dim-label"), 12, 16, 0, 16);
        var unsaved = Css(N.gtk_label_new(""), "small");
        N.gtk_label_set_markup(unsaved, Palette.Span(Palette.Pulse, "●") + " unsaved");
        var count = Css(N.gtk_label_new(""), "small", "dim-label");
        N.gtk_label_set_xalign(count, 0);
        N.gtk_label_set_ellipsize(count, 1); // a long path loses its start, not the panel its width
        void Show()
        {
            N.gtk_widget_set_visible(unsaved, editor.Dirty);
            N.gtk_widget_set_visible(empty, editor.Text.Trim().Length == 0);
            N.gtk_label_set_text(count, $"{path} · {editor.Text.Length:N0} chars");
        }
        editor.Edited += Show;
        var bar = Css(Box(Horizontal, 10, Expand(count, true, false), Button("revert", () => { editor.Revert(); Show(); }), Button(saveLabel, () => { save(); Show(); }, "suggested-action")), "editor-bar");
        var area = Css(Expand(editor.View, true, true), "code-editor");
        var panel = Panel(title, Box(Vertical, 0, editor.Banner, empty, area, bar));
        PanelRight(panel, unsaved);
        Show();
        Expand(panel, true, false);
        N.gtk_widget_set_valign(panel, Start);
        return panel;
    }

    void RefreshProjects()
    {
        var keys = new List<string> { $":queen\n{store.QueenText.Trim().Length}", ":projects" };
        foreach (var p in projects) keys.Add($"{p.Name}\n{p.Summary}\n{p.Doc.Body.Trim().Length}");
        SetRows(projectList, ref projectRows, keys, i =>
        {
            if (i == 1)
            {
                var head = Css(N.gtk_label_new("PROJECTS"), "caps", "tiny", "faint");
                N.gtk_label_set_xalign(head, 0);
                return Margin(head, 12, 16, 4, 16);
            }
            var (glyph, color, name, summary, chars) = i == 0
                ? ("♛", Palette.Pulse, "Queen", "", store.QueenText.Trim().Length)
                : ("▸", Palette.Ghost, projects[i - 2].Name, projects[i - 2].Summary, projects[i - 2].Doc.Body.Trim().Length);
            var mark = ArtLabel();
            N.gtk_label_set_markup(mark, Palette.Span(color, glyph));
            N.gtk_widget_set_size_request(mark, 16, -1);
            var text = Box(Vertical, 0, Css(Label(name), i == 0 ? "bright" : ""));
            if (summary.Length > 0)
            {
                var s = Css(N.gtk_label_new(summary), "tiny", "faint");
                N.gtk_label_set_xalign(s, 0);
                N.gtk_label_set_ellipsize(s, 3);
                N.gtk_label_set_max_width_chars(s, 1);
                N.gtk_box_append(text, s);
            }
            var row = Css(Box(Horizontal, 12, mark, Expand(text, true, false), Css(N.gtk_label_new($"{chars:N0}"), "tiny", "dim-label")), "item");
            Said(row, $"{name}, {chars:N0} characters");
            return row;
        });
        // The heading isn't something to pick.
        if (N.gtk_widget_get_next_sibling(N.gtk_widget_get_first_child(projectList)) is var heading and not 0) N.gtk_list_box_row_set_activatable(heading, false);
        MarkProject();
        if (projects.FirstOrDefault(p => p.Name == selectedProject) is { } shown) shownBrief?.Update(shown.Doc.Body.Trim());
        else if (selectedProject != null) ShowProject(null);
    }

    void MarkProject()
    {
        var i = 0;
        for (var row = N.gtk_widget_get_first_child(projectList); row != 0; row = N.gtk_widget_get_next_sibling(row), i++)
        {
            var on = i == 0 ? queenShown || selectedProject == null : i >= 2 && i - 2 < projects.Count && projects[i - 2].Name == selectedProject;
            if (on) N.gtk_widget_add_css_class(row, "on");
            else N.gtk_widget_remove_css_class(row, "on");
        }
    }

    // Showing the project that's shown again leaves its page (and unsaved brief) alone.
    // No project (or one that's gone) shows the Queen.
    void ShowProject(string? name)
    {
        if (name != null && name == selectedProject) return;
        var project = name == null ? null : store.FindProject(name);
        if (project == null && queenShown) return;
        selectedProject = project?.Name;
        queenShown = project == null;
        shownBrief = null;
        briefViews.RemoveAll(v => !v.Queen); // the project page shown before goes
        MarkProject();
        if (project == null)
        {
            panels[briefSplit] = queenEditorPanel;
            SetPage(projectDetail, queenPage);
            return;
        }
        var brief = shownBrief = new Editor(project.Doc.Body.Trim(), "brief");
        var editor = EditorPanel($"{project.Name}/brief.md", brief, Hive.Text.Tilde(project.Path),
            "Empty. Sessions in this project's folders read it first: what it is, where things are, how to build and test it.", "save", () =>
        {
            var text = brief.Text;
            store.SetProject(project.Name, null, null, text);
            brief.Settle(text);
            Say("Brief saved.");
        });
        panels[briefSplit] = editor;
        // What the brief doesn't say: the project's summary and its folders.
        var summary = EntryRow("Summary", project.Summary);
        N.adw_entry_row_set_show_apply_button(summary, true);
        On(summary, "apply", () => { store.SetProject(project.Name, Text(summary), null, null); Say("Summary saved."); Refresh(); });
        var folders = EntryRow("Folders, separated by commas", string.Join(", ", project.Paths));
        N.adw_entry_row_set_show_apply_button(folders, true);
        On(folders, "apply", () =>
        {
            store.SetProject(project.Name, null, [.. Text(folders).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)], null);
            Say("Folders saved.");
            Refresh();
        });
        var working = bees.Where(b => b.Projects.Contains(project.Name)).Select(b => b.Name).ToList();
        var about = Panel("project", Margin(Box(Vertical, 10,
            Css(Label(working.Count > 0 ? "Bees: " + string.Join(", ", working) : "No bee is assigned to it yet."), "small", "dim-label"),
            Group("", null, summary, folders)), 10, 16, 14, 16));
        var column = Box(Vertical, 22, editor, about);
        Expand(column, true, false);
        SetPage(projectDetail, Margin(Box(Horizontal, 20, column, BriefingPanel(brief, false, project.Name)), 24, 20, 20, 10));
    }

    void NewProject()
    {
        var d = N.adw_alert_dialog_new("New project", "Sessions started in its folders get its brief.");
        var name = EntryRow("Name (lower case, like my-game)", "");
        var summary = EntryRow("Summary", "");
        var folder = EntryRow("Folder (like ~/Projects/my-game)", "");
        var group = Group("", null, name, summary, folder);
        N.gtk_widget_set_size_request(group, 460, -1);
        var framed = Framing(d, "new project", group);
        N.adw_alert_dialog_add_response(d, "cancel", "Cancel");
        N.adw_alert_dialog_add_response(d, "create", "Create");
        N.adw_alert_dialog_set_response_appearance(d, "create", 1);
        N.adw_alert_dialog_set_default_response(d, "create");
        N.adw_alert_dialog_set_close_response(d, "cancel");
        N.adw_alert_dialog_set_response_enabled(d, "create", false);
        Action check = () => N.adw_alert_dialog_set_response_enabled(d, "create", Text(name).Trim().Length > 0 && Text(summary).Trim().Length > 0);
        On(name, "changed", check);
        On(summary, "changed", check);
        OnArg(d, "response", r =>
        {
            if (Str(r) != "create") return;
            var p = store.SetProject(Text(name), Text(summary), Text(folder).Trim().Length > 0 ? [Text(folder).Trim()] : null, null);
            Say($"Project {p.Name} made.");
            Refresh();
            ShowProject(p.Name);
        });
        Present(d, framed);
    }

    // ---- what Claude sees ---------------------------------------------------------

    // The exact briefing a session gets: with no project or in a project's
    // folder, as the Queen's session or as a bee. With the meter against the
    // cap; live as the page's own text is edited (before it's saved).
    sealed class BriefView(nint text, nint projectDrop, nint projectModel, nint beeDrop, nint beeModel, Meter meter, Editor editor, bool queen, string? project)
    {
        public readonly nint Text = text, ProjectDrop = projectDrop, ProjectModel = projectModel, BeeDrop = beeDrop, BeeModel = beeModel;
        public readonly Meter Meter = meter;
        public List<string> Projects = [], Bees = [];   // what the drop-downs list after their first item
        public readonly Editor Editor = editor;
        public readonly bool Queen = queen;      // the editor is the Queen's page (else the project's brief)
        public readonly string? Project = project;
        public string Shown = "";
        public bool Updating;                             // its drop-downs are being refilled
    }

    readonly List<BriefView> briefViews = [];
    bool briefsDue;
    Action refreshBriefs = null!;

    nint BriefingPanel(Editor editor, bool queen, string? project)
    {
        var (projectDrop, projectModel) = StringDropDown("none");
        var (beeDrop, beeModel) = StringDropDown("the Queen's session");
        var text = TextView("", editable: false, mono: true);
        var view = new BriefView(text, projectDrop, projectModel, beeDrop, beeModel, new Meter(), editor, queen, project);
        Choices(view);
        N.gtk_drop_down_set_selected(projectDrop, (uint)(project == null ? 0 : view.Projects.IndexOf(project) + 1));
        briefViews.Add(view);
        On(projectDrop, "notify::selected", () => ShowBrief(view));
        On(beeDrop, "notify::selected", () => ShowBrief(view));
        // Typing in the page's editor: the briefing follows, 0.2 s after a pause.
        refreshBriefs ??= RefreshBriefs;
        editor.Edited += () =>
        {
            if (briefsDue) return;
            briefsDue = true;
            After(200, refreshBriefs);
        };
        ShowBrief(view);
        var preview = Framed(text);
        N.gtk_widget_set_visible(preview, false);
        var buttons = Box(Horizontal, 8,
            Button("preview the briefing", () => N.gtk_widget_set_visible(preview, !N.gtk_widget_get_visible(preview))),
            Button("copy", () => { CopyText(view.Shown); Say("Copied the briefing."); }));
        var pick = Box(Horizontal, 8, Css(N.gtk_label_new("project"), "small", "dim-label"), projectDrop, Css(N.gtk_label_new("for"), "small", "dim-label"), beeDrop);
        var panel = Panel("what Claude sees", Margin(Box(Vertical, 0, pick, Margin(view.Meter.Box, 18, 0, 0, 0), Margin(buttons, 14, 0, 0, 0), Margin(preview, 12, 0, 0, 0)), 12, 18, 16, 18));
        N.gtk_widget_set_size_request(panel, 400, -1);
        N.gtk_widget_set_valign(panel, Start);
        return panel;
    }

    void RefreshBriefs()
    {
        briefsDue = false;
        foreach (var v in briefViews) if (N.gtk_widget_get_mapped(v.Meter.Box)) ShowBrief(v);
    }

    // The projects and bees to pick from, as they are now; what was picked stays picked.
    void Choices(BriefView v)
    {
        static void Sync(nint drop, nint model, ref List<string> shown, List<string> names, Func<string, string> say)
        {
            if (names.SequenceEqual(shown)) return;
            var at = (int)N.gtk_drop_down_get_selected(drop);
            var picked = at > 0 && at <= shown.Count ? shown[at - 1] : null;
            for (int i = shown.Count; i > 0; i--) N.gtk_string_list_remove(model, (uint)i);
            foreach (var n in names) N.gtk_string_list_append(model, say(n));
            shown = names;
            N.gtk_drop_down_set_selected(drop, (uint)(picked == null ? 0 : names.IndexOf(picked) + 1));
        }
        var updating = v.Updating;
        v.Updating = true;
        Sync(v.ProjectDrop, v.ProjectModel, ref v.Projects, [.. projects.Select(p => p.Name)], n => n);
        Sync(v.BeeDrop, v.BeeModel, ref v.Bees, [.. bees.Select(b => b.Name)], n => n);
        v.Updating = updating;
    }

    void ShowBrief(BriefView v)
    {
        if (v.Updating) return;
        Choices(v);
        var p = (int)N.gtk_drop_down_get_selected(v.ProjectDrop);
        var b = (int)N.gtk_drop_down_get_selected(v.BeeDrop);
        var project = p > 0 && p <= v.Projects.Count ? v.Projects[p - 1] : null;
        var bee = b > 0 && b <= v.Bees.Count ? v.Bees[b - 1] : null;
        if (bee != null && store.FindBee(bee) == null) bee = null;
        if (project != null && store.FindProject(project) == null) project = null;
        string? queenText = v.Queen ? Doc.Parse(v.Editor.Text).Body : null, briefText = !v.Queen && project == v.Project ? v.Editor.Text : null;
        var full = store.Brief(project, bee, null, queenText, briefText, cap: false);
        v.Meter.Set(full);
        var sent = full.Length <= Store.BriefLimit ? full : store.Brief(project, bee, null, queenText, briefText, cap: true);
        if (sent != v.Shown) ReplaceText(v.Text, v.Shown = sent);
    }
}

// A briefing's size against the 9,000-character cap, as the mockup draws
// it: 48 cells that fill one after another (red near the cap), the count
// and the share of the cap, then its sections as a stacked bar with a
// legend, and a note on the largest. Plain numbers for screen readers.
sealed unsafe class Meter
{
    public readonly nint Box;
    readonly nint cells, count, percent, stack, legend, note;
    double shown = -1, target, from, start = -1;
    List<string> names = [];
    List<int> sizes = [];
    const int Cells = 48;
    static readonly uint[] colors = [Palette.Light, 0xC9A468, Palette.Run, Palette.Done, Palette.HoneyFill, Palette.Border];

    public Meter()
    {
        cells = N.gtk_drawing_area_new();
        OnDrawArea(cells, (_, cr, w, h) => PaintCells(cr, w));
        OnResize(cells, (a, w, _) => N.gtk_drawing_area_set_content_height(a, CellRows(w) * 17));
        N.gtk_drawing_area_set_content_height(cells, 34);
        count = Css(N.gtk_label_new(""), "meter-count");
        percent = Css(N.gtk_label_new(""), "honey");
        var row = Box(Horizontal, 10, count, Expand(N.gtk_box_new(Horizontal, 0), true, false), percent);
        N.gtk_widget_set_valign(count, End);
        stack = N.gtk_drawing_area_new();
        N.gtk_drawing_area_set_content_height(stack, 10);
        OnDrawArea(stack, (_, cr, w, h) => PaintStack(cr, w, h));
        legend = Box(Vertical, 4);
        note = Css(Label(""), "small", "dim-label", "meter-note");
        Box = Box(Vertical, 0, cells, Margin(row, 8, 0, 0, 0), Margin(stack, 16, 0, 0, 0), Margin(legend, 12, 0, 0, 0), Margin(note, 16, 0, 0, 0));
    }

    // All 48 in a row when there's room for cells 6 px wide, else rows of 9 px cells.
    static int CellWidth(int w) => (w + 2) / Cells - 2 >= 6 ? Math.Min(14, (w + 2) / Cells - 2) : 9;
    static int PerRow(int w) => Math.Max(1, (w + 2) / (CellWidth(w) + 2));
    static int CellRows(int w) => (Cells + PerRow(w) - 1) / PerRow(w);

    // The briefing as it would be sent (uncapped), split at its ## sections.
    public void Set(string text)
    {
        var limit = Store.BriefLimit;
        target = text.Length;
        if (shown < 0 || !Smooth || !N.gtk_widget_get_mapped(Box)) { shown = target; N.gtk_widget_queue_draw(cells); } // off screen: no need to move
        else
        {
            var running = start >= 0;
            (from, start) = (shown, Clock);
            if (!running)
                Animate(t =>
                {
                    var p = Math.Clamp((t - start) / 0.6, 0, 1);
                    shown = from + (target - from) * (1 - Math.Pow(1 - p, 3));
                    N.gtk_widget_queue_draw(cells);
                    if (p < 1) return true;
                    start = -1;
                    return false;
                }, Box);
        }
        N.gtk_label_set_markup(count, $"{text.Length:N0}" + $"<span size=\"small\" foreground=\"#{Palette.Dim:X6}\"> / {limit:N0} chars</span>");
        N.gtk_label_set_text(percent, $"{Math.Round(100.0 * text.Length / limit)}%" + (text.Length > limit ? $" · {text.Length - limit:N0} cut off" : ""));
        N.gtk_widget_remove_css_class(count, "bad");
        if (text.Length >= limit * 0.9) N.gtk_widget_add_css_class(count, "bad");
        Said(Box, $"{text.Length:N0} of {limit:N0} characters used");

        // The sections: the part before the first "## " is its heading's.
        names = [];
        sizes = [];
        var name = "title and intro";
        var size = 0;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("## "))
            {
                if (size > 0) { names.Add(name); sizes.Add(size); }
                (name, size) = (Short(line[3..]), 0);
            }
            size += line.Length + 1;
        }
        if (size > 0) { names.Add(name); sizes.Add(size); }
        // Largest first, as the legend lists them.
        for (int i = 1; i < sizes.Count; i++)
            for (int k = i; k > 0 && sizes[k] > sizes[k - 1]; k--)
            {
                (sizes[k], sizes[k - 1]) = (sizes[k - 1], sizes[k]);
                (names[k], names[k - 1]) = (names[k - 1], names[k]);
            }
        for (var c = N.gtk_widget_get_first_child(legend); c != 0; c = N.gtk_widget_get_first_child(legend)) N.gtk_box_remove(legend, c);
        for (int i = 0; i < names.Count; i++)
        {
            var color = colors[Math.Min(i, colors.Length - 1)];
            var dot = N.gtk_drawing_area_new();
            N.gtk_widget_set_size_request(dot, 10, 10);
            N.gtk_widget_set_valign(dot, Center);
            OnDrawArea(dot, Swatch);
            swatches[dot] = color;
            N.g_signal_connect_data(dot, "destroy", (nint)(delegate* unmanaged<nint, nint, void>)&Gone, 0, 0, 0);
            var label = Css(N.gtk_label_new(names[i]), "small");
            N.gtk_label_set_xalign(label, 0);
            N.gtk_box_append(legend, Box(Horizontal, 10, dot, Expand(label, true, false), Css(N.gtk_label_new($"{sizes[i]:N0}"), "small", "dim-label")));
        }
        N.gtk_widget_queue_draw(stack);
        // Which section to trim, once the briefing takes a real share of the limit.
        N.gtk_label_set_markup(note, sizes.Count == 0 || text.Length < limit / 4 ? Markup($"Room for {limit - text.Length:N0} more chars before the briefing is cut short.") : Palette.Span(Palette.Pulse, "! ") + Markup($"{char.ToUpperInvariant(names[0][0])}{names[0][1..]} is the largest section: {sizes[0]:N0} chars, {Math.Round(100.0 * sizes[0] / Math.Max(1, text.Length))}% of this briefing."));
    }

    // Each legend swatch's colour, by its drawing area (dropped as it goes).
    static readonly Dictionary<long, uint> swatches = [];
    static readonly Action<nint, nint, int, int> Swatch = (a, cr, w, h) => { if (swatches.TryGetValue(a, out var c)) Ink.Fill(cr, 0, 0, w, h, c); };

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    static void Gone(nint area, nint data) => swatches.Remove(area);

    static string Short(string heading)
    {
        heading = heading.Trim().TrimEnd(':');
        var words = heading.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = words.Length <= 4 ? heading : string.Join(' ', words.Take(4)) + "…";
        return name.Length > 30 ? name[..29] + "…" : name;
    }

    void PaintCells(nint cr, int w)
    {
        var limit = Store.BriefLimit;
        var filled = (int)Math.Round(Math.Clamp(shown / limit, 0, 1) * Cells);
        var color = shown >= limit * 0.9 ? Palette.Red : Palette.Pulse;
        int per = PerRow(w), cw = CellWidth(w);
        for (int i = 0; i < Cells; i++)
            Ink.Fill(cr, i % per * (cw + 2), i / per * 17, cw, 14, i < filled ? color : Palette.Rule);
    }

    void PaintStack(nint cr, int w, int h)
    {
        Ink.Fill(cr, 0, 0, w, h, 0x141412);
        var scale = (double)Math.Max(Store.BriefLimit, (int)target);
        double x = 0;
        for (int i = 0; i < sizes.Count; i++)
        {
            var width = Math.Max(1, sizes[i] / scale * w);
            Ink.Fill(cr, x, 0, Math.Max(1, width - 1), h, colors[Math.Min(i, colors.Length - 1)]);
            x += width;
        }
    }
}

// Line numbers left of an editor, and its Markdown lit as the mockup has
// it: "# " bright and bold, "## " honey and bold, <!-- comments --> faint,
// a bullet's dash dark honey.
static unsafe class Highlighter
{
    public static void Attach(Editor editor)
    {
        var view = editor.View;
        var buffer = N.gtk_text_view_get_buffer(view);
        N.gtk_text_buffer_create_tag_color(buffer, "md-h1", "foreground", Palette.Hex(Palette.Bright), 0);
        N.gtk_text_buffer_create_tag_color(buffer, "md-h2", "foreground", Palette.Hex(Palette.Pulse), 0);
        N.gtk_text_buffer_create_tag_color(buffer, "md-comment", "foreground", Palette.Hex(Palette.Ghost), 0);
        N.gtk_text_buffer_create_tag_color(buffer, "md-bullet", "foreground", Palette.Hex(Palette.Flight), 0);
        N.gtk_text_buffer_create_tag_weight(buffer, "md-bold", "weight", 700, 0);
        N.gtk_text_view_set_wrap_mode(view, 3); // words, then characters
        N.gtk_text_view_set_left_margin(view, 12);
        N.gtk_text_view_set_top_margin(view, 18);
        N.gtk_text_view_set_bottom_margin(view, 10);
        N.gtk_text_view_set_pixels_below_lines(view, 4);
        var gutter = N.gtk_drawing_area_new();
        N.gtk_widget_set_size_request(gutter, 34, -1);
        OnDrawArea(gutter, (_, cr, w, h) => PaintGutter(view, cr, w));
        N.gtk_text_view_set_gutter(view, 3, gutter); // GTK_TEXT_WINDOW_LEFT
        editor.Edited += () => { Light(buffer, editor.Text); N.gtk_widget_queue_draw(gutter); };
        Light(buffer, editor.Text);
    }

    // The buffer's lines, lit.
    static void Light(nint buffer, string text)
    {
        byte* a = stackalloc byte[256], b = stackalloc byte[256]; // GtkTextIter is 80 bytes
        N.gtk_text_buffer_get_start_iter(buffer, a);
        N.gtk_text_buffer_get_end_iter(buffer, b);
        N.gtk_text_buffer_remove_all_tags(buffer, a, b);
        void Tag(string tag, int line, int from, int to)
        {
            N.gtk_text_buffer_get_iter_at_line_offset(buffer, a, line, from);
            N.gtk_text_buffer_get_iter_at_line_offset(buffer, b, line, to);
            N.gtk_text_buffer_apply_tag_by_name(buffer, tag, a, b);
        }
        var lines = text.Split('\n');
        var comment = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var length = Chars(line, line.Length);
            if (comment || line.Contains("<!--"))
            {
                var start = comment ? 0 : line.IndexOf("<!--", StringComparison.Ordinal);
                var end = line.IndexOf("-->", start, StringComparison.Ordinal);
                comment = end < 0;
                Tag("md-comment", i, Chars(line, start), end < 0 ? length : Chars(line, end + 3));
                continue;
            }
            if (line.StartsWith("# ")) { Tag("md-h1", i, 0, length); Tag("md-bold", i, 0, length); }
            else if (line.StartsWith("## ") || line.StartsWith("### ")) { Tag("md-h2", i, 0, length); Tag("md-bold", i, 0, length); }
            else if (line.TrimStart() is var t && (t.StartsWith("- ") || t.StartsWith("* ")))
            {
                var at = Chars(line, line.Length - t.Length);
                Tag("md-bullet", i, at, at + 1);
            }
        }
    }

    // Characters (GTK's offsets) in the first `units` UTF-16 units of s.
    static int Chars(string s, int units)
    {
        var n = units;
        for (int i = 0; i < units && i < s.Length; i++) if (char.IsLowSurrogate(s[i])) n--;
        return n;
    }

    // Each line's number at the top of its (maybe wrapped) line.
    static void PaintGutter(nint view, nint cr, int w)
    {
        var buffer = N.gtk_text_view_get_buffer(view);
        byte* iter = stackalloc byte[256];
        var lines = N.gtk_text_buffer_get_line_count(buffer);
        for (int i = 0; i < lines; i++)
        {
            N.gtk_text_buffer_get_iter_at_line(buffer, iter, i);
            int y, h, wx, wy;
            N.gtk_text_view_get_line_yrange(view, iter, &y, &h);
            N.gtk_text_view_buffer_to_window_coords(view, 3, 0, y, &wx, &wy); // in the gutter (GTK_TEXT_WINDOW_LEFT)
            Ink.Text(cr, w - 10, wy + 13, Ink.C(Palette.Border, (i + 1).ToString()), 12, 1);
        }
    }
}
