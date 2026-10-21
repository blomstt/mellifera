using System.Diagnostics;
using static Hive.Gui.Ui;

namespace Hive.Gui;

// The Reviews page, as the mockup has it: a compact composer and the list
// of reviews on the left (each row its title in two lines, its project and
// age, the team as ʚʚʚ┄▸ʚ, how many findings and a tag), and the review on
// the right with its report rendered: the team's pipeline, the verdict box,
// the numbered findings with their tags, file links, rows and code, then
// "honey for" each bee.
sealed unsafe partial class App
{
    nint reviewsLeft, runList, runDetail, focusText, focusHint, reviewProject, reviewModel, runCount, runInfo;
    List<string> reviewChoices = [];
    List<ReviewRun> runs = [];
    string? shownRun, shownRunKey;
    List<string>? runRows;
    bool teamShown;

    nint BuildReviews()
    {
        reviewsLeft = Box(Vertical, 22);
        runDetail = Pane();
        reviewSplit = Split(Scroll(reviewsLeft), "reviews", runDetail, "review", 340, 420, 0.3, panelled: false);
        runInfo = PanelRight(panels[reviewSplit], "");
        return reviewSplit;
    }

    static void Clear(nint box)
    {
        for (var c = N.gtk_widget_get_first_child(box); c != 0; c = N.gtk_widget_get_first_child(box)) N.gtk_box_remove(box, c);
    }

    void RefreshReviews()
    {
        var reviewers = Reviews.TeamReviewers(store);
        var overseer = Reviews.TeamOverseer(store);
        var hasTeam = reviewers.Count > 0 && overseer != null;
        if (!hasTeam)
        {
            teamShown = false;
            runList = 0;
            Clear(reviewsLeft);
            N.gtk_box_append(reviewsLeft, Panel("reviews", Status(Art.Team, "No review team yet",
                "Bees with the reviewer role look at the same thing on their own and can't see each other's work. Then the bee with the overseer role gets all their reports and writes one.",
                Halign(Button("Set up the review team", SetUpTeam, "suggested-action"), Center))));
            return;
        }
        if (!teamShown)
        {
            teamShown = true;
            Clear(reviewsLeft);
            N.gtk_box_append(reviewsLeft, Composer(reviewers, overseer!));
            runList = List();
            runRows = null;
            OnArg(runList, "row-activated", row => { ShowRun(runs[N.gtk_list_box_row_get_index(row)].Id); Reveal(reviewSplit); });
            var list = Panel("reviews", Margin(runList, 4, 0, 0, 0));
            runCount = PanelRight(list, "");
            N.gtk_box_append(reviewsLeft, list);
        }

        SetChoices(reviewProject, reviewModel, ref reviewChoices, [.. projects.Select(Choice)]);
        runs = Reviews.Runs(store);
        SetPanelRight(runCount, runs.Count > 0 ? runs.Count.ToString() : "");
        var before = runRows;
        SetRows(runList, ref runRows, [.. runs.Select(r => $"{r.Id}\n{RunKey(r)}\n{Ago(r.Created)}")], i => RunRow(runs[i]));
        // A rebuild takes the placeholder with the rows, so it goes in again.
        if (runs.Count == 0 && !ReferenceEquals(before, runRows))
            N.gtk_list_box_set_placeholder(runList, Margin(Css(Label("No reviews yet. Type what to review above, or say \"review this code\" in any Claude session."), "small", "dim-label"), 12, 16, 12, 16));
        MarkRun();
        if (shownRun != null && runs.FirstOrDefault(r => r.Id == shownRun) is { } shown) ShowRun(shown.Id);
        else if (shownRun != null) ShowRun(null);
    }

    // The composer: what to review, in which project, and Review ▸.
    nint Composer(List<Bee> reviewers, Bee overseer)
    {
        var what = Css(N.gtk_label_new(""), "small", "dim-label");
        N.gtk_label_set_markup(what, Palette.Span(Palette.Pulse, "›") + " what to review");
        N.gtk_label_set_xalign(what, 0);
        focusText = Css(TextView("", editable: true), "small");
        N.gtk_text_view_set_left_margin(focusText, 10);
        N.gtk_text_view_set_top_margin(focusText, 8);
        // The example, ghosted, until something is typed.
        focusHint = Css(Label("the changes since main · src/render/ · a task id"), "small", "ghost");
        N.gtk_widget_set_can_target(focusHint, false);
        N.gtk_widget_set_valign(focusHint, Start);
        Margin(focusHint, 8, 10, 0, 11);
        var field = N.gtk_overlay_new();
        N.gtk_overlay_set_child(field, Framed(focusText, 70));
        N.gtk_overlay_add_overlay(field, focusHint);
        N.gtk_widget_set_vexpand(field, false); // 70 px, not the column's height when the list below is short
        On(N.gtk_text_view_get_buffer(focusText), "changed", () => N.gtk_widget_set_visible(focusHint, GetText(focusText).Length == 0));
        (reviewProject, reviewModel) = StringDropDown("no project (home folder)");
        reviewChoices = [];
        Expand(reviewProject, true, false);
        var review = Button("Review ▸", () =>
        {
            var p = (int)N.gtk_drop_down_get_selected(reviewProject);
            var run = Reviews.Start(store, p > 0 ? ChoiceName(reviewChoices[p - 1]) : "", GetText(focusText));
            SetText(focusText, "");
            Say($"{string.Join(", ", run.Reviewers.Select(r => r.Bee))} are reviewing on their own; {run.Overseer!.Bee} gets their reports after.");
            RefreshReviews();
            ShowRun(run.Id);
            RefreshHive();
        }, "suggested-action");
        var names = reviewers.Count == 1 ? reviewers[0].Name : string.Join(", ", reviewers.Take(reviewers.Count - 1).Select(r => r.Name)) + " and " + reviewers[^1].Name;
        var box = Box(Vertical, 0,
            what,
            Margin(field, 6, 0, 0, 0),
            Margin(Css(Label("Empty reviews the whole project."), "tiny", "faint"), 4, 0, 0, 0),
            Margin(Box(Horizontal, 10, Css(N.gtk_label_new("in"), "small", "dim-label"), reviewProject, review), 12, 0, 0, 0),
            Margin(Css(Label($"{names} review on their own; {overseer.Name} gets their reports and writes one."), "tiny", "faint"), 10, 0, 0, 0));
        return Panel("new review", Margin(box, 10, 16, 14, 16));
    }

    // What a review's page is made of (its tasks and their reports), to
    // rebuild it only when that changes.
    static string RunKey(ReviewRun run) => run.Id + string.Concat(run.All.Select(t => "\n" + t.Doc + t.Result));

    // A review in the list: its title in two lines, its project and age,
    // the team as ʚʚʚ┄▸ʚ, how many findings and a tag.
    nint RunRow(ReviewRun r)
    {
        var o = r.Overseer;
        var report = o is { Status: "done" } ? o.Result : "";
        var heading = Md.Title(report) is { Length: > 0 } t ? t : r.Focus;
        var title = Css(Label(heading), "bright");
        N.gtk_label_set_lines(title, 2);
        N.gtk_label_set_ellipsize(title, 3);
        var meta = Css(N.gtk_label_new((r.Project.Length > 0 ? r.Project + "   " : "") + Ago(r.Created)), "small", "dim-label");
        N.gtk_label_set_xalign(meta, 0);
        var team = Css(N.gtk_label_new(""), "small");
        N.gtk_label_set_markup(team, Team(r));
        var (findings, high) = Md.Findings(report);
        var count = Css(N.gtk_label_new(r.Running ? "reviewing" : o?.Status == "done" ? $"{findings} finding{(findings == 1 ? "" : "s")}" : o?.Status ?? "stopped"), "small");
        var verdict = Md.Verdict(report);
        var (tag, tone) = r.Running ? ($"{r.Reviewers.Count(t => !t.Running)}/{r.Reviewers.Count} in", "honey")
            : o?.Status != "done" ? ("", "")
            : verdict.Contains("n't merge", StringComparison.OrdinalIgnoreCase) ? ("don't merge", "bad")
            : high > 0 ? ($"{high} high", "bad")
            : findings == 0 ? ("clean", "") : ("no high", "");
        var line = Box(Horizontal, 8, team, count);
        if (tag.Length > 0) N.gtk_box_append(line, Css(N.gtk_label_new(tag), "tag", tone));
        var marker = Css(ArtLabel("›"), "honey", "run-marker");
        N.gtk_widget_set_valign(marker, Start);
        var row = Css(Box(Horizontal, 6, marker, Expand(Box(Vertical, 2, title, meta, Margin(line, 4, 0, 0, 0)), true, false)), "run-row");
        Said(row, $"{heading}: {(r.Running ? "reviewing" : $"{findings} findings{(tag.Length > 0 ? ", " + tag : "")}")}, {Ago(r.Created)}");
        return row;
    }

    // ʚʚʚ┄▸ʚ: each reviewer honey while it works, gray when done, red if it
    // failed; the overseer likewise.
    static string Team(ReviewRun r)
    {
        static uint Tone(BeeTask t) => t.Running && t.Status != "waiting" ? Palette.Pulse : t.Status == "done" ? Palette.Dim : t.Status == "failed" ? Palette.Red : Palette.Ghost;
        var sb = new System.Text.StringBuilder();
        foreach (var t in r.Reviewers) sb.Append(Palette.Span(Tone(t), "ʚ"));
        sb.Append(Palette.Span(Palette.Ghost, "┄▸"));
        if (r.Overseer is { } o) sb.Append(Palette.Span(Tone(o), "ʚ"));
        return sb.ToString();
    }

    // The row of the review shown is lit, with › at its left.
    void MarkRun()
    {
        if (runList == 0) return;
        var i = 0;
        for (var row = N.gtk_widget_get_first_child(runList); row != 0; row = N.gtk_widget_get_next_sibling(row), i++)
        {
            var on = i < runs.Count && runs[i].Id == shownRun;
            if (on) N.gtk_widget_add_css_class(row, "on");
            else N.gtk_widget_remove_css_class(row, "on");
        }
    }

    void ShowRun(string? id)
    {
        var run = id == null ? null : runs.FirstOrDefault(r => r.Id == id);
        var key = run == null ? "" : RunKey(run);
        if (key == shownRunKey) return;
        var same = id == shownRun;
        (shownRun, shownRunKey) = (id, key);
        Titled(reviewSplit, id ?? "review");
        MarkRun();
        (runScene, runShown) = (0, null); // its page goes; the next one makes its own
        runSpinners.Clear();
        if (run == null)
        {
            SetPanelRight(runInfo, "");
            N.gtk_scrolled_window_set_child(runDetail, Status(Art.Team, "Team reviews",
                "Pick a review to read its report. To start one, type what to review on the left, or use team_review_begin from a Claude session."));
            return;
        }
        SetPanelRight(runInfo, string.Join(" · ", new[] { run.Running ? "reviewing" : run.Stage, run.Project, Ago(run.Created) }.Where(s => s.Length > 0)));
        var o = run.Overseer;
        var report = o?.Result.Trim() ?? "";
        var done = o?.Status == "done";
        var title = done && Md.Title(report) is { Length: > 0 } t ? t : run.Focus;
        var page = Box(Vertical, 0, Css(Md.Para(title, Palette.Bright), "title-2"));
        if (title != run.Focus)
        {
            var focus = Css(Md.Para(run.Focus, Palette.Dim), "small");
            N.gtk_label_set_lines(focus, 3);
            N.gtk_label_set_ellipsize(focus, 3);
            N.gtk_box_append(page, Margin(focus, 6, 0, 0, 0));
        }
        var pipeline = Box(Horizontal, 0, BuildRunScene(run));
        if (run.Running)
        {
            var stop = Button("Stop", () => { Reviews.Stop(store, run.Id); Say("Review stopped."); RefreshReviews(); RefreshHive(); }, "destructive-action");
            N.gtk_widget_set_valign(stop, Center);
            N.gtk_widget_set_halign(stop, End);
            N.gtk_box_append(pipeline, Expand(stop, true, false));
        }
        N.gtk_box_append(page, Margin(pipeline, 22, 0, 0, 0));
        if (o != null && !done)
        {
            var status = o.Running ? (o.Status == "waiting" ? "Waiting for the reviewers to finish." : "Checking the findings…")
                : report.Length > 0 ? report : $"{o.Bee} {o.Status}.";
            N.gtk_box_append(page, Margin(Css(Md.Para(status, Palette.Dim), "small"), 26, 0, 0, 0));
        }
        if (done) N.gtk_box_append(page, Margin(Md.Render(report, review: true, skipTitle: true, open: path => OpenInProject(run, path)), 26, 0, 0, 0));
        // Honey for each bee whose work held up, then the report itself.
        var honeyRow = Css(Box(Horizontal, 10, Css(N.gtk_label_new("honey for"), "small", "dim-label")), "honey-row");
        foreach (var task in run.All)
            if (task.Status == "done") N.gtk_box_append(honeyRow, HoneyFor(task));
        N.gtk_box_append(honeyRow, Expand(N.gtk_box_new(Horizontal, 0), true, false));
        if (done && o != null)
        {
            N.gtk_box_append(honeyRow, Button("copy report", () => { CopyText(report); Say("Copied the report."); }));
            N.gtk_box_append(honeyRow, Button("open in editor", () => Process.Start(Hive.Text.Which("xdg-open"), [o.ResultPath])?.Dispose()));
        }
        N.gtk_box_append(page, Margin(honeyRow, 30, 0, 0, 0));
        // Each reviewer's own report, in its task view.
        var own = Box(Horizontal, 8, Css(N.gtk_label_new("their own reports"), "small", "dim-label"));
        foreach (var task in run.Reviewers)
            if (!task.Running) N.gtk_box_append(own, Button(task.Bee, () => ShowTask(task.Id), "flat"));
        N.gtk_box_append(page, Margin(own, 14, 0, 0, 0));
        var clamp = Halign(Clamp(page, 820), Start);
        SetPage(runDetail, Margin(clamp, 18, 32, 28, 32), same);
    }

    // A bee's honey button: "Ash", "⬡ Ash" once it earned honey, or
    // "✗ Ash" for a miss (the hive gives either from the overseer's count;
    // clicked, it's taken back, with Undo).
    nint HoneyFor(BeeTask t)
    {
        if (t.Doc.Get("reward") is "yes" or "no")
        {
            var miss = !t.Rewarded;
            var (said, by) = (t.Doc.Get("reward_note"), t.Doc.Get("reward_by", "user"));
            var b = Button((miss ? "✗ " : "⬡ ") + t.Bee, () => TakeHoneyBack(give => Tasks.Reward(store, t.Id, said, give, by, miss), miss), miss ? "flat" : "honeyed");
            Said(b, $"{t.Bee} {(miss ? "missed" : "earned honey")}{GivenBy(t.Doc)}; take it back");
            N.gtk_widget_set_tooltip_text(b, (miss ? "A miss" : "Honey") + GivenBy(t.Doc) + (said.Length > 0 ? $": {said}" : "") + ". Click to take it back.");
            return b;
        }
        var give = Button(t.Bee, () =>
        {
            Tasks.Reward(store, t.Id, "", true);
            Honeyed($"{t.Bee} earned honey");
            Refresh();
        });
        Said(give, $"Give {t.Bee} honey");
        N.gtk_widget_set_tooltip_text(give, $"Give {t.Bee} honey: its work held up");
        return give;
    }

    // A finding's file, in the review's folder, opened with the user's editor.
    void OpenInProject(ReviewRun run, string where)
    {
        var path = where.Trim();
        var colon = path.IndexOf(':');
        if (colon > 0) path = path[..colon];
        var folder = Hive.Text.Expand(run.Reviewers.FirstOrDefault()?.Doc.Get("folder") ?? "~");
        var full = Path.IsPathRooted(path) ? path : Path.Combine(folder, path);
        if (!File.Exists(full)) { Say($"There's no {path} in {Hive.Text.Tilde(folder)}."); return; }
        Process.Start(Hive.Text.Which("xdg-open"), [full])?.Dispose();
    }

    // ---- the team, working: the review's pipeline ---------------------------------

    // Each reviewer in a box (✓ done, a spinner while it works, ⬡ if it
    // earned honey), ┄┄┄┄▸, then the overseer in a honey box.
    nint runScene;
    ReviewRun? runShown;
    bool runMoving;
    Func<double, bool> runStep = null!;
    readonly List<long> runSpinners = [];

    nint BuildRunScene(ReviewRun run)
    {
        runShown = run;
        nint Mark(BeeTask t)
        {
            var l = Css(ArtLabel(), "small");
            if (t.Running && t.Status != "waiting")
            {
                N.gtk_label_set_markup(l, Palette.Span(Palette.Pulse, "⠋"));
                runSpinners.Add(l);
            }
            else N.gtk_label_set_markup(l, t.Status switch
            {
                "done" => Palette.Span(Palette.Green, "✓"), "failed" => Palette.Span(Palette.Red, "✗"),
                "waiting" => Palette.Span(Palette.Ghost, "⧗"), _ => Palette.Span(Palette.Ghost, "■"),
            } + (t.Rewarded ? Palette.Span(Palette.Pulse, " ⬡") : ""));
            return l;
        }
        nint Sprite() { var l = ArtLabel(); N.gtk_label_set_markup(l, Palette.Span(Palette.Pulse, "ʚ●ɞ")); return l; }
        var reviewers = Box(Vertical, 4);
        foreach (var t in run.Reviewers)
            N.gtk_box_append(reviewers, Css(Box(Horizontal, 6, Sprite(), Css(N.gtk_label_new(t.Bee), "small"), Mark(t)), "pipe-box"));
        N.gtk_widget_set_valign(reviewers, Center);
        var arrow = Css(ArtLabel("┄┄┄┄▸"), "ghost", "small");
        Margin(arrow, 0, 10, 0, 10);
        var scene = Box(Horizontal, 0, reviewers, arrow);
        if (run.Overseer is { } o)
        {
            var sub = Css(N.gtk_label_new(o.Status == "done" ? "1 report" : o.Running && o.Status != "waiting" ? "going through the reports…" : o.Status == "waiting" ? "waits for the reviewers" : o.Status), "tiny", "dim-label");
            N.gtk_label_set_xalign(sub, 0);
            var box = Css(Box(Vertical, 2, Box(Horizontal, 6, Sprite(), Css(N.gtk_label_new(o.Bee), "bright", "small"), Mark(o)), sub), "pipe-box", "overseer");
            N.gtk_widget_set_valign(box, Center);
            N.gtk_box_append(scene, box);
        }
        Said(scene, $"{run.Reviewers.Count} reviewers, {run.Reviewers.Count(r => r.Status == "done")} done; {run.Overseer?.Bee} {run.Overseer?.Status}");
        runScene = scene;
        StartRunScene();
        return scene;
    }

    // While its review runs, its spinners turn; again when the Reviews tab comes back.
    void StartRunScene()
    {
        if (runMoving || runShown is not { Running: true } || runScene == 0 || !Smooth) return;
        runStep ??= RunStep;
        runMoving = true;
        Animate(runStep);
    }

    bool RunStep(double t)
    {
        runMoving = runShown is { Running: true } && runScene != 0 && N.gtk_widget_get_mapped(runScene);
        if (runMoving)
            foreach (var l in runSpinners) N.gtk_label_set_markup((nint)l, Palette.Span(Palette.Pulse, "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏"[(int)(Frame(t) % 10)].ToString()));
        return runMoving;
    }
}
