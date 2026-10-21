using static Hive.Gui.Ui;

namespace Hive.Gui;

// The work pane, as the work pane mockup has it: the one conversation the
// user has with the Queen, beside the hive, and a tab to watch each bee she
// hands work to (BeeTabs.cs). A ♛ Queen tab (a spinner while she answers),
// the bees' tabs and "new"; the transcript as Claude Code
// shows it (❯ you, ● the Queen, a green ● tool call and ⎿ its result, an
// edit's lines out and in, ✻ while she works, with the time and tokens); a
// ❯ prompt between two rules, and "⏵⏵ mode · model · tokens" under it; the
// folder she works in, picked in the pane's title. Each message is a turn
// (src/Talk.cs): a task that resumes the window's session with her. The
// transcript is read back from the session's own file as it grows.
sealed unsafe partial class App
{
    sealed class TalkTab(string bee)
    {
        public readonly string Bee = bee;
        public BeeTask? From;                    // the task whose session the next message continues
        public string Session = "", Folder = "", Project = "", Access = "edit", Model = "", Echo = "", Pending = "";
        public string Pick = "";                 // the Queen's: the model picked for her turns ("" for the default)
        public string? File;                     // the session's transcript
        public long Offset, Tokens;
        public long Start;                       // where its transcript was first read from (0: the whole of it)
        public readonly List<TalkLine> Lines = [];
        public readonly Dictionary<string, System.Text.Json.Nodes.JsonObject> Tools = [];
        // A bee's tab: its subagent's id, what the Queen asked, when it
        // started, and how it ended ("" while it works, done, failed, stopped).
        public string Agent = "", Call = "", What = "", State = ""; // Call: the Agent call that started it
        public DateTime Started;
        public nint Chip;                        // its tab's glyph
        public bool Typed;                       // its tab's name has typed in
    }

    TalkTab talk = null!;                        // the Queen's
    TalkTab shown = null!;                       // the tab the pane shows: hers or a bee's
    string queenSince = "";                      // when the user last pressed "new" (Tasks.QueenSince)
    nint talkPanel, talkChip, talkQueenTab, talkTabs, talkPrompt, talkStatus, talkWatch, talkWatchText, talkWhere, talkLines, talkScroll, talkBusy, talkStar, talkBusyText, talkBusyMeta, talkEntry, talkMode, talkModel, talkTokens, talkFolder, talkFolders;
    nint talkModelButton, talkModelPop;
    nint[] talkModelChoices = [];                // the model menu's labels: default, then Tasks.QueenModels
    List<string> talkFolderChoices = [];         // the projects' folders, after ~
    List<string> talkFolderProjects = [];        // and their projects
    bool talkMoving;
    int talkShown;                               // how many of the tab's lines the box shows
    string talkBusySaid = "";
    Func<double, bool> talkStep = null!;

    static readonly string[] AccessModes = ["read only", "accept edits on", "bypass permissions on"];

    const int TalkKeep = 300; // the lines a tab keeps and shows
    const int TailBytes = 64 * 1024; // a transcript opened at its end: read back this much at a time
    readonly List<TalkLine> talkQueued = []; // your lines "(sent when she's done)" not sent yet
    bool earlierEnds;                        // the Queen's transcript before its Start was looked through for how bees ended

    const string Queued = "  (sent when she's done)";

    // A new hive's first step, at the top of the Queen's tab while there are no bees.
    const string NoBees = "No bees yet. Click + new in the hive to make one, or run `hive new-bee <Name> --specialty \"...\"`.";
    bool talkNoBees; // the Queen's tab shows NoBees

    // The pane: ┤ ♛ queen ├ in the honey frame, the folder at its right.
    nint BuildTalk()
    {
        var title = Css(ArtLabel(), "panel-title");
        N.gtk_label_set_markup(title, QueenTitle);
        talkChip = Css(ArtLabel(), "talk-glyph");
        talkQueenTab = Css(N.gtk_button_new(), "talk-chip", "on");
        N.gtk_button_set_child(talkQueenTab, Box(Horizontal, 8, talkChip, N.gtk_label_new("Queen")));
        On(talkQueenTab, "clicked", () => ShowTab(talk));
        Said(talkQueenTab, "The Queen");
        talkTabs = Box(Horizontal, 22); // the bees' tabs
        var fresh = Css(TextButton("+ new", NewQueen), "talk-new");
        Said(fresh, "A new conversation with the Queen");
        N.gtk_widget_set_tooltip_text(fresh, "Start a new conversation with the Queen; finished bee tabs close");
        var tabs = Css(Box(Horizontal, 22, talkQueenTab, talkTabs, Expand(N.gtk_box_new(Horizontal, 0), true, false), fresh), "talk-tabs");
        talkLines = Box(Vertical, 2);
        talkScroll = N.gtk_scrolled_window_new();
        N.gtk_scrolled_window_set_policy(talkScroll, 2, 1); // never sideways
        N.gtk_scrolled_window_set_child(talkScroll, Margin(talkLines, 16, 24, 8, 24));
        N.gtk_widget_set_size_request(talkScroll, -1, 200);
        Expand(talkScroll); // the pane's height, whatever the transcript's
        // ✻ Running the tests… (12m 03s · ↓ 48.2k tokens · esc to stop)
        talkStar = Css(ArtLabel("✻"), "honey");
        talkBusyText = Css(ArtLabel(), "honey");
        talkBusyMeta = Css(N.gtk_label_new(""), "faint");
        talkBusy = Margin(Box(Horizontal, 10, talkStar, Box(Horizontal, 6, talkBusyText, talkBusyMeta)), 12, 0, 0, 0);
        N.gtk_widget_set_visible(talkBusy, false);
        N.gtk_box_append(talkLines, talkBusy);
        talkEntry = Css(N.gtk_entry_new(), "talk-input", "bare");
        Expand(talkEntry, true, false);
        N.gtk_entry_set_placeholder_text(talkEntry, "Message the Queen…");
        On(talkEntry, "activate", SendTalk);
        OnKey(talkEntry, (key, state) =>
        {
            if (key == 0xfe20 || key == 0xff09 && (state & 1) != 0) { CycleMode(); return true; } // shift+tab
            if (key == 0xff1b) { StopTalk(); return true; } // esc
            return VoiceKey(key, state); // alt+v dictates
        });
        OnKeyUp(talkEntry, VoiceKeyUp);
        var glyph = Css(ArtLabel("❯"), "honey");
        var (mic, voice) = BuildDictation(glyph);
        var prompt = Css(Box(Horizontal, 10, glyph, talkEntry, voice, mic), "talk-prompt");
        talkPrompt = Margin(prompt, 0, 24, 0, 24);
        talkMode = Css(N.gtk_label_new(""), "small");
        talkModel = Css(N.gtk_label_new(""), "small", "dim-label");
        talkTokens = Css(N.gtk_label_new(""), "small", "faint");
        // Long lines shorten with … instead of widening the pane past its 38%.
        foreach (var l in new[] { talkMode, talkModel, talkTokens, talkBusyText, talkBusyMeta }) N.gtk_label_set_ellipsize(l, 3);
        talkStatus = Css(Box(Horizontal, 14, talkMode, Css(N.gtk_label_new("·"), "small", "ghost"), BuildModelPicker(), Expand(N.gtk_box_new(Horizontal, 0), true, false), talkTokens), "talk-status");
        // A bee's tab has no prompt: ◉ watching it, and ✕ close.
        talkWatchText = Css(N.gtk_label_new(""), "small", "dim-label");
        N.gtk_label_set_xalign(talkWatchText, 0);
        N.gtk_label_set_wrap(talkWatchText, true);
        var close = Css(Button("✕ close", () => CloseBeeTab(shown), "flat"), "small");
        Said(close, "Close this tab");
        N.gtk_widget_set_valign(close, Center);
        talkWatch = Css(Box(Horizontal, 10, Css(ArtLabel("◉"), "honey"), Expand(talkWatchText, true, false), close), "talk-watch");
        N.gtk_widget_set_visible(talkWatch, false);
        var inside = Box(Vertical, 0, tabs, talkScroll, talkPrompt, talkStatus, talkWatch);
        talkPanel = PanelWith(title, inside, ["term"]); // its own title, not one typed in
        Said(N.gtk_overlay_get_child(talkPanel), "queen");
        (talkFolder, talkFolders) = StringDropDown("~");
        N.gtk_widget_set_tooltip_text(talkFolder, "The folder the Queen works in");
        On(talkFolder, "notify::selected", PickFolder);
        talkWhere = N.gtk_label_new(""); // a bee's tab: the bee and where it works
        N.gtk_widget_set_visible(talkWhere, false);
        PanelRight(talkPanel, Box(Horizontal, 0, talkWhere, talkFolder));
        talkStep = TalkStep;

        // The conversation the window had with her, until the user says "new".
        queenSince = Tasks.QueenSince(store);
        talk = shown = new TalkTab(Tasks.Queen);
        beeTabsSkipped.UnionWith(Tasks.QueenClosed(store)); // the bee tabs closed in this conversation
        var from = Tasks.QueenSession(Tasks.List(store, Tasks.Queen), queenSince);
        talk.From = from;
        talk.Session = from?.Doc.Get("session") ?? "";
        talk.Access = from?.Doc.Get("access") is { Length: > 0 } a && Tasks.Access.Contains(a) ? a : "edit";
        talk.Project = from?.Doc.Get("project") ?? "";
        talk.Folder = from?.Doc.Get("folder") ?? FolderOf(talk.Project);
        talk.Pick = Tasks.QueenModel(store);
        ReadTranscript(talk);
        ShowTalkTab();
        return Expand(talkPanel);
    }

    // The model in the status line, as a menu: default (no --model, the
    // user's own) or one of Tasks.QueenModels, for the Queen's next turns.
    // The pick is kept with the conversation, over a restart, until "new".
    nint BuildModelPicker()
    {
        var list = Box(Vertical, 0);
        talkModelChoices = new nint[Tasks.QueenModels.Length + 1];
        for (int i = 0; i <= Tasks.QueenModels.Length; i++)
        {
            var model = i == 0 ? "" : Tasks.QueenModels[i - 1];
            var b = Css(N.gtk_button_new(), "talk-model-choice");
            talkModelChoices[i] = N.gtk_label_new("");
            N.gtk_label_set_xalign(talkModelChoices[i], 0);
            N.gtk_button_set_child(b, talkModelChoices[i]);
            On(b, "clicked", () => PickModel(model));
            N.gtk_box_append(list, b);
        }
        talkModelPop = N.gtk_popover_new();
        N.gtk_popover_set_has_arrow(talkModelPop, false);
        N.gtk_popover_set_child(talkModelPop, Margin(list, 4, 4, 4, 4));
        talkModelButton = Css(N.gtk_menu_button_new(), "talk-model");
        N.gtk_menu_button_set_child(talkModelButton, Box(Horizontal, 4, talkModel, Css(ArtLabel("▾"), "small", "ghost")));
        N.gtk_menu_button_set_popover(talkModelButton, talkModelPop);
        N.gtk_widget_set_tooltip_text(talkModelButton, "The model the Queen answers with, from your next message");
        return talkModelButton;
    }

    void PickModel(string model)
    {
        N.gtk_popover_popdown(talkModelPop);
        if (model == talk.Pick) return;
        talk.Pick = model;
        Tasks.SetQueenModel(store, model);
        ShowMode();
        Say(model.Length > 0 ? $"The Queen answers with {Tasks.QueenModelNames[Array.IndexOf(Tasks.QueenModels, model)]} from your next message."
                             : "The Queen answers with your default model from your next message.");
    }

    // A project's folder, or ~ for none.
    string FolderOf(string project) =>
        Hive.Text.Tilde(store.FindProject(project)?.Paths.FirstOrDefault() is { } path ? Hive.Text.Expand(path) : Hive.Text.Home);

    // The folder picker's choices: ~, then each project's folder.
    void ShowFolders()
    {
        var choices = new List<string>();
        var names = new List<string>();
        foreach (var p in projects)
            if (p.Paths.Count > 0 && Hive.Text.Tilde(Hive.Text.Expand(p.Paths[0])) is var f && f != "~" && !choices.Contains(f))
            {
                choices.Add(f);
                names.Add(p.Name);
            }
        talkFolderProjects = names;
        SetChoices(talkFolder, talkFolders, ref talkFolderChoices, choices);
        updatingChoices = true;
        N.gtk_drop_down_set_selected(talkFolder, (uint)(talkFolderChoices.IndexOf(talk.Folder) + 1));
        updatingChoices = false;
    }

    // A folder picked: the Queen works there from her next turn, in the same
    // conversation (a resumed session runs wherever it's resumed; its
    // transcript stays in the file it started in).
    void PickFolder()
    {
        if (updatingChoices) return;
        var i = (int)N.gtk_drop_down_get_selected(talkFolder);
        var folder = i > 0 && i <= talkFolderChoices.Count ? talkFolderChoices[i - 1] : "~";
        if (folder == talk.Folder) return;
        talk.Project = i > 0 && i <= talkFolderProjects.Count ? talkFolderProjects[i - 1] : "";
        talk.Folder = folder;
        if (talk.Lines.Count == 0) ShowTalkTab(); // its first line says where she'll start
        else Say($"The Queen works in {folder} from your next message.");
    }

    // "new": a fresh conversation with the Queen, after a restart too.
    void NewQueen()
    {
        if (TalkBusy() != null)
        {
            Say("The Queen is still answering; press esc to stop her first.");
            return;
        }
        queenSince = Tasks.NewQueenSession(store);
        ClearBeeTabs();
        Fx.DrawIn(talkPanel); // a fresh conversation: the pane draws itself in again
        talk.From = null;
        talk.Session = talk.Echo = talk.Pending = talk.Model = talk.Pick = ""; // NewQueenSession forgot the model picked
        talk.File = null;
        talk.Offset = talk.Tokens = 0;
        talk.Lines.Clear();
        talk.Tools.Clear();
        ShowTalkTab();
        N.gtk_widget_grab_focus(talkEntry);
    }

    // Another tab shown.
    void ShowTab(TalkTab tab)
    {
        shown = tab;
        if (tab == talk) N.gtk_widget_add_css_class(talkQueenTab, "on");
        else N.gtk_widget_remove_css_class(talkQueenTab, "on");
        RebuildBeeChips();
        ShowTalkTab();
    }

    // The tab shown: its transcript, where it runs, the mode and the model
    // (the Queen's), or what the bee works on (a bee's).
    void ShowTalkTab()
    {
        var tab = shown;
        var queen = tab == talk;
        N.gtk_widget_set_visible(talkPrompt, queen);
        N.gtk_widget_set_visible(talkStatus, queen);
        N.gtk_widget_set_visible(talkWatch, !queen);
        N.gtk_widget_set_visible(talkFolder, queen);
        N.gtk_widget_set_visible(talkWhere, !queen);
        if (!queen) N.gtk_label_set_text(talkWhere, $"{tab.Bee} · {Hive.Text.Tilde(Hive.Text.Expand(tab.Folder))}");
        for (var c = N.gtk_widget_get_first_child(talkLines); c != 0;)
        {
            var next = N.gtk_widget_get_next_sibling(c);
            if (c != talkBusy) N.gtk_box_remove(talkLines, c);
            c = next;
        }
        talkShown = 0;
        talkNoBees = queen && bees.Count == 0;
        if (talkNoBees) AddTalkLine(new TalkLine("plain", "", NoBees), false);
        if (tab.Lines.Count == 0)
        {
            AddTalkLine(new TalkLine("plain", "", !queen ? $"ʚ●ɞ  {tab.Bee} is starting on it for the Queen."
                : tab.Session.Length > 0 ? "♛  Say something and the Queen carries on in your last conversation."
                : $"♛  Say something and the Queen starts a conversation in {Hive.Text.Tilde(Hive.Text.Expand(tab.Folder))}, with your hive's briefing. She hands work to the bees."), false);
            talkShown = 0;
        }
        else ShowNewLines(false);
        ShowMode();
        ScrollTalkDown(force: true);
    }

    void ShowMode()
    {
        if (shown != talk)
        {
            var b = shown;
            N.gtk_label_set_text(talkWatchText, b.State switch
            {
                "done" => $"{b.Bee} is done; its report went to the Queen",
                "failed" => $"{b.Bee} failed; the Queen has what went wrong",
                "stopped" => $"{b.Bee} stopped with the Queen's turn",
                _ => $"watching {b.Bee} · it works for the Queen; tell the Queen to change its course",
            });
            return;
        }
        var tab = talk;
        N.gtk_label_set_markup(talkMode, Palette.Span(Palette.Pulse, "⏵⏵ " + AccessModes[Math.Max(0, Array.IndexOf(Tasks.Access, tab.Access))]) + Palette.Span(Palette.Dim, " (shift+tab to cycle)"));
        // The pick, or with none what her transcript says she runs on.
        var picked = Array.IndexOf(Tasks.QueenModels, tab.Pick);
        N.gtk_label_set_text(talkModel, (picked >= 0 ? Tasks.QueenModelNames[picked] : tab.Model.Length > 0 ? Transcript.ModelName(tab.Model) : "Queen"));
        for (int i = 0; i < talkModelChoices.Length; i++)
            N.gtk_label_set_text(talkModelChoices[i], (i == picked + 1 ? "✓ " : "  ") + (i == 0 ? "default" : Tasks.QueenModelNames[i - 1]));
        N.gtk_label_set_text(talkTokens, tab.Tokens > 0 ? $"↓ {Tokens(tab.Tokens)} tokens" : "");
    }

    static string Tokens(long n) => n >= 1000 ? $"{n / 1000.0:0.0}k" : n.ToString();

    // shift+tab: what the next message may do.
    void CycleMode()
    {
        var tab = talk;
        tab.Access = Tasks.Access[(Array.IndexOf(Tasks.Access, tab.Access) + 1) % Tasks.Access.Length];
        ShowMode();
    }

    // The lines of its transcript not shown yet, fading in one after another.
    void ShowNewLines(bool fade)
    {
        var tab = shown;
        var start = Math.Max(talkShown, tab.Lines.Count - TalkKeep);
        var added = new List<long>();
        for (int i = start; i < tab.Lines.Count; i++) added.Add(AddTalkLine(tab.Lines[i], fade));
        talkShown = tab.Lines.Count;
        // The oldest go once there are more than TalkKeep.
        var n = 0;
        for (var c = N.gtk_widget_get_first_child(talkLines); c != 0; c = N.gtk_widget_get_next_sibling(c)) n++;
        while (n-- > TalkKeep + 1 && N.gtk_widget_get_first_child(talkLines) is var first && first != talkBusy) N.gtk_box_remove(talkLines, first);
        if (fade && added.Count > 0 && Smooth)
        {
            var at = Clock;
            var rows = new nint[added.Count];
            for (int i = 0; i < rows.Length; i++) rows[i] = (nint)added[i];
            foreach (var r in rows) N.gtk_widget_set_opacity(r, 0);
            Animate(t =>
            {
                var all = true;
                for (int i = 0; i < rows.Length; i++)
                {
                    var a = Math.Clamp((t - at - i * 0.06) / 0.3, 0, 1);
                    N.gtk_widget_set_opacity(rows[i], a);
                    all &= a >= 1;
                }
                return !all;
            }, rows);
        }
    }

    // A line of the transcript, as Claude Code draws it.
    nint AddTalkLine(TalkLine line, bool fade)
    {
        var (glyph, glyphColor, color) = line.Kind switch
        {
            "you" => ("❯", Palette.Dim, Palette.Text),
            "queen" => ("♛", Palette.Pulse, Palette.Text), // in a bee's tab: what the Queen asked it
            "say" => ("●", Palette.Bright, Palette.Text),
            "tool" => ("●", Palette.Green, Palette.Dim),
            "out" => ("⎿", Palette.Ghost, Palette.Dim),
            "del" => ("-", Palette.Red, 0xD9A397u),
            "add" => ("+", Palette.Green, 0xB3D1ADu),
            "err" => ("⎿", Palette.Ghost, Palette.Red),
            _ => ("", Palette.Ghost, Palette.Text),
        };
        var mark = ArtLabel();
        N.gtk_label_set_markup(mark, Palette.Span(glyphColor, glyph));
        N.gtk_widget_set_size_request(mark, 8, -1);
        N.gtk_widget_set_valign(mark, Start);
        var text = N.gtk_label_new("");
        var markup = (line.Bold.Length > 0 ? $"<span foreground=\"#{Palette.Bright:X6}\" weight=\"bold\">{Markup(line.Bold)}</span>" : "")
            + (line.Kind is "say" or "plain" ? Md.Inline(line.Text, color) : Palette.Span(color, line.Text));
        N.gtk_label_set_markup(text, markup);
        N.gtk_label_set_xalign(text, 0);
        N.gtk_label_set_wrap(text, true);
        N.gtk_label_set_wrap_mode(text, 2);
        N.gtk_label_set_selectable(text, line.Kind is "say" or "plain" or "you" or "queen");
        var row = Css(Box(Horizontal, 10, mark, Expand(text, true, false)), "talk-line", "talk-" + line.Kind);
        Said(row, (line.Kind switch { "you" => "You: ", "queen" => "The Queen: ", "say" => shown.Bee + ": ", "tool" => "Tool: ", "err" => "Error: ", _ => "" }) + line.Bold + line.Text);
        // Above the ✻ line, which stays last.
        if (N.gtk_widget_get_prev_sibling(talkBusy) is var last and not 0) N.gtk_box_insert_child_after(talkLines, row, last);
        else N.gtk_box_prepend(talkLines, row);
        return row;
    }

    // The transcript's new records, from where it was last read. The first
    // time, from its end: only as far back as the lines a tab keeps (Tail).
    // A tool call from before that is looked up when its result comes.
    void ReadTranscript(TalkTab tab)
    {
        tab.File ??= Transcript.Find(tab.Session);
        if (tab.File == null) return;
        string added;
        var first = tab.Offset == 0;
        if (first) tab.Start = 0;
        try
        {
            using var f = new FileStream(tab.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (f.Length <= tab.Offset) return;
            var at = tab.Offset;
            byte[] bytes;
            var skip = 0;
            if (first && f.Length > TailBytes)
            {
                (at, bytes) = Tail(f, tab);
                if (at > 0) skip = Array.IndexOf(bytes, (byte)'\n') + 1; // from the first whole line
                tab.Start = at + skip;
                if (tab == talk) earlierEnds = false;
            }
            else
            {
                f.Seek(at, SeekOrigin.Begin);
                bytes = new byte[f.Length - at];
                bytes = bytes[..f.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false)];
            }
            var end = Array.LastIndexOf(bytes, (byte)'\n'); // whole lines only
            if (end < skip) return;
            tab.Offset = at + end + 1;
            added = System.Text.Encoding.UTF8.GetString(bytes, skip, end - skip);
        }
        catch (IOException) { return; }
        var model = tab.Model;
        var tokens = tab.Tokens;
        var before = tab.Lines.Count;
        var folder = Hive.Text.Expand(tab.Folder);
        Func<string, System.Text.Json.Nodes.JsonObject?>? earlier = !first && tab.Start > 0 ? id => EarlierCall(tab, id) : null;
        foreach (var line in added.Split('\n'))
        {
            Transcript.Read(line, tab.Lines, tab.Tools, ref tokens, ref model, folder, subagent: tab != talk, agents: tab != talk ? beeCalls : null, earlier: earlier);
            if (tab == talk) Transcript.AgentEnd(line, agentEnds); // how the bees she started ended
        }
        (tab.Model, tab.Tokens) = (model, tokens);
        // What this window just sent comes back from the session too: shown once.
        for (int i = before; i < tab.Lines.Count; i++)
            if (tab.Echo.Length > 0 && tab.Lines[i].Kind == "you" && tab.Lines[i].Text == tab.Echo)
            {
                tab.Lines.RemoveAt(i);
                tab.Echo = "";
                if (tab == shown && talkShown > i) talkShown--;
                break;
            }
        // Only the lines the pane shows are kept (a tool call still waiting
        // for its result stays in Tools): a long conversation's oldest go.
        if (tab.Lines.Count > TalkKeep)
        {
            var drop = tab.Lines.Count - TalkKeep;
            tab.Lines.RemoveRange(0, drop);
            if (tab == shown) talkShown = Math.Max(0, talkShown - drop);
        }
    }

    // A transcript's end, read back from its last byte only as far as it
    // takes to give the lines a tab keeps: the bytes, and where they start.
    (long At, byte[] Bytes) Tail(FileStream f, TalkTab tab)
    {
        var at = f.Length;
        byte[] bytes = [];
        while (at > 0)
        {
            var start = Math.Max(0, at - Math.Max(TailBytes, f.Length - at)); // twice as far back each time
            var more = new byte[at - start];
            f.Seek(start, SeekOrigin.Begin);
            f.ReadExactly(more);
            bytes = [.. more, .. bytes];
            at = start;
            var from = Array.IndexOf(bytes, (byte)'\n') + 1;
            var end = Array.LastIndexOf(bytes, (byte)'\n');
            if (end <= from) continue;
            List<TalkLine> lines = [];
            Dictionary<string, System.Text.Json.Nodes.JsonObject> tools = [];
            long tokens = 0;
            var model = "";
            foreach (var line in System.Text.Encoding.UTF8.GetString(bytes, from, end - from).Split('\n'))
                Transcript.Read(line, lines, tools, ref tokens, ref model, subagent: tab != talk);
            if (lines.Count >= TalkKeep) break;
        }
        return (at, bytes);
    }

    // The records of a transcript from before where it was first read from.
    string[] Earlier(TalkTab tab)
    {
        try
        {
            using var f = new FileStream(tab.File!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[tab.Start];
            f.ReadExactly(bytes);
            return System.Text.Encoding.UTF8.GetString(bytes).Split('\n');
        }
        catch (IOException) { return []; }
    }

    // A tool call from before where its transcript was first read from,
    // when its result comes: so an edit's result still shows its diff.
    System.Text.Json.Nodes.JsonObject? EarlierCall(TalkTab tab, string id)
    {
        List<TalkLine> none = [];
        Dictionary<string, System.Text.Json.Nodes.JsonObject> tools = [];
        long tokens = 0;
        var model = "";
        foreach (var line in Earlier(tab))
            if (line.Contains($"\"id\":\"{id}\"", StringComparison.Ordinal))
                Transcript.Read(line, none, tools, ref tokens, ref model, subagent: tab != talk);
        return tools.GetValueOrDefault(id);
    }

    // The Queen's turn answering now, if any (one since the last "new").
    BeeTask? TalkBusy()
    {
        foreach (var t in allTasks)
            if (t.Running && Tasks.InConversation(t, queenSince))
                return t;
        return null;
    }

    // After a refresh or a new step: the conversation's new lines, its
    // session once a fresh one has one, and whether she's still answering.
    void UpdateTalk()
    {
        var tab = talk;
        ShowFolders();
        // Her newest turn with a session is what the next message continues.
        if (Tasks.QueenSession(allTasks, queenSince) is { } t)
        {
            var s = t.Doc.Get("session");
            if (s != tab.Session)
            {
                // A fresh conversation's first session keeps what the pane
                // shows (your message comes back as Echo); another one starts over.
                if (tab.Session.Length > 0)
                {
                    tab.Lines.Clear(); tab.Tools.Clear(); tab.Tokens = 0;
                    if (shown == tab) talkShown = 0;
                }
                tab.Session = s; tab.File = null; tab.Offset = 0;
            }
            tab.From = t;
        }
        ReadTranscript(tab);
        UpdateBeeTabs();
        // A message sent while she answered goes once she's done.
        if (tab.Pending.Length > 0 && TalkBusy() == null)
        {
            var said = tab.Pending;
            tab.Pending = "";
            talkQueued.Clear();
            Send(tab, said);
        }
        var atBottom = AtBottom();
        if (talkShown == 0 && shown.Lines.Count > 0 || shown == talk && talkNoBees != (bees.Count == 0)) ShowTalkTab();
        else ShowNewLines(true);
        ShowMode();
        ScrollTalkDown(atBottom);
        DrawTalk(Clock);
        StartTalk();
    }

    void SendTalk()
    {
        var tab = talk;
        var said = Text(talkEntry).Trim();
        if (said.Length == 0) return;
        N.gtk_editable_set_text(talkEntry, "");
        if (TalkBusy() != null)
        {
            tab.Pending = tab.Pending.Length > 0 ? tab.Pending + "\n\n" + said : said;
            var line = new TalkLine("you", "", said + Queued);
            talkQueued.Add(line);
            AddLine(tab, line);
            return;
        }
        AddLine(tab, new TalkLine("you", "", said));
        Send(tab, said);
    }

    void AddLine(TalkTab tab, TalkLine line)
    {
        if (shown != tab) ShowTab(tab);
        if (talkShown == 0 && tab.Lines.Count == 0) ShowTalkTab();
        tab.Lines.Add(line);
        ShowNewLines(true);
        ScrollTalkDown(force: true);
    }

    // A turn: the message as a task that resumes the conversation (or starts one).
    void Send(TalkTab tab, string said)
    {
        // Its project removed since: she carries on in ~.
        if (tab.Project.Length > 0 && store.FindProject(tab.Project) == null) (tab.Project, tab.Folder) = ("", "~");
        var task = Tasks.QueenTalk(store, said, tab.Project, tab.Access, tab.From, tab.Pick);
        Tasks.Start(task);
        tab.Echo = said;
        RefreshHive();
    }

    // esc: the Queen stops answering.
    void StopTalk()
    {
        if (TalkBusy() is not { } t) return;
        Tasks.Stop(store, t.Id);
        talk.Pending = "";
        // What waited for her turn's end won't go: its lines say so.
        foreach (var line in talkQueued)
            if (talk.Lines.IndexOf(line) is var i and >= 0) talk.Lines[i] = new TalkLine("you", "", line.Text[..^Queued.Length] + "  (not sent)");
        if (talkQueued.Count > 0 && shown == talk) ShowTalkTab();
        talkQueued.Clear();
        Say("The Queen stopped.");
        RefreshHive();
    }

    bool AtBottom()
    {
        var adj = N.gtk_scrolled_window_get_vadjustment(talkScroll);
        return N.gtk_adjustment_get_value(adj) >= N.gtk_adjustment_get_upper(adj) - N.gtk_adjustment_get_page_size(adj) - 24;
    }

    // To the newest line, unless the user scrolled up to read (force: always).
    Action? scrollDown;
    void ScrollTalkDown(bool force)
    {
        if (!force) return;
        scrollDown ??= () =>
        {
            var adj = N.gtk_scrolled_window_get_vadjustment(talkScroll);
            N.gtk_adjustment_set_value(adj, N.gtk_adjustment_get_upper(adj));
        };
        After(60, scrollDown);
    }

    // The tabs' glyphs (♛ with a spinner after it while she answers; a
    // bee's spinner, ✓ or ✗) and the ✻ line, at time t.
    void DrawTalk(double t)
    {
        if (talkChip == 0) return;
        var smooth = Smooth;
        const string spin = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
        var spun = smooth ? spin[(int)(Frame(t) % 10)].ToString() : "⠿";
        var working = TalkBusy();
        N.gtk_label_set_markup(talkChip, Palette.Span(Palette.Pulse, "♛" + (working == null ? "" : " " + spun)));
        foreach (var b in beeTabs)
            if (b.Chip != 0)
                N.gtk_label_set_markup(b.Chip, b.State switch
                {
                    "" => Palette.Span(Palette.Pulse, spun),
                    "done" => Palette.Span(Palette.Green, "✓"),
                    _ => Palette.Span(Palette.Red, "✗"),
                });
        var bee = shown != talk && shown.State.Length == 0;
        N.gtk_widget_set_visible(talkBusy, shown == talk ? working != null : bee);
        if (working == null || shown != talk && !bee) return;
        const string star = "·✢✳✻✽✻";
        N.gtk_label_set_text(talkStar, smooth ? star[(int)(t / 0.2) % star.Length].ToString() : "✻");
        string said;
        if (bee)
        {
            said = "Thinking…";
            for (int i = shown.Lines.Count - 1; i >= 0; i--)
                if (shown.Lines[i].Kind == "tool") { said = shown.Lines[i].Bold + shown.Lines[i].Text + "…"; break; }
        }
        else
        {
            var step = work.Find(w => w.Task?.Id == working.Id)?.Step;
            said = (step is { } s && s.Tool != "think" ? char.ToUpperInvariant(s.Say[0]) + s.Say[1..] : "Thinking") + "…";
        }
        if (said.Length > 60) said = said[..59] + "…";
        if (said != talkBusySaid)
        {
            talkBusySaid = said;
            TypeIn(talkBusyText, said);
        }
        var took = DateTime.Now - (bee ? shown.Started : When(working.Doc.Get("started", working.Doc.Get("created"))));
        var time = took.TotalHours >= 1 ? $"{(int)took.TotalHours}h {took.Minutes:00}m" : $"{(int)took.TotalMinutes}m {took.Seconds:00}s";
        N.gtk_label_set_text(talkBusyMeta, $"({time}" + (shown.Tokens > 0 ? $" · ↓ {Tokens(shown.Tokens)} tokens" : "") + (bee ? ")" : " · esc to stop)"));
    }

    // The spinner and ✻ move while the Queen answers.
    void StartTalk()
    {
        if (talkMoving || !Smooth || TalkBusy() == null) return;
        talkMoving = true;
        Animate(talkStep);
    }

    bool TalkStep(double t)
    {
        DrawTalk(t);
        talkMoving = TalkBusy() != null;
        if (!talkMoving) DrawTalk(t);
        return talkMoving;
    }
}
