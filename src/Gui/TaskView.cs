using static Hive.Gui.Ui;

namespace Hive.Gui;

// The task view: everything about one task in a dialog that draws itself in.
// What was asked, the live step log (the newest 50 steps, new ones coming
// in while it works), the result, honey, Follow up (the same bee carries on
// in the same session), Hand to (another bee gets the result) and the
// command to resume it in a terminal. Stop lives here and in the popover.
sealed unsafe partial class App
{
    sealed class TaskView(string id, nint dialog, nint mark, nint state, nint log)
    {
        public readonly string Id = id;
        public readonly nint Dialog = dialog, Mark = mark, State = state, Log = log;
        public string Logged = "", Status = "";
        public bool Moving;
    }

    TaskView? taskView;
    Func<double, bool> taskStep = null!;

    void ShowTask(string id)
    {
        var t = Tasks.Load(store, id);
        var d = N.adw_dialog_new();
        N.adw_dialog_set_title(d, $"{t.Bee}: {t.Title}");
        N.adw_dialog_set_content_width(d, 780);
        N.adw_dialog_set_content_height(d, 700);
        var access = Tasks.AccessLabels[Math.Max(0, Array.IndexOf(Tasks.Access, t.Doc.Get("access")))];
        var mark = Css(ArtLabel(), "task-view-mark");
        var state = Css(N.gtk_label_new(""), "title-3");
        N.gtk_label_set_xalign(state, 0);
        var log = Css(ArtLabel(), "step-log");
        N.gtk_label_set_xalign(log, 0);
        N.gtk_label_set_yalign(log, 0);
        var view = taskView = new TaskView(t.Id, d, mark, state, log);
        var box = Box(Vertical, 12,
            Box(Horizontal, 10, mark, Expand(Box(Vertical, 2, state,
                Label($"{t.Doc.Get("folder")} · {access}" + (t.Doc.Get("follows") is { Length: > 0 } f ? $" · carries on from {f}" : ""), "dim-label")), true, false)),
            Panel("asked", Margin(Selectable(Label(t.Prompt)), 8)));
        // The steps, newest at the bottom; a screen reader hears the last one.
        var steps = Scroll(Margin(log, 6, 8, 6, 8));
        N.gtk_widget_set_size_request(steps, -1, 170);
        N.gtk_widget_set_vexpand(steps, false);
        N.gtk_box_append(box, Panel("steps", steps));
        var result = t.Running ? "Still working on it." : t.Result.Trim().Length > 0 ? t.Result.Trim() : "No result was recorded.";
        N.gtk_box_append(box, Panel("result", Framed(TextView(result, editable: false, mono: true))));

        // Honey, plainly: given (and taken back), or offered for finished work.
        if (t.Doc.Get("reward") is "yes" or "no")
        {
            var miss = !t.Rewarded;
            N.gtk_box_append(box, EndAligned(Label($"{(miss ? "A miss" : "Earned honey")}{GivenBy(t.Doc)} {Ago(t.Doc.Get("rewarded"))}" + (t.Doc.Get("reward_note") is { Length: > 0 } n ? $": \"{n}\"" : "."), "heading"),
                Button("Take it back", () =>
                {
                    var (said, by) = (t.Doc.Get("reward_note"), t.Doc.Get("reward_by", "user"));
                    N.adw_dialog_close(d);
                    TakeHoneyBack(give => Tasks.Reward(store, t.Id, said, give, by, miss), miss);
                }, "flat")));
        }
        else if (t.Status == "done")
        {
            var note = N.gtk_entry_new();
            N.gtk_entry_set_placeholder_text(note, "What made it great? (optional, it helps the bee learn)");
            N.gtk_box_append(box, Box(Horizontal, 8, Prompt(note), Button("Give honey", () =>
            {
                Tasks.Reward(store, t.Id, Text(note), true);
                Honeyed($"{t.Bee} earned honey");
                N.adw_dialog_close(d);
                Refresh();
            }, "suggested-action")));
        }

        // Follow up and Hand to, once it's done.
        if (!t.Running)
        {
            if (t.Doc.Get("session").Length > 0)
            {
                var next = N.gtk_entry_new();
                N.gtk_entry_set_placeholder_text(next, $"What should {t.Bee} do next? It carries on in the same session.");
                Action follow = () =>
                {
                    var task = Tasks.FollowUp(store, t, Text(next));
                    Tasks.Start(task);
                    N.adw_dialog_close(d);
                    Say($"{t.Bee} carries on where it left off.");
                    RefreshHive();
                    RefreshTasks();
                };
                On(next, "activate", follow);
                N.gtk_box_append(box, Panel("follow up", Margin(Box(Horizontal, 8, Prompt(next), Button("Follow up", follow, "suggested-action")), 8)));
            }
            var others = bees.Where(b => b.Name != t.Bee).ToList();
            if (others.Count > 0)
            {
                var (drop, model) = StringDropDown(others[0].Name);
                foreach (var b in others.Skip(1)) N.gtk_string_list_append(model, b.Name);
                var ask = N.gtk_entry_new();
                N.gtk_entry_set_placeholder_text(ask, "What should it do with the result?");
                Action hand = () =>
                {
                    var to = others[(int)N.gtk_drop_down_get_selected(drop)].Name;
                    var task = Tasks.HandTo(store, t, to, Text(ask));
                    Tasks.Start(task);
                    N.adw_dialog_close(d);
                    Say($"{to} takes it from here, with {t.Bee}'s result.");
                    RefreshHive();
                    RefreshTasks();
                };
                On(ask, "activate", hand);
                N.gtk_box_append(box, Panel("hand to", Margin(Box(Horizontal, 8, drop, Prompt(ask), Button("Hand to", hand)), 8)));
            }
        }
        if (t.Doc.Get("session") is { Length: > 0 } session)
        {
            // claude restores the session's agent itself; the folder may have spaces.
            var command = $"cd '{t.Doc.Get("folder").Replace("'", "'\\''")}' && claude --resume {session}";
            N.gtk_box_append(box, Panel("resume in a terminal", Margin(Box(Horizontal, 8, Expand(Selectable(Label(command, "monospace", "dim-label")), true, false),
                Button("Copy", () => { CopyText(command); Say("Copied the command."); }, "flat")), 8)));
        }
        var close = Button("Close", () => N.adw_dialog_close(d));
        var buttons = EndAligned(close);
        if (t.Running)
            N.gtk_box_prepend(buttons, Button("Stop", () =>
            {
                Tasks.Stop(store, t.Id);
                N.adw_dialog_close(d);
                Say("Stopped.");
                RefreshHive(); // first: the bee page's line reads the task as stopped from it
                RefreshTasks();
            }, "destructive-action"));
        N.gtk_box_append(box, buttons);
        var panel = Panel($"task {t.Id}", Scroll(Margin(box, 14)));
        N.adw_dialog_set_child(d, panel);
        On(d, "closed", () => { if (taskView == view) taskView = null; });
        UpdateTaskView(t);
        N.adw_dialog_present(d, window);
        N.gtk_widget_grab_focus(close); // not the asked text, which would show all selected
        Fx.DrawIn(panel);
        taskStep ??= TaskStep;
        StartTaskView();
    }

    static nint Selectable(nint label)
    {
        N.gtk_label_set_selectable(label, true);
        return label;
    }

    void CopyText(string text) => N.gdk_clipboard_set_text(N.gtk_widget_get_clipboard(window), text);

    // The task's state and its step log, from the files (on a refresh and as steps come in).
    void UpdateTaskView(BeeTask? t = null)
    {
        if (taskView is not { } view) return;
        t ??= FindTask(view.Id) ?? (File.Exists(Path.Combine(store.Root, ".tasks", view.Id + ".md")) ? Tasks.Load(store, view.Id) : null);
        if (t == null) return;
        if (t.Status != view.Status)
        {
            view.Status = t.Status;
            N.gtk_label_set_text(view.Mark, t.Status is "running" or "queued" ? "⠋" : StatusMark(t.Status));
            N.gtk_label_set_text(view.State, $"{t.Bee} · {t.Status}" + (t.Doc.Get("took") is { Length: > 0 } took ? $" in {took}" : ""));
        }
        var steps = work.Find(w => w.Task?.Id == view.Id)?.Log?.Last ?? ReadStepLog(t);
        var log = StepLogMarkup(steps);
        if (log == view.Logged) return;
        view.Logged = log;
        N.gtk_label_set_markup(view.Log, log.Length > 0 ? log : $"<span foreground=\"#{Palette.Dim:X6}\">No steps recorded.</span>");
        if (steps.Count > 0) Said(view.Log, "Last step: " + steps[^1].Say);
        StartTaskView();
    }

    // The newest 50 steps of a task that isn't being followed live.
    static List<Steps.Step> ReadStepLog(BeeTask t)
    {
        var steps = new List<Steps.Step>();
        try
        {
            if (File.Exists(t.StepsPath))
                foreach (var line in File.ReadLines(t.StepsPath))
                    if (Steps.Parse(line) is { } step)
                    {
                        steps.Add(step);
                        if (steps.Count > 50) steps.RemoveAt(0);
                    }
        }
        catch (IOException) { }
        return steps;
    }

    // "16:04:21  ✿  editing src/A.cs", and "↳ error" in red under a step that failed.
    static string StepLogMarkup(List<Steps.Step> steps)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var s in steps)
        {
            if (sb.Length > 0) sb.Append('\n');
            var time = DateTimeOffset.FromUnixTimeMilliseconds(s.At).ToLocalTime().ToString("HH:mm:ss");
            sb.Append($"<span foreground=\"#{Palette.Dim:X6}\">{time}</span>  ");
            if (s.IsResult)
            {
                sb.Append(s.What == "error" ? "<span foreground=\"#E5534B\">   ↳ error</span>" : $"<span foreground=\"#{Palette.Border:X6}\">   ↳ ok</span>");
                continue;
            }
            var icon = s.Station switch
            {
                Steps.Flowers => "✿", Steps.Comb => "⬡", Steps.Library => "≡", Steps.Field => Icons.Search,
                Steps.Hive => "⬢", Steps.Thinking => "…", _ => "⚙",
            };
            sb.Append($"<span foreground=\"#{Palette.Honey:X6}\">{Markup(icon)}</span>  {Markup(s.Say)}");
        }
        return sb.ToString();
    }

    void StartTaskView()
    {
        if (taskView is not { Moving: false } view || !Smooth || view.Status is not ("running" or "queued" or "waiting")) return;
        view.Moving = true;
        Animate(taskStep);
    }

    // The status mark: a spinner while it works, ⧗ while it waits, then ✓ ✗ ■.
    bool TaskStep(double t)
    {
        if (taskView is not { } view) return false;
        var running = view.Status is "running" or "queued";
        var mark = running ? "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏"[(int)(Frame(t) % 10)].ToString() : view.Status == "waiting" ? ((long)t % 2 == 1 ? "⧖" : "⧗") : StatusMark(view.Status);
        N.gtk_label_set_text(view.Mark, mark);
        view.Moving = view.Status is "running" or "queued" or "waiting" && N.gtk_widget_get_mapped(view.Dialog);
        if (!view.Moving) N.gtk_label_set_text(view.Mark, StatusMark(view.Status));
        return view.Moving;
    }

    static string StatusMark(string status) => status switch { "done" => "✓", "failed" => "✗", "stopped" => "■", "waiting" => "⧗", _ => "⠿" };
}
