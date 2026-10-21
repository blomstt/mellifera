using System.Diagnostics;
using static Hive.Gui.Ui;

namespace Hive.Gui;

// The Memory page, as the mockup has it: a search field with how many it
// found, a chip per type with its count, a project filter, the results
// cascading in (glyph, title in two lines, what and who and when), and the
// entry: its file as a header strip (path, copy id, open file, its front
// matter as a grid), then its title, its summary, its body rendered, and
// what it's linked to.
sealed unsafe partial class App
{
    nint search, searchCount, projectDrop, projectModel, typeChips, results, entryDetail;
    List<Hit> hits = [];
    List<string> projectChoices = [];
    Dictionary<string, int> projectCounts = [];
    string? typeFilter;
    bool updatingChoices;
    List<string>? resultRows;
    string? shownEntry, chipsShown;

    static readonly string[] TypeOrder = ["worklog", "lesson", "decision", "fact", "note", "review", "preference"];
    const string EmptyComb = "The comb is empty. Claude and the bees save what they learn here as they work; `hive import` brings in Claude Code memory files.";

    nint BuildMemory()
    {
        search = N.gtk_entry_new();
        N.gtk_entry_set_placeholder_text(search, "search the hive");
        On(search, "changed", () => RefreshMemory(fromTop: true));
        searchCount = Css(N.gtk_label_new(""), "tiny", "faint");
        var field = Prompt(search);
        N.gtk_box_append(field, searchCount);
        typeChips = N.gtk_flow_box_new();
        N.gtk_flow_box_set_selection_mode(typeChips, 0);
        N.gtk_flow_box_set_max_children_per_line(typeChips, 20);
        N.gtk_flow_box_set_column_spacing(typeChips, 6);
        N.gtk_flow_box_set_row_spacing(typeChips, 6);
        (projectDrop, projectModel) = StringDropDown("any");
        // The list shows each project with how many entries match there.
        N.gtk_drop_down_set_list_factory(projectDrop, Factory(name => projectCounts.TryGetValue(name, out var n) ? $"{name}  {n}" : name));
        On(projectDrop, "notify::selected", () => { if (!updatingChoices) RefreshMemory(fromTop: true); });
        var project = Box(Horizontal, 10, Css(N.gtk_label_new("project"), "small", "dim-label"), projectDrop);
        var searchPanel = Panel("search", Margin(Box(Vertical, 0, field, Margin(typeChips, 12, 0, 0, 0), Margin(project, 10, 0, 0, 0)), 10, 16, 14, 16));
        results = List();
        OnArg(results, "row-activated", row => { ShowEntry(hits[N.gtk_list_box_row_get_index(row)].Id); Reveal(memorySplit); });
        N.gtk_list_box_set_placeholder(results, Margin(Css(Label(EmptyComb), "small", "dim-label"), 12, 16, 12, 16));
        var left = Box(Vertical, 22, searchPanel, Panel("results", Margin(results, 4, 0, 0, 0)));
        entryDetail = Pane();
        return memorySplit = Split(Scroll(left), "memory", entryDetail, "entry", 360, 460, 0.34, panelled: false);
    }

    // A new search starts at the top; a refresh keeps the scroll.
    void RefreshMemory(bool fromTop = false)
    {
        var query = Text(search);
        projectCounts = store.CountBy("project", query, null, typeFilter);
        SetChoices(projectDrop, projectModel, ref projectChoices, [.. projects.Select(p => p.Name).Prepend("global")]);
        var p = N.gtk_drop_down_get_selected(projectDrop);
        var project = p > 0 ? projectChoices[(int)p - 1] : null;
        hits = store.Search(query, project, null, typeFilter, limit: 200, withGlobal: false);
        Chips(store.CountBy("type", query, project, null));
        N.gtk_label_set_text(searchCount, query.Trim().Length > 0 ? $"{hits.Count}{(hits.Count == 200 ? "+" : "")} found" : "");
        var before = resultRows;
        SetRows(results, ref resultRows, [.. hits.Select(h => $"{h.Id}\n{h.Title}\n{HitMeta(h)}")], i => HitRow(hits[i]), keepScroll: !fromTop);
        MarkEntry();
        if (fromTop && !ReferenceEquals(before, resultRows)) Cascade(results); // a new search's results cascade in
        N.gtk_list_box_set_placeholder(results, Margin(Css(Label(query.Length > 0 ? "Nothing matches." : EmptyComb), "small", "dim-label"), 12, 16, 12, 16));
    }

    // "all 182", "▤ worklog 64", …: the one picked is honey.
    void Chips(Dictionary<string, int> counts)
    {
        var all = 0;
        foreach (var n in counts.Values) all += n;
        var key = (typeFilter ?? "") + "\n" + string.Join(",", TypeOrder.Select(t => counts.GetValueOrDefault(t))) + all;
        if (key == chipsShown) return;
        chipsShown = key;
        N.gtk_flow_box_remove_all(typeChips);
        nint Chip(string? type, string text)
        {
            var b = Css(TextButton(text, () => { typeFilter = type; RefreshMemory(fromTop: true); }), "chip-filter");
            if (type == typeFilter) N.gtk_widget_add_css_class(b, "on");
            Said(b, (type ?? "all") + ", " + text[(text.LastIndexOf(' ') + 1)..]);
            return b;
        }
        N.gtk_flow_box_append(typeChips, Chip(null, $"all {all}"));
        foreach (var t in TypeOrder)
        {
            var n = counts.GetValueOrDefault(t);
            if (n > 0 || t == typeFilter) N.gtk_flow_box_append(typeChips, Chip(t, $"{TypeGlyph(t).Glyph} {t} {n}"));
        }
    }

    static string HitMeta(Hit h) =>
        string.Join(" · ", new[] { h.Kind == "entry" ? h.Type : h.Kind, h.Project is "global" ? "" : h.Project, h.Bee, HourOf(h.Created) }.Where(s => s.Length > 0));

    // A result: its type's glyph, its title in two lines, what and who and when.
    static nint HitRow(Hit h)
    {
        var (glyph, color) = TypeGlyph(h.Type);
        var mark = ArtLabel();
        N.gtk_label_set_markup(mark, Palette.Span(color, glyph));
        N.gtk_widget_set_valign(mark, Start);
        N.gtk_widget_set_size_request(mark, 10, -1);
        var title = Css(Label(h.Title), "bright");
        N.gtk_label_set_lines(title, 2);
        N.gtk_label_set_ellipsize(title, 3);
        var row = Css(Box(Horizontal, 12, mark, Expand(Box(Vertical, 2, title, Css(Label(HitMeta(h)), "tiny", "dim-label")), true, false)), "item");
        Said(row, $"{h.Title}, {HitMeta(h)}");
        return row;
    }

    void MarkEntry()
    {
        var i = 0;
        for (var row = N.gtk_widget_get_first_child(results); row != 0; row = N.gtk_widget_get_next_sibling(row), i++)
        {
            if (i < hits.Count && hits[i].Id == shownEntry) N.gtk_widget_add_css_class(row, "on");
            else N.gtk_widget_remove_css_class(row, "on");
        }
    }

    // A new search's rows come in one after another, top down: fading in
    // and sliding the last few pixels into place.
    void Cascade(nint list)
    {
        if (!Smooth) return;
        var n = 0;
        for (var r = N.gtk_widget_get_first_child(list); r != 0 && n < 40; r = N.gtk_widget_get_next_sibling(r)) n++;
        if (n == 0) return;
        var rows = new nint[n];
        rows[0] = N.gtk_widget_get_first_child(list);
        for (int i = 1; i < n; i++) rows[i] = N.gtk_widget_get_next_sibling(rows[i - 1]);
        foreach (var r in rows) { N.gtk_widget_set_opacity(r, 0); N.gtk_widget_set_margin_start(r, 6); }
        var start = Clock;
        Animate(t =>
        {
            var all = true;
            for (int i = 0; i < rows.Length; i++)
            {
                var a = Math.Clamp((t - start - i * 0.04) / 0.35, 0, 1);
                var e = 1 - Math.Pow(1 - a, 3);
                N.gtk_widget_set_opacity(rows[i], e);
                N.gtk_widget_set_margin_start(rows[i], (int)Math.Round(6 * (1 - e)));
                all &= a >= 1;
            }
            return !all;
        }, rows); // the next letter typed rebuilds the rows
    }

    void OpenEntry(string id)
    {
        ShowEntry(id);
        Go("memory");
        Reveal(memorySplit);
    }

    void ShowEntry(string? id)
    {
        shownEntry = id;
        Titled(memorySplit, "entry");
        MarkEntry();
        if ((id == null ? null : store.Locate(id)) is not { } found)
        {
            N.gtk_scrolled_window_set_child(entryDetail, Status(Art.Scroll, "The hive's memory",
                "Everything Claude and the bees saved: facts, decisions, worklogs, lessons, preferences and notes. Search on the left; pick one to read it."));
            return;
        }
        var (realId, path) = found;
        var doc = Doc.Load(path);
        var title = doc.Get("title", Path.GetFileNameWithoutExtension(path));
        var body = Box(Vertical, 0, Css(Md.Para(title, Palette.Bright), "entry-title"));
        if (doc.Get("summary") is { Length: > 0 } summary) N.gtk_box_append(body, Margin(Css(Md.Para(summary), "summary-box"), 12, 0, 0, 0));
        if (doc.Body.Trim().Length > 0) N.gtk_box_append(body, Margin(Md.Render(doc.Body.Trim()), 18, 0, 0, 0));
        if (Linked(realId, doc) is var linked and not 0) N.gtk_box_append(body, linked);
        if (!realId.Contains(':') && store.FindBee(doc.Get("bee")) is { } author) N.gtk_box_append(body, EntryHoney(realId, doc, author));
        var page = Box(Vertical, 0, FileHeader(realId, path, doc), Margin(Halign(Clamp(body, 820), Start), 22, 32, 28, 32));
        SetPage(entryDetail, page, id == shownEntryPage);
        shownEntryPage = id;
    }

    string? shownEntryPage;

    // Its file as the header strip: where it is, copy id and open file,
    // and its front matter as a grid.
    nint FileHeader(string id, string path, Doc doc)
    {
        var rel = Path.GetRelativePath(store.Root, path);
        var dir = Path.GetDirectoryName(rel) is { Length: > 0 } d ? d + "/" : "";
        var where = Css(N.gtk_label_new(""), "small");
        N.gtk_label_set_markup(where, Palette.Span(Palette.Dim, dir) + Palette.Span(Palette.Bright, Path.GetFileName(rel)));
        N.gtk_label_set_ellipsize(where, 1); // the start
        N.gtk_label_set_xalign(where, 0);
        // Ids are never guessed: one click copies it.
        var copy = Button("copy id", () => { CopyText(id); Say("Copied " + id + "."); });
        var open = Button("open file", () => Process.Start(Hive.Text.Which("xdg-open"), [path])?.Dispose());
        var top = Box(Horizontal, 12, Expand(where, true, false), copy, open);
        nint Key(string k) { var l = Css(N.gtk_label_new(k), "small", "faint"); N.gtk_label_set_xalign(l, 0); N.gtk_widget_set_size_request(l, 64, -1); return l; }
        nint Value(string markup) { var l = Css(N.gtk_label_new(""), "small"); N.gtk_label_set_markup(l, markup); N.gtk_label_set_xalign(l, 0); N.gtk_label_set_ellipsize(l, 3); return Expand(l, true, false); }
        var (glyph, color) = TypeGlyph(doc.Get("type"));
        var type = doc.Get("type") is { Length: > 0 } t ? Palette.Span(color, $"{glyph} {t}") : Palette.Span(Palette.Dim, id.Contains(':') ? id[..id.IndexOf(':')] : "");
        var grid = Box(Vertical, 4,
            Box(Horizontal, 12, Key("type"), Value(type), Key("bee"), Value(Markup(doc.Get("bee", "—")))),
            Box(Horizontal, 12, Key("project"), Value(Markup(doc.Get("project", "—"))), Key("created"), Value(Markup(doc.Get("created", "—")))));
        if (doc.GetList("tags") is { Count: > 0 } tags)
            N.gtk_box_append(grid, Box(Horizontal, 12, Key("tags"), Value(Palette.Span(Palette.Dim, string.Join("  ", tags.Select(x => "#" + x))))));
        return Css(Box(Vertical, 12, top, grid), "file-strip");
    }

    // LINKED: what it replaces, what replaced it, and the [[ids]] it names.
    nint Linked(string id, Doc doc)
    {
        var links = Box(Vertical, 4);
        var n = 0;
        void Link(string arrow, string to)
        {
            if (to == id || n >= 12) return;
            var hit = store.Locate(to) is { } f ? Doc.Load(f.Path) : null;
            var text = hit?.Get("title", to) ?? to;
            var b = Css(TextButton(text, () => ShowEntry(to)), "link", "small");
            N.gtk_widget_set_halign(b, Start);
            var line = Box(Horizontal, 8, Css(ArtLabel(arrow), "ghost", "small"), b, Css(N.gtk_label_new(hit?.Get("type") ?? ""), "small", "faint"));
            N.gtk_box_append(links, line);
            n++;
        }
        foreach (var old in doc.GetList("supersedes")) Link("─▸ replaces", old);
        if (doc.Get("superseded_by") is { Length: > 0 } newer) Link("◂─ replaced by", newer);
        var body = doc.Body;
        for (int at = body.IndexOf("[[", StringComparison.Ordinal); at >= 0; at = body.IndexOf("[[", at + 2, StringComparison.Ordinal))
            if (body.IndexOf("]]", at + 2, StringComparison.Ordinal) is var end and > 0 && end - at < 200)
                Link("─▸", body[(at + 2)..end].Replace("«", "").Replace("»", ""));
        if (n == 0) return 0;
        return Css(Box(Vertical, 8, Css(N.gtk_label_new("LINKED"), "caps"), links), "linked");
    }

    // Honey for the bee that wrote it: given (⬡, click to take it back), or offered.
    nint EntryHoney(string id, Doc doc, Bee author)
    {
        var row = Css(Box(Horizontal, 10, Css(N.gtk_label_new("honey for"), "small", "dim-label")), "honey-row");
        if (doc.Get("reward") is "yes" or "no")
        {
            var miss = doc.Get("reward") == "no";
            var (said, by) = (doc.Get("reward_note"), doc.Get("reward_by", "user"));
            var b = Button((miss ? "✗ " : "⬡ ") + author.Name, () =>
            {
                TakeHoneyBack(give =>
                {
                    store.RewardEntry(id, said, give, by, miss);
                    if (shownEntry == id) { shownEntryPage = null; ShowEntry(id); }
                }, miss);
            }, miss ? "flat" : "honeyed");
            Said(b, $"{author.Name} {(miss ? "missed" : "earned honey")}{GivenBy(doc)}; take it back");
            N.gtk_widget_set_tooltip_text(b, (miss ? "A miss" : "Honey") + GivenBy(doc) + (said.Length > 0 ? $": {said}" : "") + ". Click to take it back.");
            N.gtk_box_append(row, b);
        }
        else
        {
            var b = Button(author.Name, () =>
            {
                store.RewardEntry(id, "", true);
                Honeyed($"{author.Name} earned honey");
                shownEntryPage = null;
                ShowEntry(id);
                Refresh();
            });
            Said(b, $"Give {author.Name} honey");
            N.gtk_widget_set_tooltip_text(b, "Honey marks it as the example to follow. Work without honey isn't punished.");
            N.gtk_box_append(row, b);
        }
        return Margin(row, 26, 0, 0, 0);
    }
}
