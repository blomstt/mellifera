using static Hive.Gui.Ui;

namespace Hive.Gui;

// Work lines, on a bee's page: three lines per piece of work, as the
// mockup has them. A spinner, the bee, what it was asked and the minutes;
// "└ " and its live step typing out, with a blinking cursor; a braille bar
// filling with the time it has taken, and which step at which station.
// Lines type in when work starts and wipe out when it's over; a stopped
// line freezes to ■ first. Clicking a line opens the bee's popover.
sealed unsafe partial class App
{
    sealed class WorkRow(Work work, Wipe wipe, nint spinner, nint tree, nint bee, nint title, nint step, nint cursor, nint bar, nint info, nint time)
    {
        public Work Work = work;
        public readonly Wipe Wipe = wipe;
        public readonly nint Spinner = spinner, Tree = tree, Bee = bee, Title = title, Step = step, Cursor = cursor, Bar = bar, Info = info, Time = time;
        public string Said = "", Ended = ""; // the step it shows; how it ended ("stopped", "failed", "done")
        public string Spun = "", Barred = "", Informed = "", Timed = "", Titled = "";
        public double Typed;                 // when its step last changed (the blinking cursor waits for the typing)
        public bool Blink;
    }

    // A set of work lines in a box, keyed by task and updated in place (so
    // typing and spinners carry on): a bee's own working and waiting tasks
    // on its page.
    sealed class Lines(App app, nint box, Dictionary<string, WorkRow> shown, Action<WorkRow> click)
    {
        public readonly nint Box = box;
        public readonly Dictionary<string, WorkRow> Shown = shown;
        readonly List<WorkRow> leaving = [];
        Action? leaveLater;
        bool filled;

        public bool Busy => Shown.Count > 0 || leaving.Count > 0;

        // Shows these lines in this order; says whether any came or went.
        public bool Update(List<Work> order)
        {
            var first = !filled;
            filled = true;
            var changed = false;
            foreach (var (key, row) in Shown.ToList())
            {
                if (order.Exists(o => o.Task!.Id == key) || row.Wipe.Leaving) continue;
                // Over: a stopped or failed line shows it a moment first.
                row.Ended = app.FindTask(key)?.Status ?? "done";
                Shown.Remove(key);
                changed = true;
                if (row.Ended is "stopped" or "failed" && Smooth)
                {
                    N.gtk_label_set_text(row.Spinner, row.Ended == "stopped" ? "■" : "✗");
                    N.gtk_widget_add_css_class(row.Spinner, row.Ended == "stopped" ? "stopped" : "bad");
                    TypeIn(row.Step, row.Ended, double.PositiveInfinity); // at once, and a step still typing in stops
                    row.Wipe.Leave();
                    leaving.Add(row);
                    leaveLater ??= LeaveLater;
                    After(1200, leaveLater);
                }
                else row.Wipe.Out();
            }
            nint after = 0;
            foreach (var w in order)
            {
                var key = w.Task!.Id;
                if (!Shown.TryGetValue(key, out var row))
                {
                    row = Shown[key] = app.WorkRowFor(w, click);
                    if (after == 0) N.gtk_box_prepend(Box, row.Wipe.Outer);
                    else N.gtk_box_insert_child_after(Box, row.Wipe.Outer, after);
                    changed = true;
                    // A new line types in: the bee, then what it's doing.
                    if (!first) TypeIn(row.Bee, w.Bee, 60, "");
                }
                row.Work = w;
                // Heard as Doing says it ("editing src/A.cs"); shown as the
                // tool call, as Claude Code shows it ("Edit src/A.cs").
                var said = Doing(w);
                var step = said[..said.LastIndexOf(" · ")];
                if (step != row.Said)
                {
                    row.Said = step;
                    row.Typed = Clock;
                    app.SpeakRow(row);
                    var shown = w.Step is { } s && !w.Waiting ? (s.Tool == "think" ? "thinking…" : $"{s.Tool} {s.What}") : step;
                    if (first) N.gtk_label_set_text(row.Step, shown);
                    else TypeIn(row.Step, shown);
                }
                if (Hive.Text.OneLine(w.Title) is var title && title != row.Titled) N.gtk_label_set_text(row.Title, row.Titled = title);

                N.gtk_box_reorder_child_after(Box, row.Wipe.Outer, after);
                after = row.Wipe.Outer;
            }
            Draw(Clock);
            app.StartWork();
            return changed && !first;
        }

        void LeaveLater()
        {
            foreach (var row in leaving) row.Wipe.Out();
            leaving.Clear();
        }

        // The spinners and the time bars at time t.
        public void Draw(double t)
        {
            var smooth = Smooth;
            var step = smooth ? Frame(t) : 0;
            foreach (var row in Shown.Values)
            {
                var w = row.Work;
                const string spin = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
                var s = w.Waiting ? "⧗" : smooth ? spin[(int)(step % spin.Length)].ToString() : "⠿";
                if (s != row.Spun) N.gtk_label_set_text(row.Spinner, row.Spun = s);
                var bar = TimeBar(DateTime.Now - w.Started);
                if (bar != row.Barred)
                {
                    N.gtk_label_set_markup(row.Bar, row.Barred = bar);
                    app.SpeakRow(row);
                }
                var time = w.Waiting ? "waits" : Short(w.Started);
                if (time != row.Timed) N.gtk_label_set_text(row.Time, row.Timed = time);
                var info = w.Log is { } log && log.Steps > 0 ? $"step {log.Steps} · {(w.Step?.Station is { } st && st != Steps.Thinking ? StationFor(st) : "thinking")}" : "";
                if (info != row.Informed) N.gtk_label_set_text(row.Info, row.Informed = info);
                // The cursor after the step blinks once the step has typed out.
                var blink = !w.Waiting && (!smooth || t - row.Typed > 1.5 && (long)(t / 0.55) % 2 == 0);
                if (blink != row.Blink) N.gtk_widget_set_opacity(row.Cursor, (row.Blink = blink) ? 1 : 0);
            }
        }
    }

    readonly List<Lines> allLines = [];
    bool workMoving;
    Func<double, bool>? workStep;

    // One motion step for every set of lines, while any has a line.
    void StartWork()
    {
        if (workMoving || !allLines.Exists(l => l.Busy) || !Smooth) return;
        workMoving = true;
        Animate(workStep ??= WorkStep);
    }

    WorkRow WorkRowFor(Work w, Action<WorkRow> click)
    {
        nint L(string css, string text = "")
        {
            var l = Css(ArtLabel(text), css);
            N.gtk_label_set_xalign(l, 0);
            return l;
        }
        var step = (L("work-line"));
        N.gtk_label_set_ellipsize(step, 3); // end
        var title = (L("work-title", Hive.Text.OneLine(w.Title)));
        N.gtk_label_set_ellipsize(title, 3);
        Expand(title, true, false);
        // A screen reader hears the row as one sentence; its parts are hidden.
        nint spin = L("work-spin"), tree = L("work-tree"), bee = L("work-bee", w.Bee), cursor = L("work-cursor", "▌"), bar = L("work-bar"), info = L("work-info"), time = L("work-time");
        N.gtk_widget_set_opacity(cursor, 0);
        Expand(step, false, false);
        N.gtk_widget_set_hexpand(cursor, true);
        var line2 = Box(Horizontal, 0, L("work-tree", "└ "), step, cursor);
        var line3 = Box(Horizontal, 8, bar, info);
        foreach (var l in new[] { line2, line3 }) N.gtk_widget_set_margin_start(l, 22);
        var box = Css(Box(Vertical, 0, Box(Horizontal, 10, spin, tree, bee, title, time), line2, line3), "work-row");
        var row = new WorkRow(w, new Wipe(box), spin, tree, bee, title, step, cursor, bar, info, time);
        OnClick(box, (_, _) => click(row));
        return row;
    }

    bool WorkStep(double t)
    {
        foreach (var l in allLines) l.Draw(t);
        workMoving = allLines.Exists(l => l.Busy);
        return workMoving;
    }

    // "Mason, working: editing src/Tasks.cs, 12 minutes".
    void SpeakRow(WorkRow row)
    {
        var w = row.Work;
        var state = w.Waiting ? "waiting" : "working: " + row.Said;
        Said(row.Wipe.Row, $"{w.Bee}, {state}, {Minutes(w.Started)}");
    }

    // "12m", "1h 05m": how long it has been going, as the list says it.
    static string Short(DateTime since)
    {
        var m = Math.Max(0, (int)(DateTime.Now - since).TotalMinutes);
        return m < 60 ? $"{m}m" : $"{m / 60}h {m % 60:00}m";
    }

    // Eight braille cells filling over an hour, a dot about every minute.
    static string TimeBar(TimeSpan took)
    {
        int[] fill = [0x00, 0x40, 0x44, 0x46, 0x47, 0xC7, 0xE7, 0xF7, 0xFF];
        var dots = (int)Math.Clamp(took.TotalMinutes / 60 * 64, took.TotalSeconds > 5 ? 1 : 0, 64);
        var full = new System.Text.StringBuilder();
        var empty = new System.Text.StringBuilder();
        for (int i = 0; i < 8; i++)
        {
            var d = Math.Clamp(dots - i * 8, 0, 8);
            if (d > 0) full.Append((char)(0x2800 + fill[d]));
            else empty.Append('⣀');
        }
        return $"<span foreground=\"#{Palette.Honey:X6}\">{full}</span><span foreground=\"#{Palette.Border:X6}\">{empty}</span>";
    }
}
