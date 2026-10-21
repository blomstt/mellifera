using static Hive.Gui.Ui;

namespace Hive.Gui;

// The scene on the Hive page, drawn as the approved mockup draws it: a
// honeycomb of pointy-top hexagons around the Queen at the left, one cell per
// bee with its name inside and its sprite ʚ●ɞ resting there, and six
// stations as a framed 2 x 3 grid of character props at the right: the
// library (reading), the web, the bench (edits and builds), the tests, the
// shell (any other command) and the flowers (waiting on you). A working bee
// leaves its cell dashed ("▸ bench"), flies along a dashed path to the
// station of its live step, and that station lights up honey while it's
// there. It all lives in one 880 x 490 box, scaled to the panel's width,
// drawn with cairo and Pango from the frame clock while anything moves. With
// no bee working and nothing just happened there is no tick at all.
sealed unsafe partial class App
{
    // Something the scene draws that a test or a screen reader may ask
    // about: its text and where it is, in scene units. Not a widget.
    sealed class Piece
    {
        public string Text = "";
        public double X, Y;
        public bool Shown = true;

        public Piece Say(string s) { Text = s; return this; }
        public Piece At(double x, double y) { (X, Y) = (x, y); return this; }
        public Piece Show(bool on) { Shown = on; return this; }
    }

    // A bee in the scene: its sprite (Body, at its baseline centre) and what
    // it carries or shows beside it (Prop: "≡ 1/2", "✗").
    sealed class Sprite(string bee)
    {
        public readonly string Bee = bee;
        public readonly Piece Body = new Piece().Say("ʚ●ɞ"), Prop = new Piece().Show(false);
        public double X, Y;                                       // where it is now (scene units, baseline centre)
        public double FromX, FromY, ToX, ToY, Start = -1, Length; // a flight
        public bool Spiral;
        public Action? Landed;
        public string State = "", Station = "";                   // rest, work, think, wait, talk, fail, deliver; its station
        public bool Carrying;                                     // a scroll (a result) on its way
        public bool OnPath;                                       // flying between two bees (a scroll handed on)
        public Work? Work;
        public string TaskId = "";                                // the task it works on, to see how it ended
        public int Stacked = -1;                                  // the overseer: reviewers' scrolls landed
        public string Delivering = "";                            // the task whose result it carries
        public double Phase;                                      // its own bob, so bees side by side don't bob together
        public string Said = "";                                  // what a screen reader hears for it
        public bool Held;                                         // waits in its cell for a scroll handed to it
        public bool Flying => Start >= 0;
    }

    // A station: its frame in the 2 x 3 grid, its title in the border, and
    // what it says while no bee is there.
    sealed class Station(string name, string idle, double x, double y)
    {
        public readonly string Name = name, Idle = idle;
        public readonly double X = x, Y = y;
        public const double W = 180, H = 124;
        public readonly List<Sprite> Here = []; // the bees there now, in arrival order (drawn left to right)
    }

    // A one-shot effect: a spark where a result lands, a puff, "+lesson" from a cell.
    sealed class Effect(double born, double life, string kind, double x, double y, string text)
    {
        public readonly double Born = born, Life = life, X = x, Y = y;
        public readonly string Kind = kind, Text = text;
    }

    // The scene's own stations, by the station a step goes to (Steps.cs).
    public const string Library = "library", Web = "web", Bench = "bench", Tests = "tests", Shell = "shell", Flowers = "flowers";

    static string StationFor(string step) => step switch
    {
        Steps.Flowers => Bench,     // edits and builds
        Steps.Comb => Tests,
        Steps.Library or Steps.Hive => Library,
        Steps.Field => Web,
        _ => Shell,
    };

    const double SceneW = 880, SceneH = 490, CellR = 44, CellStep = 79.7, RowStep = 69, QueenX = 240, QueenY = 260;

    nint scene, sceneArea, sceneFixed, atWork, restLabel;
    double sceneScale = 1;                               // pixels per scene unit
    double hexR = CellR, hexStep = CellStep, hexRow = RowStep;
    readonly List<(double X, double Y)> slots = [];      // cell centres: the Queen, the bees, "+ new"
    readonly Dictionary<string, Sprite> sprites = [];
    readonly List<Station> stations =
    [
        new(Library, "reads files & the hive", 480, 30), new(Web, "searches & fetches", 680, 30),
        new(Bench, "edits & builds", 480, 188), new(Tests, "runs tests", 680, 188),
        new(Shell, "runs commands", 480, 346), new(Flowers, "waits on you", 680, 346),
    ];
    readonly List<Effect> effects = [];
    readonly List<long> cellLabels = [];                 // a label over each cell, for its tooltip and screen readers
    bool synced, sceneRunning, moving;
    Func<double, bool> sceneStep = null!;
    Action slowTick = null!;
    long lastSecond, lastMinute;
    double drawn;                                        // the time the scene was last drawn for

    nint BuildScene()
    {
        sceneArea = Expand(N.gtk_drawing_area_new(), true, false);
        OnDrawArea(sceneArea, (_, cr, w, h) => PaintScene(cr, w, h));
        OnResize(sceneArea, (_, w, _) => LayoutScene(w));
        sceneFixed = N.gtk_fixed_new();
        scene = N.gtk_overlay_new();
        N.gtk_overlay_set_child(scene, sceneArea);
        N.gtk_overlay_add_overlay(scene, sceneFixed);
        N.gtk_widget_set_overflow(scene, 1); // hidden: a flight stays inside the panel
        OnClick(scene, ClickScene);
        sceneStep = SceneStep;
        slowTick = SlowTick;
        PlaceCells();
        // What the hive shows, under it: what the marks mean, and that it's still when no bee works.
        nint Mark(string markup)
        {
            var l = Css(ArtLabel(), "tiny", "faint");
            N.gtk_label_set_markup(l, markup);
            N.gtk_label_set_ellipsize(l, 3); // a narrow window shortens them, never the other way round
            return l;
        }
        var legend = Css(Box(Horizontal, 20,
            Mark(Ink.C(Palette.Pulse, "ʚ●ɞ") + " resting"),
            Mark(Ink.C(Palette.Flight, "┄┄") + " flight to a station"),
            Mark(Ink.C(Palette.Flight, "⬡") + " lit station: a bee is there"),
            Expand(N.gtk_box_new(Horizontal, 0), true, false),
            Mark("still when no bee works")), "legend");
        Margin(legend, 6, 0, 0, 0);
        return Box(Vertical, 0, scene, legend);
    }

    // The cells' centres, the nearest the Queen first (ring by ring, top
    // row first within a ring), as many as the Queen, the bees and "+ new"
    // need; the grid shrinks when they wouldn't fit beside the stations.
    void PlaceCells()
    {
        var n = bees.Count + 2;
        // Each cell as (2 x its column, its row) from the Queen's; rows that
        // are odd sit half a cell over. Sorted by distance, then row, then column.
        var keys = new double[13 * 13];
        var all = new (int X, int Y)[13 * 13];
        for (int r = -6, i = 0; r <= 6; r++)
            for (int c = -6; c <= 6; c++, i++)
            {
                var x2 = 2 * c + (r % 2 != 0 ? 1 : 0);
                all[i] = (x2, r);
                keys[i] = Math.Round(Math.Sqrt(x2 * x2 / 4.0 * CellStep * CellStep + r * r * RowStep * RowStep)) * 1e4 + (r + 6) * 100 + x2 + 20;
            }
        Array.Sort(keys, all);
        // Fit: the cells' outer edges inside x 12..456, y 12..478.
        var k = 1.0;
        for (; k > 0.4; k -= 0.05)
        {
            double minX = 1e9, maxX = -1e9, minY = 1e9, maxY = -1e9;
            for (int i = 0; i < n; i++)
            {
                minX = Math.Min(minX, all[i].X / 2.0 * CellStep * k); maxX = Math.Max(maxX, all[i].X / 2.0 * CellStep * k);
                minY = Math.Min(minY, all[i].Y * RowStep * k); maxY = Math.Max(maxY, all[i].Y * RowStep * k);
            }
            var hw = CellR * k * 0.866;
            if (QueenX + minX - hw >= 12 && QueenX + maxX + hw <= 456 && QueenY + minY - CellR * k >= 12 && QueenY + maxY + CellR * k <= 478) break;
        }
        (hexR, hexStep, hexRow) = (CellR * k, CellStep * k, RowStep * k);
        slots.Clear();
        for (int i = 0; i < n; i++) slots.Add((QueenX + all[i].X / 2.0 * hexStep, QueenY + all[i].Y * hexRow));
    }

    // The scene's height follows its width: the mockup's 880 x 490. The
    // new height is asked for after this allocation (GTK drops a resize
    // queued while it allocates).
    void LayoutScene(int w)
    {
        if (w <= 0) return;
        sceneScale = w / SceneW;
        sceneHeight = (int)Math.Round(SceneH * sceneScale);
        setSceneHeight ??= () => N.gtk_drawing_area_set_content_height(sceneArea, sceneHeight);
        After(1, setSceneHeight);
        PlaceCellLabels();
        if (!synced) SyncScene();
    }

    int sceneHeight;
    Action? setSceneHeight;

    // ---- who is where ------------------------------------------------------

    int CellOf(string bee) => bees.FindIndex(b => b.Name == bee) + 1;

    // A resting bee's sprite, in its cell under its name.
    (double X, double Y) Home(string bee)
    {
        var i = CellOf(bee);
        if (i <= 0 || i >= slots.Count) return (QueenX, QueenY);
        var (x, y) = slots[i];
        return (x, y + hexR * 0.36);
    }

    // A bee's place at a station: along its bottom line, left of the caption.
    (double X, double Y) AtStation(string name, int slot)
    {
        var st = stations.Find(s => s.Name == name) ?? stations[2];
        return (st.X + 20 + slot * 28, st.Y + 112);
    }

    // Where each bee should be, from what it's doing: a task's live step
    // picks its station; a subagent in a session goes by its role; a waiting
    // overseer waits at the library with its stack; a chat with you waits at
    // the flowers; a bee whose task failed stays down at its station until
    // its inbox item is handled.
    (string State, string Station) Want(Bee b, Sprite s, Work? w)
    {
        if (w == null && FailedIn(b.Name)) return ("fail", s.Station is { Length: > 0 } and not "home" ? s.Station : LastStation(b.Name));
        if (w == null) return ("rest", "home");
        if (w.Talk) return ("talk", Flowers);
        if (w.Waiting) return ("wait", Library);
        if (w.Task == null) return ("work", b.Doc.Get("role") is "reviewer" or "overseer" ? Library : Bench);
        // Thinking stays where it is; a reviewer or the overseer starts in the library, others at the bench.
        if (w.Step is not { } step || step.Station == Steps.Thinking)
            return ("think", s.State is "work" or "think" && s.Station is not ("home" or "") ? s.Station
                : w.Task.Doc.Get("role") is "reviewer" or "overseer" ? Library : Bench);
        return ("work", StationFor(step.Station));
    }

    // A failed task of this bee waits in the inbox (not dismissed).
    bool FailedIn(string bee)
    {
        foreach (var i in items) if (i.Failed && i.Bee == bee) return true;
        return false;
    }

    // Where the bee's newest task was when it ended: its last step's station.
    string LastStation(string bee)
    {
        foreach (var t in allTasks)
        {
            if (t.Bee != bee) continue;
            try
            {
                if (File.Exists(t.StepsPath))
                    foreach (var line in File.ReadLines(t.StepsPath).Reverse())
                        if (Steps.Parse(line) is { IsResult: false } step && step.Station != Steps.Thinking) return StationFor(step.Station);
            }
            catch (IOException) { }
            break;
        }
        return Shell;
    }

    (double X, double Y) Target(string bee, string state, string station, int slot) =>
        state == "rest" ? Home(bee) : AtStation(station, slot);

    Sprite SpriteOf(string bee)
    {
        if (sprites.TryGetValue(bee, out var s)) return s;
        s = new Sprite(bee) { Phase = sprites.Count * 0.23 };
        (s.X, s.Y) = Home(bee);
        return sprites[bee] = s;
    }

    // Puts every bee where its work says, flying there when it changed (at
    // the first look, it's just there).
    bool queenLit; // her turn in the work pane runs: her cell says ❯ talking

    void SyncScene()
    {
        if (sceneArea == 0) return;
        if (slots.Count != bees.Count + 2) PlaceCells();
        var first = !synced;
        synced = true;
        foreach (var gone in sprites.Keys.Where(k => !bees.Exists(b => b.Name == k)).ToList()) sprites.Remove(gone);
        var taken = new Dictionary<string, int>();
        foreach (var b in bees)
        {
            var s = SpriteOf(b.Name);
            var w = WorkOf(b.Name);
            var was = s.TaskId;
            s.Work = w;
            s.TaskId = w?.Task?.Id ?? "";
            if (!first && was.Length > 0 && w == null && FindTask(was) is { } ended && s.State is "work" or "think")
            {
                Ended(s, ended);
                continue;
            }
            // Follow up or Hand to: the scroll comes first, then the bee flies out with it.
            if (!first && s.TaskId.Length > 0 && was != s.TaskId && w?.Task?.Doc.Get("follows") is { Length: > 0 } from && FindTask(from) is { } source)
                HandOff(source.Bee, s);
            if (s.Held) continue;
            if (s.Landed != null && s.Flying && w == null) continue; // a delivery on its way home
            var (state, station) = Want(b, s, w);
            var slot = state == "rest" ? 0 : taken[station] = taken.GetValueOrDefault(station) + 1;
            if (slot > 0) slot--;
            Place(s, state, station, slot, first || !Smooth);
        }
        PlaceOverseerStacks(first);
        PlaceCellLabels();
        if ((talk != null && TalkBusy() != null) is var lit && lit != queenLit)
        {
            queenLit = lit;
            N.gtk_widget_queue_draw(sceneArea);
        }
        StartScene();
        DrawVignette(Clock); // the shown bee's vignette follows its sprite
        StartVignette();
        if (first || !sceneRunning) DrawAll(Clock);
    }

    bool Busy(Sprite s) => s.Flying || s.State is "work" or "think";

    void Place(Sprite s, string state, string station, int slot, bool jump)
    {
        var (tx, ty) = Target(s.Bee, state, station, slot);
        var changed = s.State != state;
        s.State = state;
        s.Station = state == "rest" ? "home" : station;
        Speak(s);
        if (jump)
        {
            (s.X, s.Y) = (tx, ty);
            s.Start = -1;
            s.Landed = null;
            return;
        }
        if (Math.Abs(tx - (s.Flying ? s.ToX : s.X)) < 0.5 && Math.Abs(ty - (s.Flying ? s.ToY : s.Y)) < 0.5) return;
        Fly(s, tx, ty, spiral: state == "fail" && changed);
    }

    void Fly(Sprite s, double tx, double ty, Action? landed = null, bool spiral = false, double? length = null, bool path = false)
    {
        s.OnPath = path;
        (s.FromX, s.FromY, s.ToX, s.ToY) = (s.X, s.Y, tx, ty);
        var d = Math.Sqrt(Math.Pow(tx - s.X, 2) + Math.Pow(ty - s.Y, 2));
        s.Length = length ?? Math.Clamp(0.7 + d / 300, 0.8, 1.6);
        s.Start = Now();
        s.Spiral = spiral;
        s.Landed = landed;
        if (!Smooth)
        {
            (s.X, s.Y) = (tx, ty);
            s.Start = -1;
            s.Landed = null;
            landed?.Invoke();
        }
    }

    // A bee's work ended since the last look: a reviewer carries its scroll
    // to the overseer, a finished bee flies its result home (and the inbox
    // takes it as it lands), a failed one spirals down at its station, a
    // stopped one flies home.
    void Ended(Sprite s, BeeTask t)
    {
        if (t.Status == "done" && t.Doc.Get("role") == "reviewer" && OverseerFor(t) is { } overseer && overseer.State == "wait")
        {
            s.State = "deliver";
            s.Carrying = true;
            s.Delivering = t.Id;
            Speak(s);
            Fly(s, overseer.X + 22, overseer.Y - 10, () =>
            {
                s.Carrying = false;
                overseer.Stacked = Math.Max(0, overseer.Stacked) + 1;
                Spark(s.X + 10, s.Y - 12, "≡");
                GoHome(s);
            }, length: 1.2, path: true);
            return;
        }
        if (t.Status == "done")
        {
            s.State = "deliver";
            s.Carrying = true;
            Speak(s);
            var (hx, hy) = Home(s.Bee);
            Fly(s, hx, hy, () =>
            {
                s.Carrying = false;
                s.State = "rest";
                s.Station = "home";
                Speak(s);
                Spark(s.X, s.Y - 22, "✓");
                Delivered(t.Id);
            });
            return;
        }
        if (t.Status == "failed")
        {
            var station = s.Station is "home" or "" ? LastStation(s.Bee) : s.Station;
            s.State = "fail";
            s.Station = station;
            Speak(s);
            Fly(s, s.X, s.Y + 2, () => Puff(s.X + 14, s.Y - 8), spiral: true, length: 1.2);
            return;
        }
        GoHome(s);
    }

    // A task carries on from another: the first bee flies its scroll over
    // to the next bee's cell and goes home; the next one then flies out with
    // it. The same bee (Follow up) takes its own scroll out again.
    void HandOff(string fromBee, Sprite to)
    {
        if (!Smooth || slots.Count == 0) return;
        if (!sprites.TryGetValue(fromBee, out var giver) || giver == to || giver.Flying || giver.State != "rest")
        {
            to.Carrying = true; // its own scroll, out to its station
            var (hx, hy) = Home(to.Bee);
            Spark(hx, hy - 24, "≡");
            return;
        }
        to.Held = true;
        giver.State = "deliver";
        giver.Carrying = true;
        Speak(giver);
        var (tx, ty) = Home(to.Bee);
        Fly(giver, tx + 18, ty - 12, () =>
        {
            giver.Carrying = false;
            Spark(giver.X, giver.Y - 14, "≡");
            GoHome(giver);
            to.Held = false;
            to.Carrying = true;
            resync = true; // after this frame: it flies out
        }, length: 1.2, path: true);
    }

    bool resync;

    void GoHome(Sprite s)
    {
        s.State = "rest";
        s.Station = "home";
        Speak(s);
        var (hx, hy) = Home(s.Bee);
        Fly(s, hx, hy);
    }

    // The overseer a reviewer's report goes to (its run's), if it's in the scene.
    Sprite? OverseerFor(BeeTask reviewer)
    {
        foreach (var t in allTasks)
            if (t.Doc.Get("role") == "overseer" && t.Doc.GetList("waits_for").Contains(reviewer.Id) && sprites.TryGetValue(t.Bee, out var o)) return o;
        return null;
    }

    // The overseer's stack: how many reviewers' scrolls have landed. At the
    // first look it's what's done already; after, a scroll counts when it lands.
    void PlaceOverseerStacks(bool first)
    {
        foreach (var s in sprites.Values)
        {
            if (s.State != "wait" || s.Work?.Task is not { } t) { s.Stacked = -1; continue; }
            var done = 0;
            foreach (var id in t.Doc.GetList("waits_for")) if (FindTask(id) is { Running: false }) done++;
            // Scrolls still on their way don't count yet.
            var flying = 0;
            foreach (var d in sprites.Values) if (d.Carrying && t.Doc.GetList("waits_for").Contains(d.Delivering)) flying++;
            if (first || s.Stacked < 0 || s.Stacked < done - flying) s.Stacked = done - flying;
        }
    }

    // What a screen reader hears for a bee: who, what it's doing, for how long.
    // Set when it changes (and each minute), not every frame.
    void Speak(Sprite s)
    {
        var w = s.Work;
        var took = w != null ? ", " + Minutes(w.Started) : "";
        var said = s.State switch
        {
            "work" => $"{s.Bee}, working: {w?.Step?.Say ?? w?.Title ?? "at work"}{took}",
            "think" => $"{s.Bee}, thinking{took}",
            "wait" when w?.Task is { } t => $"{s.Bee}, waiting for {t.Doc.GetList("waits_for").Count} reviewers, {Math.Max(0, s.Stacked)} reports in{took}",
            "wait" => $"{s.Bee}, waiting{took}",
            "talk" => $"{s.Bee}, talking with you{took}",
            "fail" => $"{s.Bee}, its task failed",
            "deliver" => $"{s.Bee}, bringing its result to the inbox",
            _ => $"{s.Bee}, resting in its cell",
        };
        if (said == s.Said) return;
        s.Said = said;
        var i = CellOf(s.Bee);
        if (i > 0 && i < cellLabels.Count) SpeakCell((nint)cellLabels[i], said);
        if (vignette?.Bee == s.Bee)
        {
            Said(vignette.Box, said);
            DrawVignette(Now());
        }
    }

    static void SpeakCell(nint label, string said)
    {
        N.gtk_widget_set_tooltip_text(label, said);
        Said(label, said);
    }

    static string Minutes(DateTime since)
    {
        var m = (int)(DateTime.Now - since).TotalMinutes;
        return m < 1 ? "just started" : m == 1 ? "1 minute" : m < 60 ? $"{m} minutes" : $"{m / 60} h {m % 60} min";
    }

    // ---- motion -------------------------------------------------------------

    // Runs the scene from the frame clock while anything in it moves (with
    // animations off, a second at a time for the times).
    void StartScene()
    {
        if (sceneRunning || !SceneBusy()) return;
        sceneRunning = moving = true;
        if (Smooth) Animate(sceneStep);
        else After(1000, slowTick);
    }

    bool SceneBusy() => effects.Count > 0 || sprites.Values.Any(Busy);

    void SlowTick()
    {
        SceneStep(Clock);
        if (sceneRunning) After(1000, slowTick);
    }

    bool SceneStep(double t)
    {
        DrawAll(t);
        if (resync)
        {
            resync = false;
            SyncScene();
        }
        var second = (long)t;
        if (second != lastSecond)
        {
            lastSecond = second;
            if (DateTime.Now.Minute != lastMinute)
            {
                lastMinute = DateTime.Now.Minute;
                foreach (var s in sprites.Values) Speak(s);
                BuildCells();
                PlaceCellLabels();
            }
        }
        sceneRunning = moving = SceneBusy();
        if (!sceneRunning) DrawAll(t); // the last frame: everything still
        return sceneRunning;
    }

    // The time of the frame being drawn (what a flight or an effect that
    // starts during it counts from), or now.
    double? frameTime;
    double Now() => frameTime ?? Clock;

    // Everything in the scene at time t: where each sprite is, then a redraw.
    void DrawAll(double t)
    {
        frameTime = t;
        try
        {
            drawn = t;
            var smooth = Smooth;
            foreach (var st in stations) st.Here.Clear();
            foreach (var s in sprites.Values) MoveSprite(s, t, smooth);
            for (int i = effects.Count - 1; i >= 0; i--)
                if ((t - effects[i].Born) / effects[i].Life >= 1) effects.RemoveAt(i);
            if (sceneArea != 0) N.gtk_widget_queue_draw(sceneArea);
        }
        finally { frameTime = null; }
    }

    void MoveSprite(Sprite s, double t, bool smooth)
    {
        if (s.Flying)
        {
            var p = Math.Clamp((t - s.Start) / s.Length, 0, 1);
            var e = p < 0.5 ? 2 * p * p : 1 - Math.Pow(-2 * p + 2, 2) / 2; // ease in and out
            var (x, y) = Along(s.FromX, s.FromY, s.ToX, s.ToY, e);
            if (s.Spiral)
            {
                var r = 14 * (1 - p);
                x += r * Math.Cos(p * Math.Tau * 2);
                y += r * 0.5 * Math.Sin(p * Math.Tau * 2);
            }
            (s.X, s.Y) = (x, y);
            if (p >= 1)
            {
                s.Start = -1;
                if (s.Carrying && s.State is "work" or "think") s.Carrying = false; // a handed scroll, brought to its station
                var landed = s.Landed;
                s.Landed = null;
                landed?.Invoke();
            }
        }
        // At work it bobs, 3 units up and back every 1.4 s (still with animations off).
        var bob = smooth && s.State is "work" or "think" && !s.Flying ? -3 * (0.5 - 0.5 * Math.Cos(Math.Tau * (t / 1.4 + s.Phase))) : 0;
        s.Body.At(s.X, s.Y + bob);
        if (!s.Flying && s.State is not ("rest" or "deliver") && stations.Find(st => st.Name == s.Station) is { } at) at.Here.Add(s);
        // Beside it: the overseer's stack, a failed bee's ✗, a scroll on its way.
        var prop = s.Carrying ? "≡"
            : s.State == "fail" ? "✗"
            : s.State == "wait" && s.Stacked >= 0 && s.Work?.Task is { } task ? $"≡ {s.Stacked}/{task.Doc.GetList("waits_for").Count}"
            : "";
        s.Prop.Say(prop).Show(prop.Length > 0).At(s.X + 14, s.Y + bob);
    }

    // A point along the S-curve the mockup's flight paths take from (x1, y1)
    // to (x2, y2): out level from the start, in level to the end.
    static (double X, double Y) Along(double x1, double y1, double x2, double y2, double t)
    {
        var dx = x2 - x1;
        double cx1 = x1 + dx * 0.45, cy1 = y1, cx2 = x2 - dx * 0.4, cy2 = y2;
        var u = 1 - t;
        return (u * u * u * x1 + 3 * u * u * t * cx1 + 3 * u * t * t * cx2 + t * t * t * x2,
                u * u * u * y1 + 3 * u * u * t * cy1 + 3 * u * t * t * cy2 + t * t * t * y2);
    }

    // ---- one-shot effects -----------------------------------------------------

    void AddFx(string kind, double x, double y, string text, double life)
    {
        if (!Smooth || slots.Count == 0) return; // a one-shot effect is all motion
        effects.Add(new Effect(Now(), life, kind, x, y, text));
        StartScene();
    }

    // "· ✓ ·" where a result lands; "≡" where a scroll lands on the stack.
    void Spark(double x, double y, string mark) => AddFx("spark", x, y, mark, 0.7);

    // A puff of smoke where a failed bee came down.
    void Puff(double x, double y) => AddFx("puff", x, y, "~", 1.4);

    // Something a bee saved: its cell flashes honey and "+lesson" rises from it.
    void Deposited(string bee, string type)
    {
        var i = bees.FindIndex(b => b.Name.Equals(bee, StringComparison.OrdinalIgnoreCase)) + 1;
        if (slots.Count == 0 || i <= 0 || i >= slots.Count) return;
        var (x, y) = slots[i];
        AddFx("lit", x, y, "", 1.6);
        AddFx("saved", x, y - hexR * 0.55, "+" + type, 1.8);
    }

    // A result reached the hive (the inbox wipes its item in as it arrives).
    void Delivered(string taskId) => Arrived(taskId);

    // ---- painting -----------------------------------------------------------

    void PaintScene(nint cr, int w, int h)
    {
        if (slots.Count == 0) return;
        var smooth = Smooth;
        var t = drawn;
        var step = smooth ? Frame(t) : 0;
        N.cairo_save(cr);
        N.cairo_scale(cr, sceneScale, sceneScale);
        // Flight paths first, under the cells and stations.
        foreach (var s in sprites.Values) PaintPath(cr, s);
        for (int i = 0; i < slots.Count; i++) PaintCell(cr, i, t);
        foreach (var st in stations) PaintStation(cr, st, step, smooth);
        foreach (var s in sprites.Values)
        {
            if (s.State is "rest" or "deliver" && !s.Flying) continue; // drawn in its cell (or a lit cell says ❯ talking)
            var fail = s.State == "fail";
            Ink.Text(cr, s.Body.X, s.Body.Y, SpriteMarkup(fail ? Palette.Ghost : Palette.Dim, fail ? Palette.Faint : Palette.Pulse), 13, 0.5);
            if (s.Prop.Shown) Ink.Text(cr, s.Prop.X, s.Prop.Y, Ink.C(fail ? Palette.Red : Palette.Pulse, s.Prop.Text), 11);
        }
        foreach (var fx in effects) PaintEffect(cr, fx, t);
        N.cairo_restore(cr);
    }

    static string SpriteMarkup(uint wings, uint body) => Ink.C(wings, "ʚ") + Ink.C(body, "●") + Ink.C(wings, "ɞ");

    // A cell: the Queen's, a resting bee's (its sprite in it), one whose bee
    // is out (dashed, "▸ bench"), one talking with you (lit), or "+ new".
    void PaintCell(nint cr, int i, double t)
    {
        var (x, y) = slots[i];
        var k = hexR / CellR;
        if (i == 0)
        {
            // Lit, "❯ talking", while her turn in the work pane runs.
            Ink.Hex(cr, x, y, hexR, queenLit ? Palette.TalkCell : Palette.QueenCell, queenLit ? Palette.Pulse : Palette.Honey, queenLit ? 1.5 : 1.25);
            Ink.Text(cr, x, y - 10 * k, Ink.C(Palette.Pulse, "♛"), 14 * k, 0.5);
            Ink.Text(cr, x, y + 8 * k, Ink.C(Palette.Bright, "Queen"), 12 * k, 0.5, bold: true);
            Ink.Text(cr, x, y + 23 * k, queenLit ? Ink.C(Palette.Pulse, "❯ talking") : Ink.C(Palette.Dim, "main"), 10 * k, 0.5);
            return;
        }
        if (i > bees.Count)
        {
            Ink.Hex(cr, x, y, hexR, 0, Palette.Line, 1, dashed: true, filled: false);
            Ink.Text(cr, x, y + 4 * k, Ink.C(Palette.Dim, "+ new"), 12 * k, 0.5);
            return;
        }
        var bee = bees[i - 1].Name;
        var s = sprites.GetValueOrDefault(bee);
        var name = bee.Length > 11 ? bee[..10] + "…" : bee;
        var flash = 0.0;
        foreach (var fx in effects)
            if (fx.Kind == "lit" && Math.Abs(fx.X - x) < 1 && Math.Abs(fx.Y - y) < 1) flash = Math.Max(flash, 1 - (t - fx.Born) / fx.Life);
        if (s?.State == "talk")
        {
            Ink.Hex(cr, x, y, hexR, Palette.TalkCell, Palette.Pulse, 1.5);
            Ink.Text(cr, x, y - 4 * k, Ink.C(Palette.Bright, name), 11 * k, 0.5, bold: true);
            Ink.Text(cr, x, y + 14 * k, Ink.C(Palette.Pulse, "❯ talking"), 10 * k, 0.5);
            return;
        }
        if (s == null || s.State is "rest" or "deliver")
        {
            Ink.Hex(cr, x, y, hexR, Palette.Cell, flash > 0 ? Palette.Honey : Palette.Border, 1 + flash * 0.5);
            Ink.Text(cr, x, y - 4 * k, Ink.C(Palette.Text, name), 11 * k, 0.5);
            // Its sprite rests here, or flies home to it.
            var (sx, sy) = s is { Flying: true } ? (s.Body.X, s.Body.Y) : (x, y + 16 * k);
            Ink.Text(cr, sx, sy, SpriteMarkup(Palette.Dim, Palette.Pulse), 13 * (s is { Flying: true } ? 1 : k), 0.5);
            return;
        }
        Ink.Hex(cr, x, y, hexR, 0, Palette.Flight, 1, dashed: true, filled: false);
        Ink.Text(cr, x, y - 4 * k, Ink.C(Palette.Text, name), 11 * k, 0.5);
        Ink.Text(cr, x, y + 14 * k, Ink.C(Palette.Dim, "▸ " + s.Station), 10 * k, 0.5);
    }

    // The dashed path from a bee's cell to where it is (or is flying to).
    void PaintPath(nint cr, Sprite s)
    {
        if (s.State is "rest" or "deliver" && !s.Flying) return;
        var i = CellOf(s.Bee);
        if (i <= 0 || i >= slots.Count) return;
        var (hx, hy) = Home(s.Bee);
        var (ex, ey) = s.Flying ? (s.ToX, s.ToY) : (s.X, s.Y);
        if (s.Flying && (s.OnPath || s.State is "rest" or "deliver")) (hx, hy, ex, ey) = (s.FromX, s.FromY, s.ToX, s.ToY);
        if (Math.Abs(ex - hx) < 2 && Math.Abs(ey - hy) < 2) return;
        var (cx, cy) = slots[i];
        N.cairo_new_path(cr);
        var started = false;
        for (int n = 0; n <= 40; n++)
        {
            var (x, y) = Along(hx, hy - 4, ex - 12, ey - 4, n / 40.0);
            // Leave out the part inside the bee's own cell.
            if (Math.Abs(x - cx) < hexR * 0.87 && Math.Abs(y - cy) < hexR * 0.9) continue;
            if (!started) { N.cairo_move_to(cr, x, y); started = true; }
            else N.cairo_line_to(cr, x, y);
        }
        if (started) Ink.Stroke(cr, Palette.Flight, 1, 2, 5);
    }

    // A station: its frame (honey while a bee is there), its title in the
    // border, its prop, and along the bottom the bees there and what the
    // first one does, or what the station is for.
    void PaintStation(nint cr, Station st, long step, bool smooth)
    {
        var lit = st.Here.Count > 0;
        Ink.Box(cr, st.X, st.Y, Station.W, Station.H, lit ? Palette.Flight : Palette.Line);
        var titleW = Ink.Width(cr, st.Name, 12) + 12;
        Ink.Fill(cr, st.X + 8, st.Y - 7, titleW, 14, Palette.Panel);
        Ink.Text(cr, st.X + 14, st.Y + 4, Ink.C(lit ? Palette.Bright : Palette.Text, st.Name), 12);
        var mx = st.X + Station.W / 2;
        var y = st.Y;
        var live = lit && smooth;
        switch (st.Name)
        {
            case Library:
                // Two shelves of books; while a bee reads, one book is lifted along the shelf.
                const string top = "▐▌▌▐▌▐▐▌▌▐▌▐▌", bottom = "▐▌▐▌▌▐▌▐▌▐▐▌▌";
                var at = live ? (int)(step / 5 % 13) : -1;
                Ink.Text(cr, mx, y + 36, Shelf(top, lit ? Palette.Honey : Palette.Faint, at), 13, 0.5);
                Ink.Text(cr, mx, y + 52, Shelf(bottom, lit ? Palette.Amber : Palette.Ghost, -1), 13, 0.5);
                Ink.Text(cr, mx, y + 66, Ink.C(Palette.Border, "▔▔▔▔▔▔▔▔▔▔▔▔▔"), 13, 0.5);
                break;
            case Web:
                // A dish; while a bee searches its rays turn.
                var r = live ? (int)(step / 4 % 4) : -1;
                uint Ray(int k) => r == k ? Palette.Pulse : lit ? Palette.Honey : Palette.Ghost;
                Ink.Text(cr, mx, y + 36, Ink.C(Ray(0), " ╲") + "   " + Ink.C(Ray(1), "│") + "   " + Ink.C(Ray(2), "╱ "), 13, 0.5);
                Ink.Text(cr, mx, y + 52, Ink.C(Ray(3), "──") + "   " + Ink.C(lit ? Palette.Pulse : Palette.Dim, "◎") + "   " + Ink.C(Ray(3), "──"), 13, 0.5);
                Ink.Text(cr, mx, y + 68, Ink.C(Ray(2), " ╱") + "   " + Ink.C(Ray(1), "│") + "   " + Ink.C(Ray(0), "╲ "), 13, 0.5);
                break;
            case Bench:
                // A workpiece in a vice; while a bee edits, its lines are written out.
                var frame = lit ? Palette.Dim : Palette.Ghost;
                var lines = live ? "≡≡≡ ≡≡"[..(int)(1 + step / 3 % 6)] : "≡≡≡ ≡≡";
                Ink.Text(cr, mx, y + 34, Ink.C(frame, "┌─────────┐"), 13, 0.5);
                Ink.Text(cr, mx, y + 50, Ink.C(frame, "│ ") + Ink.C(lit ? Palette.Pulse : Palette.Faint, lines.PadRight(6)) + Ink.C(frame, "  │"), 13, 0.5);
                Ink.Text(cr, mx, y + 66, Ink.C(frame, "└────┬────┘"), 13, 0.5);
                Ink.Text(cr, mx, y + 82, Ink.C(Palette.Ghost, "═════╧═════"), 13, 0.5);
                break;
            case Tests:
                // Twelve cells; while a bee tests they fill one by one, then show how it went.
                var result = st.Here.Find(s => s.Work?.Log?.Result != null)?.Work?.Log?.Result;
                var filled = !lit ? 7 : !live ? 12 : (int)(step / 2 % 13);
                string Row(int from)
                {
                    var sb = new System.Text.StringBuilder();
                    for (int k = from; k < from + 6; k++)
                    {
                        if (k > from) sb.Append(' ');
                        var on = k < filled;
                        sb.Append(Ink.C(on ? (lit ? Palette.Honey : Palette.Dim) : Palette.Ghost, on ? "⬢" : "⬡"));
                    }
                    return sb.ToString();
                }
                Ink.Text(cr, mx, y + 42, Row(0), 13, 0.5);
                Ink.Text(cr, mx, y + 62, Row(6), 13, 0.5);
                if (result is { } res) Ink.Text(cr, mx, y + 84, res.What == "error" ? Ink.C(Palette.Red, "✗ failed") : Ink.C(Palette.Green, "✓ passed"), 11, 0.5);
                break;
            case Shell:
                // A prompt; while a bee runs a command it shows and the cursor blinks.
                var edge = lit ? Palette.Dim : Palette.Ghost;
                var command = st.Here.Count > 0 && st.Here[0].Work?.Step is { } cs && cs.Tool == "Bash" ? Hive.Text.OneLine(cs.What) : "";
                if (command.Length > 8) command = command[..7] + "…";
                var cursor = !lit || !smooth || (long)(drawn / 0.55) % 2 == 0 ? "_" : " ";
                Ink.Text(cr, mx, y + 38, Ink.C(edge, "┌───────────┐"), 13, 0.5);
                Ink.Text(cr, mx, y + 54, Ink.C(edge, "│ ") + Ink.C(lit ? Palette.Pulse : Palette.Dim, "$") + " "
                    + Ink.C(Palette.Text, command) + Ink.C(lit ? Palette.Pulse : Palette.Faint, cursor) + new string(' ', 8 - command.Length) + Ink.C(edge, "│"), 13, 0.5);
                Ink.Text(cr, mx, y + 70, Ink.C(edge, "└───────────┘"), 13, 0.5);
                break;
            default: // the flowers
                var sway = lit && smooth && (long)(drawn / 1.2) % 2 == 1;
                var heads = lit ? Palette.Pulse : Palette.Dim;
                Ink.Text(cr, mx, y + 42, Ink.C(heads, sway ? "  ✿  ❀  ✿  " : " ✿   ❀   ✿ "), 13, 0.5);
                Ink.Text(cr, mx, y + 58, Ink.C(lit ? Palette.Flight : Palette.Ghost, " │  ╲│╱  │ "), 13, 0.5);
                Ink.Text(cr, mx, y + 72, Ink.C(Palette.Border, "▔▔▔▔▔▔▔▔▔▔▔"), 13, 0.5);
                break;
        }
        // The bottom line: the bees here (their sprites are drawn with the
        // others) and what the first one does; or what the station is for.
        if (!lit)
        {
            Ink.Text(cr, st.X + 16, y + 112, Ink.C(Palette.Faint, st.Idle), 11);
            return;
        }
        var first = st.Here[0];
        var doing = first.State switch
        {
            "fail" => "✗ failed",
            "talk" => "waits on you",
            "wait" => "waits for reviewers",
            "think" => "thinking",
            _ => first.Work?.Step is { } s ? Short(s) : first.Work?.Task == null ? "in a session" : "",
        };
        var room = (int)((Station.W - 38 - (st.Here.Count - 1) * 28 - 8) / Ink.Width(cr, "x", 11));
        var who = first.Bee + (st.Here.Count > 1 ? $" +{st.Here.Count - 1}" : "");
        var line = doing.Length > 0 ? " " + doing : "";
        if (who.Length + line.Length > room) line = line[..Math.Max(0, room - who.Length - 1)] + "…";
        Ink.Text(cr, st.X + 38 + (st.Here.Count - 1) * 28, y + 112, Ink.C(Palette.Pulse, who) + Ink.C(Palette.Dim, line), 11);
    }

    // A shelf of books in one colour, one lifted honey at `at`.
    static string Shelf(string books, uint color, int at) =>
        at < 0 ? Ink.C(color, books) : Ink.C(color, books[..at]) + Ink.C(Palette.Pulse, books[at].ToString()) + Ink.C(color, books[(at + 1)..]);

    // A step as the mockup's captions say it: "Edit Scene.cs", "Bash tests/run.sh".
    static string Short(Steps.Step s)
    {
        var what = s.What;
        if (s.Tool is "Read" or "Edit" or "Write" or "MultiEdit") what = Path.GetFileName(what);
        return s.Tool == "think" ? "thinking" : (s.Tool.StartsWith("hive_") ? s.Tool.Replace('_', ' ') + " " : s.Tool + " ") + what;
    }

    void PaintEffect(nint cr, Effect fx, double t)
    {
        var p = Math.Clamp((t - fx.Born) / fx.Life, 0, 1);
        switch (fx.Kind)
        {
            case "spark":
                var gap = new string(' ', (int)(p * 3));
                Ink.Text(cr, fx.X, fx.Y, Ink.C(Palette.Pulse, p < 0.2 ? fx.Text : $"·{gap}{fx.Text}{gap}·"), 12, 0.5);
                break;
            case "puff":
                Ink.Text(cr, fx.X - p * 6, fx.Y - p * 10, Ink.C(Palette.Dim, p < 0.35 ? "~" : p < 0.7 ? "~ ~" : "~ ~ ~"), 11);
                break;
            case "saved":
                Ink.Text(cr, fx.X, fx.Y - p * 18, Ink.C(Palette.Pulse, fx.Text), 10, 0.5);
                break;
        }
    }

    // ---- clicks and labels -------------------------------------------------------

    // Where a bee is in the scene, in pixels, for its popover to point at.
    (double X, double Y, double W, double H) SpriteBox(string bee)
    {
        var (x, y) = sprites.TryGetValue(bee, out var s) ? (s.X, s.Y) : Home(bee);
        return ((x - 14) * sceneScale, (y - 14) * sceneScale, 28 * sceneScale, 18 * sceneScale);
    }

    void ClickScene(double px, double py)
    {
        if (sceneScale <= 0) return;
        var (x, y) = (px / sceneScale, py / sceneScale);
        // A bee out at a station: its sprite.
        foreach (var s in sprites.Values)
            if (s.State != "rest" && Math.Abs(x - s.X) < 16 && y > s.Y - 16 && y < s.Y + 6)
            {
                ShowBeePop(s.Bee);
                return;
            }
        for (int i = 0; i < slots.Count; i++)
        {
            var (cx, cy) = slots[i];
            if (Math.Abs(x - cx) > hexR * 0.87 || Math.Abs(y - cy) > hexR * 0.9) continue;
            if (i == 0) { ShowProject(null); Go("briefs"); Reveal(briefSplit); }
            else if (i <= bees.Count) ShowBeePop(bees[i - 1].Name);
            else NewBee();
            return;
        }
    }

    // A label over each cell (the Queen, the bees, "+ new"): its tooltip,
    // and what a screen reader says for it. The drawing itself says nothing.
    void PlaceCellLabels()
    {
        if (sceneFixed == 0 || slots.Count == 0) return;
        var n = Math.Min(slots.Count, occupied.Count + 1);
        for (int i = 0; i < n; i++)
        {
            if (i == cellLabels.Count)
            {
                var l = N.gtk_label_new("");
                N.gtk_fixed_put(sceneFixed, l, 0, 0);
                cellLabels.Add(l);
            }
            var label = (nint)cellLabels[i];
            var (x, y) = slots[i];
            N.gtk_widget_set_visible(label, true);
            N.gtk_widget_set_size_request(label, (int)(hexR * 1.7 * sceneScale), (int)(hexR * 1.6 * sceneScale));
            N.gtk_fixed_move(sceneFixed, label, (x - hexR * 0.85) * sceneScale, (y - hexR * 0.8) * sceneScale);
            // A bee's cell says what its sprite is doing (one sentence per bee).
            var said = i == occupied.Count ? "New bee"
                : i > 0 && sprites.TryGetValue(occupied[i].Name, out var s) && s.Said.Length > 0 ? s.Said
                : $"{occupied[i].Name}, {occupied[i].Sub}";
            SpeakCell(label, said);
        }
        for (int i = n; i < cellLabels.Count; i++) N.gtk_widget_set_visible((nint)cellLabels[i], false);
    }
}
