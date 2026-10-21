using System.Text.Json.Nodes;
using static Hive.Gui.Ui;

namespace Hive.Gui;

// The work pane's bee tabs: one to watch each bee the pane's Queen hands
// work to with her Agent tool, in the foreground or the background. Claude
// Code keeps a subagent's transcript beside its session's, in
// <session>/subagents/agent-<id>.jsonl, with agent-<id>.meta.json saying
// which agent it runs as; the Queen's own transcript says how it ended (the
// Agent call's result, or the notification a background one sends). A bee
// works as long as her turn runs: the turn waits for its background bees,
// and esc, which stops the turn, stops them too. Watch-only: the user steers
// through the Queen. A finished tab stays, ✓ or ✗, until it's closed;
// "new" clears them. Subagents of the user's terminal sessions, tasks and
// reviews get no tab (the scene shows them).
sealed unsafe partial class App
{
    readonly List<TalkTab> beeTabs = [];
    readonly HashSet<string> beeTabsSkipped = [];        // agent ids: closed (Tasks.QueenClosed), not a bee, or a bee's own subagent
    readonly Dictionary<string, string> agentEnds = [];  // agent id -> how it ended, from the Queen's transcript (Transcript.AgentEnd)
    readonly HashSet<string> beeCalls = [];              // the Agent calls in the bees' transcripts: their subagents aren't the Queen's

    // After the Queen's transcript was read: tabs for bees she started
    // since, each tab's new lines, and how the ones that were working ended.
    void UpdateBeeTabs()
    {
        // The folder is listed on every look: its time can stay put while a
        // meta lands in the same tick, or while a half-written one is finished.
        var opened = talk.File != null && talk.Session.Length > 0 && OpenBeeTabs(Path.Combine(Path.GetDirectoryName(talk.File)!, talk.Session, "subagents"));
        foreach (var tab in beeTabs) ReadTranscript(tab);
        // A bee's own subagent (its Agent call in a bee's transcript) isn't the Queen's: no tab.
        for (int i = beeTabs.Count - 1; i >= 0; i--)
            if (beeTabs[i] is var nested && beeCalls.Contains(nested.Call))
            {
                beeTabsSkipped.Add(nested.Agent);
                beeTabs.RemoveAt(i);
                if (shown == nested) ShowTab(talk);
                opened = true;
            }
        var queenBusy = TalkBusy() != null;
        var ended = false;
        foreach (var tab in beeTabs)
        {
            if (tab.State.Length > 0) continue;
            var end = agentEnds.GetValueOrDefault(tab.Agent, "");
            if (end == "" && !queenBusy && talk.Start > 0 && !earlierEnds)
            {
                // Her transcript was read from its end: how it ended may be further back (looked through once).
                earlierEnds = true;
                Dictionary<string, string> before = [];
                foreach (var line in Earlier(talk)) Transcript.AgentEnd(line, before);
                foreach (var (id, how) in before) agentEnds.TryAdd(id, how);
                end = agentEnds.GetValueOrDefault(tab.Agent, "");
            }
            // A bee still at it when her turn is over went with the turn (esc, or it died).
            tab.State = end == "completed" ? "done" : end is not ("" or "async_launched") ? "failed" : queenBusy ? "" : "stopped";
            ended |= tab.State.Length > 0;
        }
        if (opened) RebuildBeeChips();
        if (ended && shown != talk) ShowMode();
        if (opened || ended) Fx.Spark(talkPanel); // a bee started or finished: the pane's border sparks once
    }

    // A tab for each bee in the subagents folder that hasn't one: the
    // Queen's subagents that run as a bee, working or finished (after a
    // restart too), unless the user closed it.
    bool OpenBeeTabs(string dir)
    {
        var opened = false;
        List<string> metas = [];
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
                if (Path.GetFileName(f) is var n && n.StartsWith("agent-") && n.EndsWith(".meta.json")) metas.Add(f);
        }
        catch (IOException) { return false; } // none yet (DirectoryNotFoundException is one)
        foreach (var meta in metas)
        {
            var name = Path.GetFileName(meta);
            var id = name.Substring(6, name.Length - 6 - 10); // agent-<id>.meta.json
            if (beeTabsSkipped.Contains(id) || beeTabs.Exists(t => t.Agent == id)) continue;
            JsonObject? o;
            try { o = JsonNode.Parse(File.ReadAllText(meta)) as JsonObject; }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { continue; } // still being written: next look
            var type = Json.Str(o?["agentType"]);
            var bee = bees.Find(b => Agents.Name(b.Name) == type);
            var call = Json.Str(o?["toolUseId"]);
            if (bee == null || beeCalls.Contains(call))
            {
                beeTabsSkipped.Add(id);
                continue;
            }
            beeTabs.Add(new TalkTab(bee.Name)
            {
                Agent = id,
                Call = call,
                What = Json.Str(o?["description"]),
                File = Path.Combine(dir, $"agent-{id}.jsonl"),
                Folder = talk.Folder,
                Started = File.GetLastWriteTime(meta),
            });
            opened = true;
        }
        return opened;
    }

    // The bees' tabs after ♛ Queen: a spinner while it works, then ✓ or ✗.
    void RebuildBeeChips()
    {
        Clear(talkTabs);
        foreach (var tab in beeTabs)
        {
            var t = tab;
            var glyph = Css(ArtLabel(), "talk-glyph");
            var chip = Css(N.gtk_button_new(), "talk-chip");
            var name = N.gtk_label_new(t.Bee);
            N.gtk_button_set_child(chip, Box(Horizontal, 8, glyph, name));
            if (!t.Typed) TypeIn(name, t.Bee, 60, ""); // a new tab's name types in
            t.Typed = true;
            On(chip, "clicked", () => ShowTab(t));
            Said(chip, $"Watch {t.Bee}" + (t.What.Length > 0 ? ": " + t.What : ""));
            if (t.What.Length > 0) N.gtk_widget_set_tooltip_text(chip, t.What);
            if (t == shown) N.gtk_widget_add_css_class(chip, "on");
            t.Chip = glyph;
            N.gtk_box_append(talkTabs, chip);
        }
        DrawTalk(Clock);
        StartTalk();
    }

    // ✕ close: the tab goes (the bee carries on if it still works).
    void CloseBeeTab(TalkTab tab)
    {
        if (tab == talk) return;
        beeTabs.Remove(tab);
        beeTabsSkipped.Add(tab.Agent);
        Tasks.CloseQueenTab(store, tab.Agent); // and it stays closed after a restart
        if (shown == tab) ShowTab(talk);
        else RebuildBeeChips();
    }

    // "new": the tabs close (all finished: "new" waits for her turn to end);
    // the new conversation's bees get new ones.
    void ClearBeeTabs()
    {
        foreach (var tab in beeTabs) beeTabsSkipped.Add(tab.Agent);
        beeTabs.Clear();
        agentEnds.Clear();
        shown = talk;
        N.gtk_widget_add_css_class(talkQueenTab, "on");
        RebuildBeeChips();
    }
}
