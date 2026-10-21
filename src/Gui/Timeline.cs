using static Hive.Gui.Ui;

namespace Hive.Gui;

// The Timeline under the scene, the page's whole width: three hours, every
// bee that worked as a lane, drawn as the mockup draws it. A ruler with the
// hours and ▼ at now; each lane a row of dots with hour lines and the now
// line; bars for runs (tasks, reviews, subagents) in three states (done,
// working with a shimmer moving along it, failed); ticks for what was saved,
// which pop up when new; a legend under it. Each lane's name is a real
// label a screen reader hears as a sentence; hover over a lane says what's
// under the pointer, and a click opens it.
sealed unsafe partial class App
{
    // A bar (From < To) or a tick (From == To) in a bee's lane. Open is what
    // a click opens: "task:<id>", "run:<id>", an entry's id, or nothing.
    sealed class Mark(string lane, DateTime from, DateTime to, int kind, string title, string open)
    {
        public readonly string Lane = lane, Title = title, Open = open;
        public readonly DateTime From = from, To = to;
        public readonly int Kind = kind; // 0 done or stopped, 1 running, 2 failed, 3 a save
        public double Born = -1;         // a tick that popped in: since when
    }

    const double NameWidth = 96, LaneHeight = 18, LaneGap = 4;
    nint timelineArea, timelineNames, timelineEmpty;
    readonly List<Mark> marks = [];
    readonly List<string> lanes = [];
    readonly List<long> laneLabels = [];
    readonly HashSet<string> seenMarks = [];
    DateTime windowStart;
    bool timelineMoving, timelineFilled;
    Func<double, bool> timelineStep = null!;
    double timelineTime;
    string[] laneSaid = [];

    nint BuildTimeline()
    {
        timelineArea = Expand(N.gtk_drawing_area_new(), true, false);
        OnDrawArea(timelineArea, (_, cr, w, h) => PaintTimeline(cr, w));
        OnMotion(timelineArea, (x, y) => N.gtk_widget_set_tooltip_text(timelineArea, MarkAt(x, y) is { } m ? m.Title : null));
        OnClick(timelineArea, (x, y) => { if (MarkAt(x, y) is { } m) OpenMark(m); });
        // The names column: the ruler's row is empty, then a name per lane.
        var rulerRow = N.gtk_label_new("");
        N.gtk_widget_set_size_request(rulerRow, (int)NameWidth, (int)LaneHeight);
        Said(rulerRow, "The last three hours");
        timelineNames = Box(Vertical, (int)LaneGap, rulerRow);
        N.gtk_widget_set_size_request(timelineNames, (int)NameWidth, -1);
        timelineEmpty = Css(Label("Nothing ran in the last 3 hours. When a bee works, its task shows here as a lane, with what it saved to the hive.", "dim-label", "small"), "caption");
        timelineStep = TimelineStep;
        nint Key(uint color, string what, double w, double h)
        {
            var swatch = N.gtk_drawing_area_new();
            N.gtk_widget_set_size_request(swatch, (int)Math.Max(w, 1), 10);
            N.gtk_widget_set_valign(swatch, Center);
            OnDrawArea(swatch, (_, cr, sw, sh) => Ink.Fill(cr, (sw - w) / 2, (sh - h) / 2, w, h, color));
            return Box(Horizontal, 6, swatch, Css(Label(what), "tiny", "faint"));
        }
        var legend = Box(Horizontal, 20, Key(Palette.Done, "task done", 14, 8), Key(Palette.Pulse, "working", 14, 8),
            Key(Palette.Border, "failed", 14, 8), Key(Palette.Dim, "saved to the hive", 1, 10));
        N.gtk_widget_set_margin_start(legend, (int)NameWidth);
        N.gtk_widget_set_margin_top(legend, 12);
        return Box(Vertical, 0, Box(Horizontal, 0, timelineNames, timelineArea), timelineEmpty, legend);
    }

    void RefreshTimeline()
    {
        // Three hours, from the start of the hour two hours ago: ▼ now moves
        // along the last of them, and the window steps on each hour.
        var now = DateTime.Now;
        windowStart = now.Date.AddHours(now.Hour - 2);
        var since = windowStart;
        marks.Clear();
        for (int i = 0; i < allTasks.Count; i++)
        {
            var t = allTasks[i];
            var start = When(t.Doc.Get("started", t.Doc.Get("created")));
            var end = t.Running ? now : Finished(t);
            if (end < since) continue;
            var took = t.Doc.Get("took");
            marks.Add(new Mark(t.Bee, start, end, t.Running ? 1 : t.Status == "failed" ? 2 : 0,
                string.Concat(t.Bee, ": ", TitleOf(t), " (", t.Status, took.Length > 0 ? " in " + took : "", ")"),
                t.Doc.Get("run") is { Length: > 0 } run ? "run:" + run : "task:" + t.Id));
        }
        for (int i = 0; i < work.Count; i++)
            if (work[i].Task == null) marks.Add(new Mark(work[i].Bee, work[i].Started, now, 1, work[i].Bee + ": " + work[i].Title, ""));
        for (int i = 0; i < activityHits.Count; i++)
        {
            var h = activityHits[i];
            if (h.Bee.Length > 0 && When(h.Created) is var at && at >= since)
                marks.Add(new Mark(h.Bee, at, at, 3, string.Concat(h.Bee, " saved a ", h.Type, ": ", h.Title), h.Id));
        }
        // What's new since the last look pops in.
        var t0 = Clock;
        foreach (var m in marks)
            if (m.Kind == 3 && seenMarks.Add("save:" + m.Open) && timelineFilled) m.Born = t0;
        timelineFilled = true;
        lanes.Clear();
        foreach (var b in bees)
            if (marks.Exists(m => m.Lane.Equals(b.Name, StringComparison.OrdinalIgnoreCase))) lanes.Add(b.Name);
        // A name per lane, made as lanes appear.
        while (laneLabels.Count < lanes.Count)
        {
            var l = Css(Label(""), "timeline-name");
            N.gtk_label_set_wrap(l, false);
            N.gtk_label_set_ellipsize(l, 3);
            N.gtk_widget_set_size_request(l, (int)NameWidth - 8, (int)LaneHeight);
            N.gtk_box_append(timelineNames, l);
            laneLabels.Add(l);
        }
        if (laneSaid.Length < laneLabels.Count) Array.Resize(ref laneSaid, laneLabels.Count);
        for (int i = 0; i < laneLabels.Count; i++)
        {
            var label = (nint)laneLabels[i];
            N.gtk_widget_set_visible(label, i < lanes.Count);
            if (i >= lanes.Count) continue;
            N.gtk_label_set_text(label, lanes[i]);
            // What a screen reader hears: the lane as a sentence, when it changed.
            var said = new System.Text.StringBuilder(lanes[i]);
            foreach (var m in marks)
                if (m.Lane.Equals(lanes[i], StringComparison.OrdinalIgnoreCase))
                    said.Append(m.Kind == 3 ? "; " + m.Title[(m.Title.IndexOf(' ') + 1)..] : "; " + m.Title[(m.Title.IndexOf(": ") + 2)..]);
            if (said.ToString() != laneSaid[i]) N.gtk_accessible_update_property(label, 4, laneSaid[i] = said.ToString(), -1);
        }
        N.gtk_widget_set_visible(timelineEmpty, lanes.Count == 0);
        N.gtk_widget_set_visible(timelineArea, lanes.Count > 0);
        N.gtk_drawing_area_set_content_height(timelineArea, (int)(LaneHeight + lanes.Count * (LaneHeight + LaneGap)));
        if (timelineRange != 0) N.gtk_label_set_text(timelineRange, $"{windowStart:HH:mm} – now");
        DrawTimeline(t0);
        if (timelineMoving || !TimelineBusy(t0)) return;
        timelineMoving = true;
        if (Smooth) Animate(timelineStep);
    }

    bool TimelineBusy(double t) => work.Count > 0 || marks.Exists(m => m.Born >= 0 && t - m.Born < 0.4);

    bool TimelineStep(double t)
    {
        DrawTimeline(t);
        timelineMoving = TimelineBusy(t);
        if (!timelineMoving) DrawTimeline(t);
        return timelineMoving;
    }

    static DateTime Finished(BeeTask t) => When(t.Doc.Get("finished", t.Doc.Get("created")));

    int Lane(string bee) => lanes.FindIndex(l => l.Equals(bee, StringComparison.OrdinalIgnoreCase));

    // Where a moment is across the three hours, 0 to 1.
    double At(DateTime t) => Math.Clamp((t - windowStart).TotalSeconds / (3 * 3600), 0, 1);

    void DrawTimeline(double t)
    {
        timelineTime = t;
        if (timelineArea != 0) N.gtk_widget_queue_draw(timelineArea);
    }

    void PaintTimeline(nint cr, int w)
    {
        if (lanes.Count == 0) return;
        var t = timelineTime;
        var smooth = Smooth;
        var now = At(DateTime.Now);
        // The ruler: the hours, and ▼ at now.
        for (int h = 0; h < 3; h++)
            Ink.Text(cr, Math.Round(w * h / 3.0), 13, Ink.C(Palette.Dim, windowStart.AddHours(h).ToString("HH:mm")), 12);
        Ink.Text(cr, w * now, 13, Ink.C(Palette.Pulse, "▼"), 12, 0.5);
        for (int l = 0; l < lanes.Count; l++)
        {
            var top = LaneHeight + LaneGap + l * (LaneHeight + LaneGap);
            // A dot every 8 px along its middle; the hour lines; the now line.
            for (double x = 0.5; x < w; x += 8) Ink.Fill(cr, x, top + 8.5, 1, 1, Palette.Line);
            for (int h = 1; h < 3; h++) Ink.Fill(cr, Math.Round(w * h / 3.0), top, 1, LaneHeight, Palette.Rule);
            Ink.Fill(cr, Math.Round(w * now), top - 4, 1, LaneHeight + 8, Palette.Flight);
            foreach (var m in marks)
            {
                if (Lane(m.Lane) != l || m.Kind == 3) continue;
                double x1 = Math.Round(w * At(m.From)), x2 = Math.Max(x1 + 2, Math.Round(w * At(m.To)));
                if (m.Kind == 1)
                {
                    // Working: honey with a lighter band running along it.
                    Ink.Fill(cr, x1, top + 4, x2 - x1, 10, Palette.Run);
                    var band = smooth ? (t / 1.6 % 1) : 0.5;
                    var bx = x1 + (x2 - x1) * band;
                    for (int k = -6; k <= 6; k++)
                    {
                        var px = bx + k * 2;
                        if (px < x1 || px >= x2) continue;
                        Ink.Fill(cr, px, top + 4, 2, 10, Palette.Pulse, 1 - Math.Abs(k) / 7.0);
                    }
                }
                else Ink.Fill(cr, x1, top + 4, x2 - x1, 10, m.Kind == 2 ? Palette.Border : Palette.Done);
            }
            foreach (var m in marks)
            {
                if (Lane(m.Lane) != l || m.Kind != 3) continue;
                // A save: a 1 px tick, growing in when it's new.
                var grow = m.Born >= 0 && smooth ? Math.Clamp((t - m.Born) / 0.3, 0, 1) : 1;
                Ink.Fill(cr, Math.Round(w * At(m.From)), top + 3 + 12 * (1 - grow), 1, 12 * grow, Palette.Dim);
            }
        }
    }

    // The mark under (x, y): a save before a bar, the shortest bar first.
    Mark? MarkAt(double x, double y)
    {
        var lane = (int)Math.Floor((y - LaneHeight - LaneGap / 2) / (LaneHeight + LaneGap));
        var w = N.gtk_widget_get_width(timelineArea);
        if (lane < 0 || lane >= lanes.Count || w <= 0) return null;
        var at = x / w;
        var slack = 4.0 / w;
        Mark? best = null;
        var bestSpan = double.MaxValue;
        foreach (var m in marks)
        {
            if (Lane(m.Lane) != lane) continue;
            double from = At(m.From), to = Math.Max(from, At(m.To));
            if (at < from - slack || at > to + slack) continue;
            var span = m.Kind == 3 ? -1 : to - from;
            if (span < bestSpan) (best, bestSpan) = (m, span);
        }
        return best;
    }

    void OpenMark(Mark m)
    {
        if (m.Open.StartsWith("task:")) ShowTask(m.Open[5..]);
        else if (m.Open.StartsWith("run:"))
        {
            ShowRun(m.Open[4..]);
            Go("reviews");
            Reveal(reviewSplit);
        }
        else if (m.Open.Length > 0) OpenEntry(m.Open);
    }
}
