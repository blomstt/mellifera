using static Hive.Gui.Ui;

namespace Hive.Gui;

// Claude's usage limits at the right of the status line, as the mockups
// have them: "session ▰▰▰▰▱▱▱▱▱▱ 42% ↻ 20:00 │ week ▰▰▱▱▱▱▱▱▱▱ 23% ↻ Thu";
// a click opens the popover with both windows as 40 cells against the 80%
// mark, when each resets, and "warn me at 80%". The numbers come only from
// `hive statusline` (src/Usage.cs); without them it says so.
sealed unsafe partial class App
{
    nint usageButton, usageLabel, usagePopover;
    Usage.Limits? usage;
    long usageWarnedSession, usageWarnedWeek; // the windows already warned about (by when they reset)
    bool usageChanged;

    string UsageFile => Usage.FilePath(store);
    string UsageWarnPath => Path.Combine(store.Root, ".usage-warn"); // "off": no warning
    bool UsageWarn => !File.Exists(UsageWarnPath) || File.ReadAllText(UsageWarnPath).Trim() != "off";

    void BuildUsage()
    {
        usageLabel = Css(N.gtk_label_new(""), "small");
        usageButton = Css(N.gtk_button_new(), "usage");
        N.gtk_button_set_child(usageButton, usageLabel);
        On(usageButton, "clicked", ShowUsage);
        N.gtk_box_append(status.Right, usageButton);
        RefreshUsage();
    }

    // Ten cells of a meter, the used ones honey.
    static string Bar(double used, int cells)
    {
        var on = (int)Math.Round(Math.Clamp(used, 0, 100) * cells / 100);
        return Palette.Span(Palette.Pulse, new string('▰', on)) + Palette.Span(Palette.Line, new string('▰', cells - on));
    }

    void RefreshUsage()
    {
        if (usageLabel == 0) return;
        usage = Usage.Load(store);
        if (usage == null)
        {
            N.gtk_label_set_markup(usageLabel, Palette.Span(Palette.Faint, "no usage data yet"));
            Said(usageButton, "Claude usage: no usage data yet");
            return;
        }
        string Part(string name, Usage.Window? w) =>
            w == null ? "" : w.Over
                ? Palette.Span(Palette.Dim, name + " ") + Palette.Span(Palette.Faint, "reset")
                : Palette.Span(Palette.Dim, name + " ") + Bar(w.Used, 10) + " " + Palette.Span(Palette.Bright, $"{w.Used:0}%") + " " + Palette.Span(Palette.Faint, "↻ " + Usage.When(w.ResetsAt));
        var parts = new List<string>();
        if (Part("session", usage.Session) is { Length: > 0 } s) parts.Add(s);
        if (Part("week", usage.Week) is { Length: > 0 } wk) parts.Add(wk);
        N.gtk_label_set_markup(usageLabel, string.Join(Palette.Span(Palette.Border, "  │  "), parts));
        string Said_(string name, Usage.Window? w) => w == null ? "" : w.Over ? $"{name} reset" : $"{name} {w.Used:0} percent, resets {Usage.When(w.ResetsAt, hour: true)}";
        Said(usageButton, "Claude usage: " + string.Join("; ", new[] { Said_("session", usage.Session), Said_("week", usage.Week) }.Where(x => x.Length > 0)));
        // At 80% of a window the status line says so, once per window.
        if (!UsageWarn) return;
        if (usage.Session is { Over: false, Used: >= 80 } ss && ss.Resets != usageWarnedSession)
        {
            usageWarnedSession = ss.Resets;
            status.Say($"Claude usage: the session is at {ss.Used:0}% of its limit; it resets at {Usage.When(ss.ResetsAt, hour: true)}.");
        }
        else if (usage.Week is { Over: false, Used: >= 80 } ww && ww.Resets != usageWarnedWeek)
        {
            usageWarnedWeek = ww.Resets;
            status.Say($"Claude usage: the week is at {ww.Used:0}% of its limit; it resets {Usage.When(ww.ResetsAt, hour: true)}.");
        }
    }

    // The popover: both windows as 40 cells, the 80% mark, when they reset.
    void ShowUsage()
    {
        if (usagePopover == 0)
        {
            usagePopover = N.gtk_popover_new();
            N.gtk_popover_set_has_arrow(usagePopover, false);
            N.gtk_widget_set_parent(usagePopover, usageButton);
        }
        usage = Usage.Load(store);
        var box = Box(Vertical, 0);
        var opened = Clock;
        nint Window(string name, string span, Usage.Window? w, bool first)
        {
            var head = Box(Horizontal, 10, Css(N.gtk_label_new(name), "bright"), Css(N.gtk_label_new(span), "tiny", "faint"), Expand(N.gtk_box_new(Horizontal, 0), true, false),
                Css(N.gtk_label_new(w == null ? "—" : w.Over ? "reset" : $"{w.Used:0}%"), "usage-percent"));
            var cells = N.gtk_drawing_area_new();
            N.gtk_drawing_area_set_content_height(cells, 16);
            N.gtk_drawing_area_set_content_width(cells, 398);
            var used = w == null || w.Over ? 0 : w.Used;
            OnDrawArea(cells, (_, cr, width, h) =>
            {
                // Filling cell by cell as it opens; the 80% cell marked underneath.
                var on = (int)Math.Round(used * 40 / 100 * Math.Clamp((drawnUsage - opened) / 0.6, 0, 1));
                for (int i = 0; i < 40; i++)
                {
                    Ink.Fill(cr, i * 10, 0, 8, 16, i < on ? Palette.Pulse : i == 31 ? Palette.Border : Palette.Rule);
                    if (i == 31) Ink.Fill(cr, i * 10, 13, 8, 3, Palette.Flight);
                }
            });
            usageCells.Add(cells);
            var resets = Css(N.gtk_label_new(""), "small", "dim-label");
            N.gtk_label_set_xalign(resets, 0);
            if (w != null)
                N.gtk_label_set_markup(resets, w.Over ? $"reset at {Usage.When(w.ResetsAt, hour: true)}; the next number comes with Claude's next answer"
                    : (w.ResetsAt.Date == DateTime.Today ? "resets at " : "resets ") + Palette.Span(Palette.Text, Usage.When(w.ResetsAt, hour: true)) + $" · in {Usage.Until(w.ResetsAt)}");
            else N.gtk_label_set_text(resets, "no number for it yet");
            return Margin(Box(Vertical, 6, head, cells, resets), first ? 0 : 22, 0, 0, 0);
        }
        usageCells.Clear();
        if (usage == null)
        {
            var none = Css(Label("No usage data yet. Claude Code hands it to `hive statusline` after a session's first answer, for claude.ai Pro and Max plans; `hive install` sets that status line if you have none."), "small", "dim-label");
            N.gtk_label_set_max_width_chars(none, 52); // wraps at the popover's width instead of widening it
            N.gtk_box_append(box, none);
        }
        else
        {
            N.gtk_box_append(box, Window("Session", "5-hour window", usage.Session, true));
            N.gtk_box_append(box, Window("Week", "7-day window", usage.Week, false));
        }
        var more = Css(Box(Vertical, 6), "usage-more");
        var busy = work.Where(w => !w.Waiting).Select(w => w.Bee).Distinct().ToList();
        if (busy.Count > 0)
        {
            var who = busy.Count == 1 ? busy[0] + " is" : string.Join(", ", busy.Take(busy.Count - 1)) + " and " + busy[^1] + " are";
            var line = Css(N.gtk_label_new(""), "small");
            N.gtk_label_set_markup(line, Palette.Span(Palette.Pulse, "⠹ ") + Markup($"{who} working now; they count against the same limits."));
            N.gtk_label_set_xalign(line, 0);
            N.gtk_label_set_wrap(line, true);
            N.gtk_box_append(more, line);
        }
        var warn = Css(N.gtk_check_button_new_with_label("warn me in the status line at 80%"), "small");
        N.gtk_check_button_set_active(warn, UsageWarn);
        On(warn, "notify::active", () => File.WriteAllText(UsageWarnPath, N.gtk_check_button_get_active(warn) ? "on\n" : "off\n"));
        N.gtk_box_append(more, warn);
        N.gtk_box_append(box, Margin(more, 22, 0, 0, 0));
        N.gtk_box_append(box, Margin(Css(Label("From Claude Code's status line (rate_limits); claude.ai plans only."), "tiny", "faint"), 18, 0, 0, 0));
        N.gtk_widget_set_size_request(box, 400, -1);
        var panel = Panel("claude usage", Margin(Clamp(box, 400), 14, 20, 14, 20), "lit"); // and no wider: its lines wrap instead
        if (usage != null && usage.Updated > DateTime.MinValue) PanelRight(panel, "updated " + Usage.When(usage.Updated, hour: true));
        N.gtk_popover_set_child(usagePopover, panel);
        N.gtk_popover_popup(usagePopover);
        Fx.DrawIn(panel);
        // The cells fill as it opens.
        drawnUsage = opened;
        if (!Smooth) { drawnUsage = opened + 1; return; }
        var areas = new nint[usageCells.Count];
        for (int i = 0; i < areas.Length; i++) areas[i] = (nint)usageCells[i];
        Animate(t =>
        {
            drawnUsage = t;
            foreach (var a in areas) N.gtk_widget_queue_draw(a);
            return t - opened < 0.65;
        }, areas);
    }

    double drawnUsage;
    readonly List<long> usageCells = [];
}
