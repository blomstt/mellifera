using static Hive.Gui.Ui;

namespace Hive.Gui;

// The other pages' character art: a bee's vignette at the top of its page,
// a review's mini scene, Memory's results cascading in and an entry's front
// matter as a file header. Each moves only while something it shows moves.
sealed unsafe partial class App
{
    // ---- a bee's vignette ---------------------------------------------------

    // Its cell, large: resting in it, or (dashed, honey) where it works now,
    // bobbing as it does in the scene, with a hint of the station's prop.
    sealed class Vignette(string bee, nint box)
    {
        public readonly string Bee = bee;
        public readonly nint Box = box;
        public readonly Piece Art = new(); // what it says: "at the bench", "in its cell"
        public double Bob;
        public bool Moving;
    }

    Vignette? vignette;
    Func<double, bool> vignetteStep = null!;

    nint BuildVignette(string bee)
    {
        var area = N.gtk_drawing_area_new();
        N.gtk_widget_set_size_request(area, 110, 110);
        N.gtk_widget_set_valign(area, Center);
        Said(area, sprites.TryGetValue(bee, out var sprite) && sprite.Said.Length > 0 ? sprite.Said : bee);
        var v = vignette = new Vignette(bee, area);
        OnDrawArea(area, (a, cr, w, h) => { if (vignette is { } shown && shown.Box == a) PaintVignette(shown, cr, w, h); });
        vignetteStep ??= VignetteStep;
        DrawVignette(Clock);
        StartVignette();
        return area;
    }

    void StartVignette()
    {
        if (vignette is not { Moving: false } v || !Smooth || !VignetteBusy(v)) return;
        v.Moving = true;
        Animate(vignetteStep, v.Box);
    }

    bool VignetteBusy(Vignette v) => vignette == v && selectedBee == v.Bee && sprites.TryGetValue(v.Bee, out var s) && (s.Flying || s.State is "work" or "think" or "deliver");

    bool VignetteStep(double t)
    {
        if (vignette is not { } v) return false;
        DrawVignette(t);
        v.Moving = VignetteBusy(v) && N.gtk_widget_get_mapped(v.Box);
        return v.Moving;
    }

    void DrawVignette(double t)
    {
        if (vignette is not { } v) return;
        var s = sprites.GetValueOrDefault(v.Bee);
        v.Art.Say(s == null ? "in its cell" : s.State switch
        {
            "work" or "think" or "fail" => (s.Station == Library ? "in the library" : s.Station == Web ? "on the web" : s.Station == Flowers ? "at the flowers" : "at the " + s.Station) + (s.State == "fail" ? " ✗" : ""),
            "wait" => "waits for reviewers",
            "talk" => "talking with you",
            "deliver" => "on its way home",
            _ => "in its cell",
        });
        v.Bob = Smooth && s?.State is "work" or "think" ? -2 * (0.5 - 0.5 * Math.Cos(Math.Tau * t / 1.4)) : 0;
        N.gtk_widget_queue_draw(v.Box);
    }

    void PaintVignette(Vignette v, nint cr, int w, int h)
    {
        var s = sprites.GetValueOrDefault(v.Bee);
        var out_ = s != null && s.State is not ("rest" or "deliver");
        var talk = s?.State == "talk";
        N.cairo_save(cr);
        N.cairo_translate(cr, w / 2.0, h / 2.0);
        N.cairo_scale(cr, w / 100.0, h / 100.0);
        Ink.Hex(cr, 0, 0, 46, out_ ? Palette.QueenCell : Palette.Cell, talk ? Palette.Pulse : out_ ? Palette.Flight : Palette.Border, talk ? 1.5 : 1, dashed: out_ && !talk);
        var art = Ink.C(talk ? Palette.Pulse : Palette.Dim, v.Art.Text);
        Ink.Text(cr, 0, -8, art, Math.Min(10, 10 * 72 / Ink.Width(cr, art, 10)), 0.5); // smaller when it's wider than the hexagon
        Ink.Text(cr, 0, 14 + v.Bob, SpriteMarkup(Palette.Dim, s?.State == "fail" ? Palette.Faint : Palette.Pulse), 15, 0.5);
        // A hint of what it works with.
        var hint = s?.State switch
        {
            "work" or "think" => s.Station switch { Bench => "≡≡≡ ≡≡", Library => "▐▌▌▐▌▐", Tests => "⬢ ⬢ ⬡", Web => "─ ◎ ─", Shell => "$ _", _ => "✿ ❀ ✿" },
            "wait" when s.Stacked >= 0 => s.Prop.Text,
            "fail" => "✗",
            _ => "",
        };
        if (hint.Length > 0) Ink.Text(cr, 0, 30, Ink.C(s?.State == "fail" ? Palette.Red : Palette.Pulse, hint), 9, 0.5);
        N.cairo_restore(cr);
    }
}
