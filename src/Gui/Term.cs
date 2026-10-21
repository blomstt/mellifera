using System.Runtime.InteropServices;
using static Hive.Gui.Ui;

namespace Hive.Gui;

// The terminal skin's pieces: Nerd Font icons (plain Unicode without the
// font), panels titled in their border, the tab strip and the status line.
// It looks like a terminal, but it is never just text: these are the parts
// that type, shake and count down.
static unsafe class Icons
{
    // Symbols Nerd Font Mono is installed (ttf-nerd-fonts-symbols-mono).
    // Without it, every icon is a plain Unicode symbol from the text font.
    public static bool Nerd;

    public static void Detect()
    {
        nint families;
        int n;
        N.pango_font_map_list_families(N.pango_cairo_font_map_get_default(), &families, &n);
        for (int i = 0; i < n && !Nerd; i++)
            Nerd = Str(N.pango_font_family_get_name(((nint*)families)[i])) == "Symbols Nerd Font Mono";
        N.g_free(families);
    }

    static string I(int nerd, string plain) => Nerd ? char.ConvertFromUtf32(nerd) : plain;

    public static string Done => I(0xf00c, "✓");
    public static string Failed => I(0xf00d, "✗");
    public static string Molt => I(0xf040, "±");
    public static string Book => I(0xf02d, "≡");
    public static string Search => I(0xf002, "⌕");
    public static string Test => I(0xf0c3, "⊞");
    public static string Tool => I(0xf0ad, "⚙");
    public static string Inbox => I(0xf01c, "▤");
    public static string Web => I(0xf0ac, "◍");
    public static string Crown => I(0xf01a5, "◈");
    public static string Bee => I(0xf0fa1, "●");
    public static string Scroll => I(0xf0bc2, "≡");
    public static string Flower => I(0xf024a, "✿");
    public static string Shell => I(0xf120, "›");
    public static string Chat => I(0xf075, "…");
    public static string Undo => I(0xf0e2, "↶");
    public static string Copy => I(0xf0c5, "⧉");
    public static string Review => I(0xf188, "⊙");
    public static string Hourglass => I(0xf254, "⧗");
    public static string Mic => I(0xf130, "◉");
}

// Character art for the quiet places (a page with nothing picked yet).
static class Art
{
    public const string Bee = "ʚ●ɞ";
    public const string Scroll = "╭──────────╮\n│ ≡≡≡≡≡≡≡  │\n│ ≡≡≡≡     │\n│ ≡≡≡≡≡≡   │\n╰──────────╯";
    public const string Team = " ⌒⌒   ⌒⌒   ⌒⌒\n‹▓▓● ‹▓▓● ‹▓▓●\n  ≡    ≡    ≡\n  ╰────┼────╯\n       ▾  ⌒⌒\n       ≡ ●▓▓›";
    public const string Brief = "╭─ brief ──────╮\n│ ▓▓▓▓▓▓▓▓░░░░ │\n│ ≡≡≡≡≡≡       │\n╰──────────────╯";
}

// The tabs: "hive  bees  reviews  memory  briefs", the page shown bright
// with a honey line under it. One strip in the header, another at the
// bottom when narrow.
sealed class Tabs
{
    public readonly nint Bar;
    readonly List<(string Name, nint Button)> tabs = [];

    public Tabs(string[] names, Action<string> go)
    {
        Bar = Css(Box(Horizontal, 0), "tabs");
        foreach (var name in names)
        {
            var b = N.gtk_button_new_with_label(name);
            On(b, "clicked", () => go(name));
            N.gtk_box_append(Bar, Css(b, "tab"));
            tabs.Add((name, b));
        }
        N.gtk_widget_set_valign(Bar, Fill);
    }

    public void Show(string name)
    {
        foreach (var (n, b) in tabs)
            if (n == name) N.gtk_widget_add_css_class(b, "on");
            else N.gtk_widget_remove_css_class(b, "on");
    }
}

// The status line at the foot of the window: what the window just did,
// typed in. An action that can be taken back offers Undo for 10 s, with a
// bar that empties as the time runs out; an error is red, with ✗ and a small
// shake. Right holds what stays: the Claude usage meters.
sealed class StatusLine
{
    public readonly nint Bar, Message, UndoButton, Countdown, Right;
    Action? undo;
    double undoUntil;
    int undoGen;
    const double UndoSeconds = 10;

    public StatusLine()
    {
        Message = Label("", "status-msg");
        N.gtk_label_set_wrap(Message, false);
        N.gtk_label_set_ellipsize(Message, 3); // end
        Margin(Message, 0, 6, 0, 6);
        Expand(Message, true, false);
        Countdown = Css(ArtLabel(), "countdown");
        UndoButton = Button("Undo", () =>
        {
            var u = undo;
            Settle();
            u?.Invoke();
        });
        N.gtk_widget_set_visible(Countdown, false);
        N.gtk_widget_set_visible(UndoButton, false);
        Right = Box(Horizontal, 14);
        Bar = Css(Box(Horizontal, 12, Css(ArtLabel("›"), "prompt"), Message, Countdown, UndoButton, Right), "statusline");
    }

    public void Say(string text, bool error = false, bool plain = false)
    {
        if (plain)
        {
            N.gtk_widget_remove_css_class(Message, "error");
            TypeIn(Message, text, perSecond: double.PositiveInfinity);
        }
        else if (error)
        {
            N.gtk_widget_add_css_class(Message, "error");
            TypeIn(Message, "✗ " + text);
            Shake(Message);
        }
        else
        {
            N.gtk_widget_remove_css_class(Message, "error");
            TypeIn(Message, text);
        }
    }

    // What the user decided happened at once; Undo takes it back for 10 s.
    public void Undoable(string text, Action undo, bool plain = false)
    {
        Say(text, plain: plain);
        this.undo = undo;
        undoUntil = Clock + UndoSeconds;
        var gen = ++undoGen;
        N.gtk_widget_set_visible(UndoButton, true);
        N.gtk_widget_set_visible(Countdown, true);
        var shown = -1;
        Animate(t =>
        {
            if (gen != undoGen) return false;
            var left = undoUntil - t;
            if (left <= 0)
            {
                Settle();
                return false;
            }
            // Ten cells, the last one fading out smoothly (whole cells only
            // with animations off).
            var f = left / UndoSeconds * 10;
            var full = (int)f;
            var alpha = Smooth ? (int)((f - full) * 20) * 5 : 0;
            if (full * 100 + alpha == shown) return true;
            shown = full * 100 + alpha;
            N.gtk_label_set_markup(Countdown, new string('▰', full) + (alpha > 0 ? $"<span alpha=\"{alpha}%\">▰</span>" : "")
                + $"<span foreground=\"#{Palette.Border:X6}\">{new string('▱', 10 - full - (alpha > 0 ? 1 : 0))}</span>");
            return true;
        });
    }

    // Undo is used up, or its time ran out.
    void Settle()
    {
        undo = null;
        undoGen++;
        N.gtk_widget_set_visible(UndoButton, false);
        N.gtk_widget_set_visible(Countdown, false);
    }
}

// The layer over a panel. When the panel's page opens (or a popover shows
// it), its frame draws itself in from the top left corner and its title
// types in; when what it shows changes, a honey spark runs once around its
// border. Drawn only while one of them runs; one draw function for all.
static unsafe class Fx
{
    sealed class State
    {
        public nint Frame, Panel;
        public string Title = "", Page = "";
        public double Spark = -1, DrawIn = -1, Typed = -1; // when each started
        public bool Moving;
    }

    static readonly Dictionary<long, State> layers = [];
    static readonly Dictionary<long, long> byPanel = []; // panel (the overlay) -> its layer
    const double SparkSeconds = 0.6, DrawSeconds = 0.25, TitleSeconds = 0.35;

    // The page the panels made now belong to (set while each page is built).
    public static string Page = "";

    public static nint Layer(nint frame, string title)
    {
        var overlay = N.gtk_overlay_new();
        N.gtk_overlay_set_child(overlay, frame);
        var area = N.gtk_drawing_area_new();
        N.gtk_widget_set_can_target(area, false);
        OnDrawArea(area, Draw);
        N.gtk_overlay_add_overlay(overlay, area);
        layers[area] = new State { Frame = frame, Panel = overlay, Title = title, Page = Page };
        byPanel[overlay] = area;
        N.g_signal_connect_data(overlay, "destroy", (nint)(delegate* unmanaged<nint, nint, void>)&Gone, 0, 0, 0);
        return overlay;
    }

    // The panel is freed (its page was replaced): its layer goes with it.
    [UnmanagedCallersOnly]
    static void Gone(nint panel, nint data)
    {
        if (byPanel.Remove(panel, out var area)) layers.Remove(area);
    }

    // The panel's title changed (a detail panel now shows another bee): it types in.
    public static void Retitle(nint panel, string title)
    {
        N.gtk_frame_set_label_widget(N.gtk_overlay_get_child(panel), PanelTitle(""));
        Said(N.gtk_overlay_get_child(panel), title);
        if (!byPanel.TryGetValue(panel, out var area)) return;
        var state = layers[area];
        state.Title = title;
        if (!Smooth || !N.gtk_widget_get_mapped(panel)) { ShowTitle(state, title.Length); return; }
        state.Typed = Clock;
        ShowTitle(state, 0);
        Run(area, state);
    }

    // A spark runs once around the panel's border.
    public static void Spark(nint panel)
    {
        if (!Smooth || !byPanel.TryGetValue(panel, out var area) || !N.gtk_widget_get_mapped((nint)area)) return;
        var state = layers[area];
        state.Spark = Clock;
        Run(area, state);
    }

    // The frame draws itself in from its top left corner, and its title types in.
    public static void DrawIn(nint panel)
    {
        if (!Smooth || !byPanel.TryGetValue(panel, out var area)) return;
        var state = layers[area];
        state.DrawIn = state.Typed = Clock;
        N.gtk_widget_add_css_class(state.Frame, "drawing");
        N.gtk_widget_set_opacity(N.gtk_frame_get_child(state.Frame), 0);
        ShowTitle(state, 0);
        Run(area, state);
    }

    // Every panel of a page, as the page opens.
    public static void DrawInPage(string page)
    {
        foreach (var state in layers.Values)
            if (state.Page == page && N.gtk_widget_get_mapped(state.Panel)) DrawIn(state.Panel);
    }

    public static bool Sparking(nint panel) => byPanel.TryGetValue(panel, out var area) && layers[area].Spark >= 0;
    public static bool Drawing(nint panel) => byPanel.TryGetValue(panel, out var area) && layers[area].DrawIn >= 0;

    static void Run(long area, State state)
    {
        if (state.Moving) return;
        state.Moving = true;
        Animate(t =>
        {
            N.gtk_widget_queue_draw((nint)area);
            if (state.DrawIn >= 0)
            {
                var p = (t - state.DrawIn) / DrawSeconds;
                N.gtk_widget_set_opacity(N.gtk_frame_get_child(state.Frame), Math.Clamp(p * 1.4 - 0.3, 0, 1));
                if (p >= 1)
                {
                    state.DrawIn = -1;
                    N.gtk_widget_remove_css_class(state.Frame, "drawing");
                }
            }
            if (state.Typed >= 0)
            {
                var typed = (int)((Frame(t) - Frame(state.Typed)) / 12.5 / TitleSeconds * state.Title.Length);
                ShowTitle(state, typed);
                if (typed >= state.Title.Length) state.Typed = -1;
            }
            if (state.Spark >= 0 && t - state.Spark >= SparkSeconds) state.Spark = -1;
            state.Moving = state.Spark >= 0 || state.DrawIn >= 0 || state.Typed >= 0;
            return state.Moving;
        }, state.Panel);
    }

    // The title's first n characters, with a cursor while it types.
    static void ShowTitle(State state, int n)
    {
        var label = N.gtk_frame_get_label_widget(state.Frame);
        if (label == 0 || state.Title.Length == 0 || !N.gtk_widget_has_css_class(label, "panel-title")) return;
        n = Math.Clamp(n, 0, state.Title.Length);
        N.gtk_label_set_markup(label, TitleMarkup(state.Title[..n] + (n < state.Title.Length ? "▌" : "")));
    }

    static void Draw(nint area, nint cr, int w, int h)
    {
        if (!layers.TryGetValue(area, out var state) || state.Spark < 0 && state.DrawIn < 0) return;
        var now = Clock;
        // The border: the frame's edges, its top through the title's middle.
        var label = N.gtk_frame_get_label_widget(state.Frame);
        double top = label != 0 ? N.gtk_widget_get_height(label) / 2.0 : 0, x0 = 0.5, y0 = top + 0.5, x1 = w - 0.5, y1 = h - 0.5;
        if (state.DrawIn >= 0)
        {
            // Top and left edges first, from the corner; then right and bottom.
            var p = Math.Clamp((now - state.DrawIn) / DrawSeconds, 0, 1);
            var a = Math.Min(1, p / 0.6);
            var b = Math.Clamp((p - 0.4) / 0.6, 0, 1);
            Palette.Source(cr, Palette.Border);
            N.cairo_set_line_width(cr, 1);
            Line(cr, x0, y0, x0 + (x1 - x0) * a, y0, label, area);
            Line(cr, x0, y0, x0, y0 + (y1 - y0) * a, 0, 0);
            if (b > 0)
            {
                Line(cr, x1, y0, x1, y0 + (y1 - y0) * b, 0, 0);
                Line(cr, x0, y1, x0 + (x1 - x0) * b, y1, 0, 0);
            }
            // A honey point where the frame is being drawn.
            N.cairo_rectangle(cr, x0 + (x1 - x0) * a - 1.5, y0 - 1.5, 3, 3);
            N.cairo_rectangle(cr, x0 - 1.5, y0 + (y1 - y0) * a - 1.5, 3, 3);
            Palette.Source(cr, Palette.Pulse, 1 - p);
            N.cairo_fill(cr);
        }
        if (state.Spark < 0) return;
        var sp = Math.Clamp((now - state.Spark) / SparkSeconds, 0, 1);
        var perimeter = 2 * (x1 - x0) + 2 * (y1 - y0);
        // A bright head and a tail behind it, clockwise from the top left.
        const int pieces = 14;
        var head = sp * perimeter;
        var tail = perimeter * 0.16;
        for (int i = 0; i < pieces; i++)
        {
            var at = head - tail * i / pieces;
            if (at < 0) break;
            var (ax, ay) = Along(at, x0, y0, x1, y1);
            var (bx, by) = Along(Math.Max(0, at - tail / pieces), x0, y0, x1, y1);
            N.cairo_move_to(cr, ax, ay);
            N.cairo_line_to(cr, bx, by);
            N.cairo_set_line_width(cr, i == 0 ? 2.5 : 1.5);
            Palette.Source(cr, i == 0 ? Palette.Pulse : Palette.Honey, (1 - (double)i / pieces) * (sp < 0.85 ? 1 : (1 - sp) / 0.15));
            N.cairo_stroke(cr);
        }
    }

    // A border line, leaving out the title where it crosses it.
    static void Line(nint cr, double xa, double ya, double xb, double yb, nint title, nint area)
    {
        float* from = stackalloc float[2], to = stackalloc float[2];
        from[0] = from[1] = 0;
        if (title != 0 && N.gtk_widget_compute_point(title, area, from, to))
        {
            var (l, r) = (to[0] - 2, to[0] + N.gtk_widget_get_width(title) + 2);
            if (xb > l)
            {
                N.cairo_move_to(cr, xa, ya);
                N.cairo_line_to(cr, Math.Min(xb, l), yb);
                if (xb > r) { N.cairo_move_to(cr, r, ya); N.cairo_line_to(cr, xb, yb); }
                N.cairo_stroke(cr);
                return;
            }
        }
        N.cairo_move_to(cr, xa, ya);
        N.cairo_line_to(cr, xb, yb);
        N.cairo_stroke(cr);
    }

    static (double X, double Y) Along(double d, double x0, double y0, double x1, double y1)
    {
        double w = x1 - x0, h = y1 - y0;
        if (d < w) return (x0 + d, y0);
        d -= w;
        if (d < h) return (x1, y0 + d);
        d -= h;
        if (d < w) return (x1 - d, y1);
        d -= w;
        return (x0, y1 - Math.Min(d, h));
    }
}

// A row that wipes in or out left to right, the way a terminal redraws a
// line: a honey edge sweeps across with black behind it. After a wipe out
// the row closes up (a revealer) and is removed.
sealed unsafe class Wipe
{
    public readonly nint Outer, Cover, Row;
    double start = -1, length = 0.3;
    bool outward, covered;
    Action? done;

    static readonly Dictionary<long, Wipe> covers = [];
    static readonly List<Wipe> closing = [];
    static Action? removeClosed;

    public Wipe(nint row)
    {
        Row = row;
        Cover = N.gtk_drawing_area_new();
        N.gtk_widget_set_can_target(Cover, false);
        OnDrawArea(Cover, Draw);
        covers[Cover] = this;
        N.g_signal_connect_data(Cover, "destroy", (nint)(delegate* unmanaged<nint, nint, void>)&Gone, 0, 0, 0);
        var overlay = N.gtk_overlay_new();
        N.gtk_overlay_set_child(overlay, row);
        N.gtk_overlay_add_overlay(overlay, Cover);
        Outer = N.gtk_revealer_new();
        N.gtk_revealer_set_transition_type(Outer, 4); // slide up: the rows under it close up
        N.gtk_revealer_set_transition_duration(Outer, 220);
        N.gtk_revealer_set_child(Outer, overlay);
        N.gtk_revealer_set_reveal_child(Outer, true);
    }

    public bool Leaving { get; private set; }
    public bool Moving => start >= 0;

    // The row is freed (its page was replaced, or it was removed).
    [UnmanagedCallersOnly]
    static void Gone(nint cover, nint data) => covers.Remove(cover);

    // Hidden behind black until it wipes in (a result still on its way).
    public void Hold()
    {
        covered = true;
        N.gtk_widget_queue_draw(Cover);
    }

    public void In(double seconds = 0.3) => Run(false, seconds, null);

    // On its way out (now, or after a moment showing how it ended): held
    // until it's removed, since its page may be rebuilt meanwhile.
    public void Leave()
    {
        if (Leaving) return;
        Leaving = true;
        N.g_object_ref(Outer);
    }

    // Wipes out, closes up, and is removed from its box.
    public void Out()
    {
        if (wiping) return;
        wiping = true;
        Leave();
        Run(true, 0.3, () =>
        {
            N.gtk_revealer_set_reveal_child(Outer, false);
            closing.Add(this);
            removeClosed ??= RemoveClosed;
            After(Smooth ? 260u : 1u, removeClosed);
        });
    }

    bool wiping;

    // From the box it's in, if a page rebuilt meanwhile hasn't taken it already.
    static void RemoveClosed()
    {
        foreach (var w in closing)
        {
            if (N.gtk_widget_get_parent(w.Outer) is var box and not 0) N.gtk_box_remove(box, w.Outer);
            N.g_object_unref(w.Outer);
        }
        closing.Clear();
    }

    void Run(bool leave, double seconds, Action? then)
    {
        outward = leave;
        done = then;
        covered = false;
        if (!Smooth || !N.gtk_widget_get_mapped(Row)) // off screen: at once
        {
            covered = leave;
            start = -1;
            N.gtk_widget_queue_draw(Cover);
            then?.Invoke();
            return;
        }
        var running = start >= 0;
        start = Clock;
        length = seconds;
        if (running) return;
        Animate(t =>
        {
            N.gtk_widget_queue_draw(Cover);
            if (t - start < length) return true;
            start = -1;
            covered = outward;
            N.gtk_widget_queue_draw(Cover);
            var d = done;
            done = null;
            d?.Invoke();
            return false;
        }, Outer);
    }

    static void Draw(nint area, nint cr, int w, int h)
    {
        if (!covers.TryGetValue(area, out var wipe)) return;
        if (wipe.start < 0)
        {
            if (!wipe.covered) return;
            N.cairo_rectangle(cr, 0, 0, w, h);
            Palette.Source(cr, Palette.Panel); // rows sit on panels
            N.cairo_fill(cr);
            return;
        }
        var p = Math.Clamp((Clock - wipe.start) / wipe.length, 0, 1);
        var x = p * (w + 30) - 15;
        // Black where the row isn't (yet, or any more): left of the edge
        // going out, right of it coming in.
        if (wipe.outward) N.cairo_rectangle(cr, 0, 0, Math.Max(0, x), h);
        else N.cairo_rectangle(cr, Math.Max(0, x), 0, w, h);
        Palette.Source(cr, Palette.Panel); // rows sit on panels
        N.cairo_fill(cr);
        // The edge, ▓▒░, in honey.
        for (int i = 0; i < 3; i++)
        {
            var bx = wipe.outward ? x - (i + 1) * 5 : x + i * 5;
            N.cairo_rectangle(cr, bx, 0, 5, h);
            Palette.Source(cr, Palette.Honey, 0.85 - i * 0.28);
            N.cairo_fill(cr);
        }
    }
}

// A number that rolls like an odometer when it changes: each digit that
// changes slides up and out as the new one slides in from below.
sealed unsafe class Odometer
{
    public readonly nint Box;
    readonly nint layer;
    readonly List<(nint Old, nint New)> digits = [];
    string shown = "", from = "", to = "";
    double cw, lh, start = -1;

    public Odometer(string css)
    {
        layer = N.gtk_fixed_new();
        Box = Css(N.gtk_box_new(Horizontal, 0), "odometer", css);
        N.gtk_widget_set_overflow(Box, 1); // hidden: a digit rolling out of the line doesn't show
        N.gtk_box_append(Box, layer);
        N.gtk_widget_set_valign(Box, Center);
    }

    public string Shown => shown;

    public void Set(int n)
    {
        var text = n.ToString();
        if (text == shown) return;
        if (cw == 0)
        {
            var layout = N.gtk_widget_create_pango_layout(Box, "0");
            int w, h;
            N.pango_layout_get_size(layout, &w, &h);
            N.g_object_unref(layout);
            (cw, lh) = (w / 1024.0, h / 1024.0);
        }
        var width = Math.Max(text.Length, shown.Length);
        (from, to) = (shown.PadLeft(width), text.PadLeft(width));
        while (digits.Count < width)
        {
            var a = ArtLabel();
            var b = ArtLabel();
            N.gtk_fixed_put(layer, a, 0, 0);
            N.gtk_fixed_put(layer, b, 0, 0);
            digits.Add((a, b));
        }
        for (int i = 0; i < digits.Count; i++)
        {
            var (o, nw) = digits[i];
            N.gtk_label_set_text(o, i < width ? from[i].ToString() : "");
            N.gtk_label_set_text(nw, i < width ? to[i].ToString() : "");
            N.gtk_widget_set_visible(o, i < width && from[i] != to[i]);
            N.gtk_widget_set_visible(nw, i < width);
        }
        N.gtk_widget_set_size_request(Box, (int)Math.Ceiling(cw * width), (int)Math.Ceiling(lh));
        var first = shown.Length == 0;
        shown = text;
        Said(Box, text);
        if (!Smooth || first || !N.gtk_widget_get_mapped(Box))
        {
            Place(1);
            return;
        }
        var running = start >= 0;
        start = Clock;
        if (running) return;
        Animate(t =>
        {
            var p = Math.Clamp((t - start) / 0.35, 0, 1);
            Place(p < 0.5 ? 2 * p * p : 1 - Math.Pow(-2 * p + 2, 2) / 2);
            if (p < 1) return true;
            start = -1;
            return false;
        });
    }

    // The changed digits at p of their roll (0: the old ones show, 1: the new ones).
    void Place(double p)
    {
        for (int i = 0; i < from.Length; i++)
        {
            var (o, nw) = digits[i];
            var rolls = from[i] != to[i];
            N.gtk_fixed_move(layer, o, i * cw, rolls ? -p * lh : 0);
            N.gtk_fixed_move(layer, nw, i * cw, rolls ? (1 - p) * lh : 0);
            if (p >= 1) N.gtk_widget_set_visible(o, false);
        }
    }
}
