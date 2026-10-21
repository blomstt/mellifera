namespace Hive.Gui;

// Drawing what the mockups draw as SVG: text placed by its baseline (left,
// centred or right of x), hexagons, boxes and dashed lines, with cairo and
// Pango. Sizes are in the drawing's own units (the scene's 880 x 490 box,
// or pixels), so a scaled drawing scales its text too.
static unsafe class Ink
{
    // Laid-out text by markup and size, kept between frames (a frame redraws
    // the same few dozen strings); dropped when there are too many.
    static readonly Dictionary<string, nint> layouts = [];

    // Pango markup: text in a colour.
    public static string C(uint color, string text) => Palette.Span(color, text);

    // Draws markup at (x, baseline y): anchor 0 starts there, 0.5 centres
    // it, 1 ends there. Returns its width. px is the font size in units.
    public static double Text(nint cr, double x, double y, string markup, double px, double anchor = 0, bool bold = false)
    {
        var layout = Layout(cr, markup, px, bold);
        int w, h;
        N.pango_layout_get_size(layout, &w, &h);
        var width = w / 1024.0;
        N.cairo_move_to(cr, x - width * anchor, y - N.pango_layout_get_baseline(layout) / 1024.0);
        N.pango_cairo_show_layout(cr, layout);
        return width;
    }

    // How wide Text would draw markup, without drawing it.
    public static double Width(nint cr, string markup, double px, bool bold = false)
    {
        int w, h;
        N.pango_layout_get_size(Layout(cr, markup, px, bold), &w, &h);
        return w / 1024.0;
    }

    static nint Layout(nint cr, string markup, double px, bool bold)
    {
        var key = $"{px:0.##}{(bold ? "b" : "")}\u0001{markup}";
        if (!layouts.TryGetValue(key, out var layout))
        {
            if (layouts.Count > 600)
            {
                foreach (var l in layouts.Values) N.g_object_unref(l);
                layouts.Clear();
            }
            layout = N.pango_cairo_create_layout(cr);
            // Pango sizes are points (1024ths); a unit is a pixel at 96 dpi, 0.75 pt.
            N.pango_layout_set_markup(layout, $"<span font_family=\"Fira Code, Symbols Nerd Font Mono, monospace\" size=\"{(int)(px * 768)}\"{(bold ? " weight=\"bold\"" : "")}>{markup}</span>", -1);
            layouts[key] = layout;
        }
        else N.pango_cairo_update_layout(cr, layout);
        return layout;
    }

    // A pointy-top hexagon around (cx, cy), r from the centre to a corner.
    public static void Hex(nint cr, double cx, double cy, double r, uint fill, uint stroke, double width = 1, bool dashed = false, bool filled = true)
    {
        var hw = r * 0.8660254;
        N.cairo_new_path(cr);
        N.cairo_move_to(cr, cx, cy - r);
        N.cairo_line_to(cr, cx + hw, cy - r / 2);
        N.cairo_line_to(cr, cx + hw, cy + r / 2);
        N.cairo_line_to(cr, cx, cy + r);
        N.cairo_line_to(cr, cx - hw, cy + r / 2);
        N.cairo_line_to(cr, cx - hw, cy - r / 2);
        N.cairo_close_path(cr);
        if (filled)
        {
            Palette.Source(cr, fill);
            N.cairo_fill_preserve(cr);
        }
        Stroke(cr, stroke, width, dashed ? 3 : 0, 4);
    }

    // An outlined box, its edges on whole pixels' middles.
    public static void Box(nint cr, double x, double y, double w, double h, uint stroke, double width = 1)
    {
        N.cairo_new_path(cr);
        N.cairo_rectangle(cr, x, y, w, h);
        Stroke(cr, stroke, width);
    }

    public static void Fill(nint cr, double x, double y, double w, double h, uint color, double alpha = 1)
    {
        N.cairo_new_path(cr);
        N.cairo_rectangle(cr, x, y, w, h);
        Palette.Source(cr, color, alpha);
        N.cairo_fill(cr);
    }

    // Strokes the current path, dashed when on > 0.
    public static void Stroke(nint cr, uint color, double width = 1, double on = 0, double off = 0, double alpha = 1)
    {
        double* dash = stackalloc double[2];
        (dash[0], dash[1]) = (on, off);
        N.cairo_set_dash(cr, dash, on > 0 ? 2 : 0, 0);
        N.cairo_set_line_width(cr, width);
        Palette.Source(cr, color, alpha);
        N.cairo_stroke(cr);
        N.cairo_set_dash(cr, dash, 0, 0);
    }

    public static void Line(nint cr, double x1, double y1, double x2, double y2, uint color, double width = 1, double on = 0, double off = 0)
    {
        N.cairo_new_path(cr);
        N.cairo_move_to(cr, x1, y1);
        N.cairo_line_to(cr, x2, y2);
        Stroke(cr, color, width, on, off);
    }
}
