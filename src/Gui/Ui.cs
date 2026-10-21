using System.Runtime.InteropServices;

namespace Hive.Gui;

// Signals and small widget helpers. A signal handler is a C# closure kept
// under a slot number; GTK gets a static unmanaged trampoline and the slot as
// its data, and frees the slot (Release) when the handler goes with its
// widget, so rebuilt rows and pages don't pile closures up.
static unsafe class Ui
{
    public static Action<string>? OnError;
    // What changed in a watched folder: its path and the GFileMonitorEvent.
    public static Action<string, int>? OnFileChanged;

    static readonly Dictionary<nint, Delegate> handlers = [];
    static nint lastSlot;
    static readonly List<Action<nint, int, int>> draws = [];
    static readonly List<Action> timers = [];

    // clicked, activate, apply, search-changed, changed, notify::...
    public static void On(nint obj, string signal, Action handler)
    {
        var fn = signal.StartsWith("notify::")
            ? (nint)(delegate* unmanaged<nint, nint, nint, void>)&Notify
            : (nint)(delegate* unmanaged<nint, nint, void>)&Plain;
        Connect(obj, signal, fn, handler);
    }

    // Connects fn with handler's slot as its data; GTK calls Release when
    // the handler is disconnected (its object is finalized).
    static void Connect(nint obj, string signal, nint fn, Delegate handler)
    {
        handlers[++lastSlot] = handler;
        N.g_signal_connect_data(obj, signal, fn, lastSlot, (nint)(delegate* unmanaged<nint, nint, void>)&Release, 0);
    }

    [UnmanagedCallersOnly] static void Release(nint data, nint closure) => handlers.Remove(data);

    static T Handler<T>(nint data) where T : Delegate => (T)handlers[data];

    // row-activated (the row), response (the response id, as a C string)
    public static void OnArg(nint obj, string signal, Action<nint> handler)
    {
        Connect(obj, signal, (nint)(delegate* unmanaged<nint, nint, nint, void>)&WithArg, handler);
    }

    public static void OnClick(nint widget, Action<double, double> handler)
    {
        var gesture = N.gtk_gesture_click_new();
        Connect(gesture, "pressed", (nint)(delegate* unmanaged<nint, int, double, double, nint, void>)&Pressed, handler);
        N.gtk_widget_add_controller(widget, gesture);
    }

    // A key pressed in a widget, before the widget itself handles it (so an
    // entry's Tab can mean something else): (keyval, modifier state) → handled.
    public static void OnKey(nint widget, Func<uint, uint, bool> handler)
    {
        var controller = N.gtk_event_controller_key_new();
        N.gtk_event_controller_set_propagation_phase(controller, 1); // GTK_PHASE_CAPTURE
        Connect(controller, "key-pressed", (nint)(delegate* unmanaged<nint, uint, uint, uint, nint, int>)&Key, handler);
        N.gtk_widget_add_controller(widget, controller);
    }

    // A key let go in a widget: (keyval, modifier state).
    public static void OnKeyUp(nint widget, Action<uint, uint> handler)
    {
        var controller = N.gtk_event_controller_key_new();
        N.gtk_event_controller_set_propagation_phase(controller, 1); // GTK_PHASE_CAPTURE
        Connect(controller, "key-released", (nint)(delegate* unmanaged<nint, uint, uint, uint, nint, void>)&KeyUp, handler);
        N.gtk_widget_add_controller(widget, controller);
    }

    [UnmanagedCallersOnly]
    static void KeyUp(nint self, uint keyval, uint keycode, uint state, nint data) => Guard(() => Handler<Action<uint, uint>>(data)(keyval, state));

    [UnmanagedCallersOnly]
    static int Key(nint self, uint keyval, uint keycode, uint state, nint data)
    {
        var handled = false;
        Guard(() => handled = Handler<Func<uint, uint, bool>>(data)(keyval, state));
        return handled ? 1 : 0;
    }

    public static void OnMotion(nint widget, Action<double, double> handler)
    {
        var motion = N.gtk_event_controller_motion_new();
        Connect(motion, "motion", (nint)(delegate* unmanaged<nint, double, double, nint, void>)&Moved, handler);
        N.gtk_widget_add_controller(widget, motion);
    }

    // Pass the same delegate for many areas (a field), and it's kept once.
    public static void OnDraw(nint area, Action<nint, int, int> handler)
    {
        N.gtk_drawing_area_set_draw_func(area, (nint)(delegate* unmanaged<nint, nint, int, int, nint, void>)&Draw, Keep(draws, handler), 0);
    }

    // A draw function that also gets the area (one shared delegate for many areas).
    public static void OnDrawArea(nint area, Action<nint, nint, int, int> handler) =>
        N.gtk_drawing_area_set_draw_func(area, (nint)(delegate* unmanaged<nint, nint, int, int, nint, void>)&DrawArea, Keep(areaDraws, handler), 0);

    static readonly List<Action<nint, nint, int, int>> areaDraws = [];

    // A drawing area's new size: (area, width, height).
    public static void OnResize(nint area, Action<nint, int, int> handler) =>
        N.g_signal_connect_data(area, "resize", (nint)(delegate* unmanaged<nint, int, int, nint, void>)&Resized, Keep(draws, handler), 0, 0);

    static int Keep<T>(List<T> list, T item)
    {
        var i = list.IndexOf(item);
        if (i >= 0) return i;
        list.Add(item);
        return list.Count - 1;
    }

    // Runs action once, ms from now. Pass the same delegate each time (a
    // field), and it's kept once, not once per call.
    public static void After(uint ms, Action action)
    {
        N.g_timeout_add(ms, (nint)(delegate* unmanaged<nint, int>)&Tick, Keep(timers, action));
    }

    // Watches one folder, not its subfolders (a GFileMonitor): what changes
    // in it goes to OnFileChanged. 0 if it can't.
    public static nint Watch(string dir)
    {
        var file = N.g_file_new_for_path(dir);
        var monitor = N.g_file_monitor_directory(file, 0, 0, 0);
        N.g_object_unref(file);
        if (monitor == 0) return 0;
        N.g_signal_connect_data(monitor, "changed", (nint)(delegate* unmanaged<nint, nint, nint, int, nint, void>)&FileChanged, 0, 0, 0);
        return monitor;
    }

    public static void Unwatch(nint monitor)
    {
        N.g_file_monitor_cancel(monitor);
        N.g_object_unref(monitor);
    }

    [UnmanagedCallersOnly] static void Plain(nint self, nint data) => Guard(() => Handler<Action>(data)());
    [UnmanagedCallersOnly] static void Notify(nint self, nint pspec, nint data) => Guard(() => Handler<Action>(data)());
    [UnmanagedCallersOnly] static void WithArg(nint self, nint arg, nint data) => Guard(() => Handler<Action<nint>>(data)(arg));
    [UnmanagedCallersOnly] static void Pressed(nint self, int n, double x, double y, nint data) => Guard(() => Handler<Action<double, double>>(data)(x, y));
    [UnmanagedCallersOnly] static void Moved(nint self, double x, double y, nint data) => Guard(() => Handler<Action<double, double>>(data)(x, y));
    [UnmanagedCallersOnly] static void Draw(nint area, nint cr, int w, int h, nint data) => Guard(() => draws[(int)data](cr, w, h));
    [UnmanagedCallersOnly] static void Resized(nint area, int w, int h, nint data) => Guard(() => draws[(int)data](area, w, h));
    [UnmanagedCallersOnly] static void DrawArea(nint area, nint cr, int w, int h, nint data) => Guard(() => areaDraws[(int)data](area, cr, w, h));

    [UnmanagedCallersOnly]
    static void FileChanged(nint monitor, nint file, nint other, int kind, nint data) => Guard(() =>
    {
        var p = N.g_file_get_path(file);
        var path = Str(p);
        N.g_free(p);
        OnFileChanged?.Invoke(path, kind);
    });

    [UnmanagedCallersOnly]
    static int Tick(nint data)
    {
        Guard(timers[(int)data]);
        return 0; // G_SOURCE_REMOVE: once
    }

    // ---- motion: GTK's frame clock --------------------------------------------

    // Everything that moves runs from one tick callback on the window, in
    // step with the monitor, and only while something moves. A step gets the
    // frame's time in seconds and returns false when it's done; with no step
    // left, the tick callback is removed, so a still window costs nothing.
    public static nint MotionHost;
    static readonly List<Func<double, bool>> steps = [];
    static uint tick;

    public static bool Ticking => tick != 0;
    public static int Moving => steps.Count;

    // Now, on the frame clock's scale (seconds).
    public static double Clock => N.g_get_monotonic_time() / 1e6;

    // Animations on (gtk-enable-animations). Off: things jump instead of
    // flying, and nothing moves just to look alive.
    public static bool Smooth => MotionHost != 0 && N.adw_get_enable_animations(MotionHost);

    // A step that calls GTK on widgets passes them as keep: they're held (a
    // ref) until it's done. A page or list rebuilt under a running step frees
    // its widgets, and GTK called on a freed widget crashes the window; a held
    // one is only taken off screen, so the step finishes on it harmlessly.
    public static void Animate(Func<double, bool> step, params nint[] keep)
    {
        if (keep.Length > 0)
        {
            foreach (var w in keep) N.g_object_ref(w);
            var inner = step;
            step = t =>
            {
                var again = false;
                try { return again = inner(t); }
                finally { if (!again) foreach (var w in keep) N.g_object_unref(w); }
            };
        }
        steps.Add(step);
        if (tick == 0 && MotionHost != 0)
            tick = N.gtk_widget_add_tick_callback(MotionHost, (nint)(delegate* unmanaged<nint, nint, nint, int>)&Ticked, 0, 0);
    }

    [UnmanagedCallersOnly]
    static int Ticked(nint widget, nint clock, nint data)
    {
        var now = N.gdk_frame_clock_get_frame_time(clock) / 1e6;
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var again = false;
            Guard(() => again = step(now));
            if (again) continue;
            steps.RemoveAt(i--);
        }
        if (steps.Count > 0) return 1; // G_SOURCE_CONTINUE
        tick = 0;
        return 0;
    }

    // Character art steps at 12.5 frames a second, from the same clock.
    public static long Frame(double t) => (long)Math.Floor(t * 12.5);

    // Types text into a label, stepped like a terminal, with cursor at its
    // end while it types. A newer text for the same label stops the old one.
    static readonly Dictionary<long, int> typing = [];

    public static void TypeIn(nint label, string text, double perSecond = 90, string cursor = "▌")
    {
        var gen = typing[label] = typing.GetValueOrDefault(label) + 1;
        if (!Smooth || text.Length < 2 || double.IsInfinity(perSecond))
        {
            N.gtk_label_set_text(label, text);
            return;
        }
        var start = Clock;
        var shown = -1;
        N.gtk_label_set_text(label, cursor);
        Animate(t =>
        {
            if (typing[label] != gen) return false;
            var n = (int)((Frame(t) - Frame(start)) / 12.5 * perSecond);
            if (n >= text.Length)
            {
                N.gtk_label_set_text(label, text);
                typing.Remove(label);
                return false;
            }
            if (n > 0 && char.IsHighSurrogate(text[n - 1])) n--;
            if (n != shown) N.gtk_label_set_text(label, text[..(shown = n)] + cursor);
            return true;
        }, label);
    }

    // A small shake, as an error does: side to side for 0.4 s.
    public static void Shake(nint w)
    {
        if (!Smooth) return;
        var start = Clock;
        var start0 = N.gtk_widget_get_margin_start(w);
        var end0 = N.gtk_widget_get_margin_end(w);
        Animate(t =>
        {
            var p = (t - start) / 0.4;
            var dx = p >= 1 ? 0 : (int)Math.Round(5 * Math.Sin(p * Math.PI * 6) * (1 - p));
            N.gtk_widget_set_margin_start(w, Math.Max(0, start0 + dx));
            N.gtk_widget_set_margin_end(w, Math.Max(0, end0 - dx));
            return p < 1;
        }, w);
    }

    // An exception must never unwind into GTK.
    static void Guard(Action action)
    {
        try { action(); }
        catch (HiveException e) { OnError?.Invoke(e.Message); }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            OnError?.Invoke(e.Message);
        }
    }

    // ---- widgets -----------------------------------------------------------

    public const int Horizontal = 0, Vertical = 1;
    public const int Fill = 0, Start = 1, End = 2, Center = 3;

    public static string Str(nint p) => p == 0 ? "" : Marshal.PtrToStringUTF8(p) ?? "";

    public static nint Css(nint w, params string[] classes)
    {
        foreach (var c in classes) if (c.Length > 0) N.gtk_widget_add_css_class(w, c);
        return w;
    }

    public static nint Halign(nint w, int align)
    {
        N.gtk_widget_set_halign(w, align);
        return w;
    }

    public static nint Margin(nint w, int all) => Margin(w, all, all, all, all);

    public static nint Margin(nint w, int top, int end, int bottom, int start)
    {
        N.gtk_widget_set_margin_top(w, top);
        N.gtk_widget_set_margin_end(w, end);
        N.gtk_widget_set_margin_bottom(w, bottom);
        N.gtk_widget_set_margin_start(w, start);
        return w;
    }

    public static nint Expand(nint w, bool h = true, bool v = true)
    {
        N.gtk_widget_set_hexpand(w, h);
        N.gtk_widget_set_vexpand(w, v);
        return w;
    }

    public static nint Box(int orientation, int spacing, params nint[] children)
    {
        var box = N.gtk_box_new(orientation, spacing);
        foreach (var c in children) N.gtk_box_append(box, c);
        return box;
    }

    public static nint Label(string text, params string[] css)
    {
        var l = N.gtk_label_new(text);
        N.gtk_label_set_xalign(l, 0);
        N.gtk_label_set_wrap(l, true);
        return Css(l, css);
    }

    // A button: its label in a 1 px box that turns honey under the pointer
    // ("suggested-action": the main action, filled honey).
    public static nint Button(string label, Action clicked, params string[] css) => Said(Css(TextButton(label, clicked, css), "btn"), label);

    // What a screen reader says for a widget, in place of its text.
    public static nint Said(nint w, string text)
    {
        N.gtk_accessible_update_property(w, 4, text, -1); // GTK_ACCESSIBLE_PROPERTY_LABEL
        return w;
    }

    // A label of character art, a spinner, braille or box lines: never heard
    // by a screen reader. Its accessible role is "none", which GTK leaves out
    // of what it presents (the hidden state won't do: GTK sets it again from
    // visibility whenever a page or a parent is shown).
    public static nint ArtLabel(string text = "")
    {
        var l = N.g_object_new(N.gtk_label_get_type(), "accessible-role", RoleNone, 0);
        N.gtk_label_set_text(l, text);
        return l;
    }

    static int roleNone = -1;

    public static int RoleNone
    {
        get
        {
            if (roleNone < 0) roleNone = *(int*)N.g_enum_get_value_by_nick(N.g_type_class_ref(N.gtk_accessible_role_get_type()), "none");
            return roleNone;
        }
    }

    // A button that is just its text (tabs, ‹ back).
    public static nint TextButton(string text, Action clicked, params string[] css)
    {
        var b = N.gtk_button_new_with_label(text);
        On(b, "clicked", clicked);
        return Css(b, css);
    }

    // A panel: a frame with its title in the top border, ┤ like this ├, and
    // a layer over it where a spark runs around the border when it changes.
    public static nint Panel(string title, nint child, params string[] css) => PanelWith(PanelTitle(title), child, css, title);

    public static nint PanelWith(nint title, nint child, string[] css, string titled = "")
    {
        var frame = Css(Css(N.gtk_frame_new(null), "panel"), css);
        N.gtk_frame_set_label_widget(frame, title);
        N.gtk_frame_set_child(frame, child);
        if (titled.Length > 0) Said(frame, titled); // heard as its title, not ┤ title ├
        return Fx.Layer(frame, titled);
    }

    public static nint PanelWith(nint title, nint child) => PanelWith(title, child, []);

    // A label (or a button, a count) at the right of a panel's top border,
    // on black like its title: "2 at work · 9 resting", "16:00 – now".
    public static nint PanelRight(nint panel, nint label)
    {
        var box = Css(Box(Horizontal, 8, label), "panel-right");
        N.gtk_widget_set_halign(box, End);
        N.gtk_widget_set_valign(box, Start);
        N.gtk_overlay_add_overlay(panel, box);
        return label;
    }

    // A panel's right-hand label as plain text. Change it with SetPanelRight,
    // so an empty one leaves no black notch in the border.
    public static nint PanelRight(nint panel, string text)
    {
        var label = PanelRight(panel, N.gtk_label_new(""));
        SetPanelRight(label, text);
        return label;
    }

    public static void SetPanelRight(nint label, string text)
    {
        N.gtk_label_set_text(label, text);
        N.gtk_widget_set_visible(N.gtk_widget_get_parent(label), text.Length > 0);
    }

    // The panel's title changes; it types in.
    public static void SetPanelTitle(nint panel, string title) => Fx.Retitle(panel, title);

    public static nint PanelTitle(string title)
    {
        var l = ArtLabel();
        N.gtk_label_set_markup(l, TitleMarkup(title));
        return Css(l, "panel-title");
    }

    public static string TitleMarkup(string title) =>
        $"<span foreground=\"#{Palette.Ghost:X6}\">┤</span> {Markup(title)} <span foreground=\"#{Palette.Ghost:X6}\">├</span>";

    public static string Markup(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // An entry behind a › prompt.
    // An entry behind a › prompt, in one bordered box (the entry's own border goes).
    public static nint Prompt(nint entry, string prompt = "›")
    {
        N.gtk_widget_set_hexpand(entry, true);
        return Css(Box(Horizontal, 0, Css(ArtLabel(prompt), "prompt", "entry-prompt"), Css(entry, "bare")), "field");
    }

    public static nint Scroll(nint child, bool horizontal = false)
    {
        var sw = N.gtk_scrolled_window_new();
        N.gtk_scrolled_window_set_policy(sw, horizontal ? 1 : 2, 1);
        N.gtk_scrolled_window_set_child(sw, child);
        return Expand(sw);
    }

    public static nint List(params string[] css)
    {
        var lb = N.gtk_list_box_new();
        N.gtk_list_box_set_selection_mode(lb, 0);
        return Css(lb, css.Length > 0 ? css : ["boxed-list"]);
    }

    // Fills a list with a row per key, unless it already shows exactly these
    // (shown holds what it shows). A key is the row's id, a line break, then
    // what the row says. A rebuild keeps the keyboard focus on the same id and
    // the scroll where it was (or goes to the top, for a new search).
    public static void SetRows(nint list, ref List<string>? shown, List<string> keys, Func<int, nint> row, bool keepScroll = true)
    {
        static string Id(string key) => key[..key.IndexOf('\n')];
        var old = shown;
        if (old != null && old.SequenceEqual(keys)) return;
        var focus = N.gtk_widget_get_focus_child(list);
        var at = focus != 0 && old != null ? N.gtk_list_box_row_get_index(focus) : -1;
        var focused = at >= 0 && at < old!.Count ? Id(old[at]) : null;
        var sw = N.gtk_widget_get_ancestor(list, N.gtk_scrolled_window_get_type());
        var adjustment = sw != 0 ? N.gtk_scrolled_window_get_vadjustment(sw) : 0;
        var top = adjustment != 0 && keepScroll ? N.gtk_adjustment_get_value(adjustment) : 0;
        N.gtk_list_box_remove_all(list);
        for (int i = 0; i < keys.Count; i++)
        {
            var r = row(i);
            N.gtk_list_box_append(list, r);
            if (focused != null && Id(keys[i]) == focused) N.gtk_widget_grab_focus(r);
        }
        if (adjustment != 0) N.gtk_adjustment_set_value(adjustment, top);
        shown = keys;
    }

    public static nint Row(string title, string subtitle, int subtitleLines = 2)
    {
        var row = N.adw_action_row_new();
        N.adw_preferences_row_set_use_markup(row, false);
        N.adw_preferences_row_set_title(row, title);
        if (subtitle.Length > 0) N.adw_action_row_set_subtitle(row, subtitle);
        N.adw_action_row_set_subtitle_lines(row, subtitleLines);
        return row;
    }

    public static nint Pill(string text)
    {
        var l = Css(N.gtk_label_new(text), "caption", "dim-label");
        N.gtk_label_set_wrap(l, true);
        N.gtk_label_set_max_width_chars(l, 18);
        N.gtk_label_set_justify(l, 1); // right
        N.gtk_label_set_xalign(l, 1);
        return l;
    }

    public static nint EntryRow(string title, string text, Action? apply = null)
    {
        var row = N.adw_entry_row_new();
        N.adw_entry_row_add_prefix(row, Css(ArtLabel("›"), "prompt"));
        N.adw_preferences_row_set_use_markup(row, false);
        N.adw_preferences_row_set_title(row, title);
        N.gtk_editable_set_text(row, text);
        if (apply != null)
        {
            N.adw_entry_row_set_show_apply_button(row, true);
            On(row, "apply", apply);
        }
        return row;
    }

    public static string Text(nint editable) => Str(N.gtk_editable_get_text(editable));

    public static nint SwitchRow(string title, string subtitle, bool active)
    {
        var row = N.adw_switch_row_new();
        N.adw_preferences_row_set_use_markup(row, false);
        N.adw_preferences_row_set_title(row, title);
        N.adw_action_row_set_subtitle(row, subtitle);
        N.adw_switch_row_set_active(row, active);
        return row;
    }

    public static nint Group(string title, string? description, params nint[] rows)
    {
        var g = N.adw_preferences_group_new();
        if (title.Length > 0) N.adw_preferences_group_set_title(g, title);
        if (description != null) N.adw_preferences_group_set_description(g, description);
        foreach (var r in rows) N.adw_preferences_group_add(g, r);
        return g;
    }

    // A page with nothing picked yet: character art, a title and what to do.
    public static nint Status(string art, string title, string? description, nint child = 0)
    {
        var box = Box(Vertical, 14, Css(Halign(ArtLabel(art), Center), "art", "status-art"), Css(Halign(N.gtk_label_new(title), Center), "title-2"));
        if (description != null)
        {
            var d = Label(description, "dim-label");
            N.gtk_label_set_justify(d, 2); // centre
            N.gtk_label_set_xalign(d, 0.5f);
            N.gtk_box_append(box, Clamp(d, 520));
        }
        if (child != 0) N.gtk_box_append(box, child);
        N.gtk_widget_set_valign(box, Center);
        return Expand(Margin(box, 24));
    }

    // A text view that wraps; the caller frames it.
    public static nint TextView(string text, bool editable, bool mono = false)
    {
        var tv = N.gtk_text_view_new();
        N.gtk_text_view_set_editable(tv, editable);
        N.gtk_text_view_set_cursor_visible(tv, editable);
        N.gtk_text_view_set_wrap_mode(tv, 3); // word, then char
        N.gtk_text_view_set_monospace(tv, mono);
        N.gtk_text_view_set_left_margin(tv, 14);
        N.gtk_text_view_set_right_margin(tv, 14);
        N.gtk_text_view_set_top_margin(tv, 12);
        N.gtk_text_view_set_bottom_margin(tv, 12);
        SetText(tv, text);
        return tv;
    }

    public static nint Clamp(nint child, int max = 860)
    {
        var c = N.adw_clamp_new();
        N.adw_clamp_set_maximum_size(c, max);
        N.adw_clamp_set_child(c, child);
        return c;
    }

    // A drop-down over a string list that can be changed later. It shows
    // its choice as "choice ▾" in a box; the list it opens is the plain choices.
    public static (nint DropDown, nint Model) StringDropDown(string first)
    {
        byte* none = null;
        var model = N.gtk_string_list_new(&none);
        N.gtk_string_list_append(model, first);
        var drop = N.gtk_drop_down_new(model, 0);
        if (choiceFactory == 0)
        {
            choiceFactory = Factory(s => $"{s} ▾");
            listFactory = Factory(s => s);
        }
        N.gtk_drop_down_set_factory(drop, choiceFactory);
        N.gtk_drop_down_set_list_factory(drop, listFactory);
        return (drop, model);
    }

    static nint choiceFactory, listFactory;

    // A list item factory showing each string, as say() writes it.
    public static nint Factory(Func<string, string> say)
    {
        var f = N.gtk_signal_list_item_factory_new();
        OnArg(f, "setup", item =>
        {
            var l = N.gtk_label_new("");
            N.gtk_label_set_xalign(l, 0);
            N.gtk_label_set_ellipsize(l, 2); // middle
            N.gtk_label_set_max_width_chars(l, 34);
            N.gtk_list_item_set_child(item, l);
        });
        OnArg(f, "bind", item =>
        {
            var choice = Str(N.gtk_string_object_get_string(N.gtk_list_item_get_item(item)));
            N.gtk_label_set_text(N.gtk_list_item_get_child(item), say(choice));
            Said(N.gtk_list_item_get_child(item), choice); // after the text, which a label says itself: not the ▾ after it
        });
        return f;
    }

    public static void SetText(nint tv, string text) => N.gtk_text_buffer_set_text(N.gtk_text_view_get_buffer(tv), text, -1);

    // SetText for a text the user is reading: only the part that differs is
    // replaced, so the cursor and the lines that show stay where they were.
    // (Setting all of it would leave GTK to lay it out again from the top.)
    public static void ReplaceText(nint tv, string text)
    {
        var old = GetText(tv);
        int head = 0, tail = 0;
        while (head < old.Length && head < text.Length && old[head] == text[head]) head++;
        while (tail < old.Length - head && tail < text.Length - head && old[^(tail + 1)] == text[^(tail + 1)]) tail++;
        if (head > 0 && char.IsHighSurrogate(old[head - 1])) head--; // never half a character
        if (tail > 0 && char.IsLowSurrogate(old[^tail])) tail--;
        var buf = N.gtk_text_view_get_buffer(tv);
        byte* from = stackalloc byte[256], to = stackalloc byte[256]; // GtkTextIter is 80 bytes
        N.gtk_text_buffer_get_iter_at_offset(buf, from, Characters(old, head));
        N.gtk_text_buffer_get_iter_at_offset(buf, to, Characters(old, old.Length - tail));
        N.gtk_text_buffer_begin_irreversible_action(buf); // like SetText: not something to undo
        N.gtk_text_buffer_delete(buf, from, to);
        N.gtk_text_buffer_insert(buf, from, text[head..^tail], -1);
        N.gtk_text_buffer_end_irreversible_action(buf);
    }

    // GTK counts characters; a C# string counts UTF-16 units.
    static int Characters(string s, int units)
    {
        var n = units;
        for (int i = 0; i < units; i++) if (char.IsLowSurrogate(s[i])) n--;
        return n;
    }

    public static string GetText(nint tv)
    {
        var buf = N.gtk_text_view_get_buffer(tv);
        byte* start = stackalloc byte[256], end = stackalloc byte[256]; // GtkTextIter is 80 bytes
        N.gtk_text_buffer_get_start_iter(buf, start);
        N.gtk_text_buffer_get_end_iter(buf, end);
        var p = N.gtk_text_buffer_get_text(buf, start, end, false);
        var s = Str(p);
        N.g_free(p);
        return s;
    }

    public static string Ago(string created)
    {
        if (!DateTime.TryParseExact(created, ["yyyy-MM-dd HH:mm", "yyyy-MM-dd"], null, System.Globalization.DateTimeStyles.None, out var t))
            return created;
        var d = DateTime.Now - t;
        if (created.Length <= 10) return t.Date == DateTime.Today ? "today" : t.Date == DateTime.Today.AddDays(-1) ? "yesterday" : created;
        return d.TotalMinutes < 1 ? "just now"
             : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min ago"
             : d.TotalHours < 24 ? $"{(int)d.TotalHours} h ago"
             : t.Date == DateTime.Today.AddDays(-1) ? "yesterday"
             : d.TotalDays < 7 ? $"{(int)d.TotalDays} days ago"
             : created[..10];
    }
}

// A text editor over text kept in a file. A change on disk never overwrites
// what the user typed: an editor without unsaved changes takes the new text,
// and one with unsaved changes shows a banner offering Reload or Keep mine.
sealed class Editor
{
    public readonly nint View, Banner, KeepMine;
    string loaded, onDisk; // the text the user started from, and the newest on disk

    public Editor(string text, string what)
    {
        loaded = onDisk = text;
        View = Ui.TextView(text, editable: true);
        Banner = N.adw_banner_new($"The {what} changed on disk while you were editing it.");
        N.adw_banner_set_button_label(Banner, "Reload");
        Ui.On(Banner, "button-clicked", () =>
        {
            Ui.ReplaceText(View, onDisk);
            Settle(onDisk);
        });
        Ui.On(N.gtk_text_view_get_buffer(View), "changed", () => Edited?.Invoke());
        KeepMine = Ui.Button("Keep mine", () => Settle(onDisk), "flat");
        N.gtk_widget_set_tooltip_text(KeepMine, "Keep your text; saving it replaces the version on disk");
        N.gtk_widget_set_visible(KeepMine, false);
    }

    public string Text => Ui.GetText(View);

    // What the user typed differs from the text they started from.
    public bool Dirty => !Same(Text, loaded);

    // Back to the text on disk.
    public void Revert()
    {
        Ui.ReplaceText(View, onDisk);
        Settle(onDisk);
    }

    // The text changed (typed, or taken from disk).
    public Action? Edited;

    // The user saved this text, or chose between theirs and the one on disk.
    public void Settle(string text)
    {
        loaded = onDisk = text;
        N.adw_banner_set_revealed(Banner, false);
        N.gtk_widget_set_visible(KeepMine, false);
    }

    // The text on disk now.
    public void Update(string text)
    {
        if (Same(text, onDisk)) return;
        onDisk = text;
        var mine = Text;
        if (Same(mine, text) || Same(text, loaded)) Settle(text);
        else if (Same(mine, loaded))
        {
            Ui.ReplaceText(View, text);
            Settle(text);
        }
        else
        {
            N.adw_banner_set_revealed(Banner, true);
            N.gtk_widget_set_visible(KeepMine, true);
        }
    }

    static bool Same(string a, string b) => a.Trim() == b.Trim();
}
