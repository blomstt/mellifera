using static Hive.Gui.Ui;

namespace Hive.Gui;

// The inbox, in a popover from the header's ▤ button: what waits for the
// user. Molts to approve, finished tasks to judge, failed tasks and finished
// reviews, each acted on where it is. Dismiss only takes an item off the
// list; it never counts against the bee (work without honey isn't
// punished). Its count is on the ▤ button and the Hive tab's badge.
sealed unsafe partial class App
{
    // Id is what it is: "molt:<id>", "applied:<id>" (a card change that
    // applied at once), "task:<id>" or "run:<id>". Act is the button that
    // acts in place (Approve, Undo, Give honey), if any.
    sealed class Item(string id, string bee, string title, string sub, string when, string act = "", bool failed = false)
    {
        public readonly string Id = id, Bee = bee, Title = title, Sub = sub, When = when, Act = act;
        public readonly bool Failed = failed;
        public string Key => Id[(Id.IndexOf(':') + 1)..];
    }

    // An item's row: its wipe, and the labels a refresh updates in place.
    sealed class InboxRow(Item item, Wipe wipe, nint title, nint sub)
    {
        public Item Item = item;
        public readonly Wipe Wipe = wipe;
        public readonly nint Title = title, Sub = sub;
        public double Held = -1; // covered until its bee drops it off (when it started waiting)
    }

    List<(string Id, Doc Doc)> applied = []; // card changes that applied at once (Store.SmallRule)
    nint inbox, inboxPanel, inboxEmpty, inboxButton, inboxPop, inboxBadge;
    Odometer inboxCount = null!;
    List<Item> items = [];
    readonly Dictionary<string, InboxRow> inboxShown = [];
    Action flushHeld = null!;

    // What the user dismissed, one id per line: the window's own note, not part of any bee's record.
    string DismissedPath => Path.Combine(store.Root, ".dismissed");

    nint BuildInbox()
    {
        inbox = Css(Box(Vertical, 0), "work-list");
        inboxEmpty = Margin((Css(Label("Nothing waits for you. Finished work, failed tasks, reviews and card changes show up here.", "dim-label", "small"), "caption")), 8, 16, 10, 16);
        inboxCount = new Odometer("inbox-count");
        flushHeld = FlushHeld;
        inboxPanel = Panel("inbox", Margin(Box(Vertical, 0, inboxEmpty, inbox), 8, 0, 6, 0));
        PanelRight(inboxPanel, inboxCount.Box);
        Expand(inboxPanel, true, true);
        return inboxPanel;
    }

    // The header's ▤ and how many wait, in honey (dimmed when nothing
    // does). It opens the inbox panel in a popover, 400 px wide, scrolling
    // when it's long; the panel draws itself in as it opens.
    nint BuildInboxButton()
    {
        inboxBadge = Css(ArtLabel("0"), "inbox-badge");
        inboxButton = Css(N.gtk_menu_button_new(), "inbox-button", "empty");
        N.gtk_menu_button_set_child(inboxButton, Box(Horizontal, 6, ArtLabel("▤"), inboxBadge));
        N.gtk_widget_set_tooltip_text(inboxButton, "What waits for you");
        Said(inboxButton, "Inbox, nothing waits");
        var panel = Clamp(inboxPanel, 400);
        N.gtk_widget_set_size_request(panel, 400, -1);
        var scroll = Scroll(panel);
        N.gtk_scrolled_window_set_propagate_natural_height(scroll, true);
        N.gtk_scrolled_window_set_max_content_height(scroll, 560);
        inboxPop = N.gtk_popover_new();
        N.gtk_popover_set_has_arrow(inboxPop, false);
        N.gtk_popover_set_child(inboxPop, scroll);
        On(inboxPop, "show", () => Fx.DrawIn(inboxPanel));
        N.gtk_menu_button_set_popover(inboxButton, inboxPop);
        return inboxButton;
    }

    // Tasks and reviews from the last week; older ones are in the bees' pages and Reviews.
    void RefreshInbox()
    {
        var dismissed = File.Exists(DismissedPath) ? new HashSet<string>(File.ReadAllLines(DismissedPath)) : [];
        var list = new List<Item>(); // card changes first, oldest first; then the rest, newest first
        foreach (var (id, doc) in molts)
        {
            if (dismissed.Contains("molt:" + id)) continue;
            var bee = doc.Get("bee");
            var by = doc.Get("by", bee);
            list.Add(new Item("molt:" + id, bee, by == bee ? $"{bee} wants to change its card" : $"A change for {bee}'s card",
                $"{by} · {doc.Get("reason")}", doc.Get("created"), "read"));
        }
        var weekAgo = DateTime.Now.AddDays(-7);
        foreach (var (id, doc) in applied)
        {
            if (dismissed.Contains("applied:" + id) || When(doc.Get("created")) < weekAgo || !bees.Exists(b => b.Name.Equals(doc.Get("bee"), StringComparison.OrdinalIgnoreCase))) continue;
            var bee = doc.Get("bee");
            list.Add(new Item("applied:" + id, bee, $"{bee}'s card gained a rule, applied", $"{doc.Get("by", bee)} · {doc.Get("reason")}", doc.Get("created"), "Undo"));
        }
        for (int i = 0; i < allTasks.Count; i++)
        {
            var t = allTasks[i];
            var finished = t.Doc.Get("finished");
            if (t.Running || When(finished) < weekAgo || t.Doc.Get("role") is "talk" or "queen") continue; // a talk turn was answered in the pane
            var said = Hive.Text.OneLine(t.Result) is var r && r.Length > 140 ? r[..140] + "…" : r;
            var (id, run) = ("task:" + t.Id, t.Doc.Get("run"));
            if (t.Doc.Get("role") == "overseer" && t.Status == "done" && !dismissed.Contains("run:" + run))
                list.Add(new Item("run:" + run, t.Bee, "Review ready: " + t.Doc.Get("focus"), t.Bee + ReviewCounts(t.Result), finished, "open"));
            else if (run.Length > 0 || dismissed.Contains(id)) continue;
            else if (t.Status == "done" && !t.Rewarded) // a miss (the Queen's or the hive's) can still earn the user's honey; it says so
                list.Add(new Item(id, t.Bee, t.Title, $"{t.Bee} · " + (t.Doc.Get("reward") == "no" ? $"a miss{GivenBy(t.Doc)} · " : "") + said, finished, "Give honey"));
            else if (t.Status == "failed") list.Add(new Item(id, t.Bee, t.Title, $"{t.Bee} · failed: {said}", finished, "open", failed: true));
        }
        var first = items.Count == 0 && inboxShown.Count == 0 && !inboxFilled;
        inboxFilled = true;
        items = list;
        var changed = false;
        // Gone: wiped out, then the rows under it close up.
        foreach (var (id, row) in inboxShown.ToList())
            if (!list.Exists(i => i.Id == id))
            {
                row.Wipe.Out();
                inboxShown.Remove(id);
                changed = true;
            }
        nint after = 0;
        foreach (var item in list)
        {
            if (inboxShown.TryGetValue(item.Id, out var row))
            {
                row.Item = item;
                N.gtk_label_set_text(row.Title, item.Title);
                N.gtk_label_set_text(row.Sub, SubLine(item));
            }
            else
            {
                row = inboxShown[item.Id] = InboxRowFor(item);
                if (after == 0) N.gtk_box_prepend(inbox, row.Wipe.Outer);
                else N.gtk_box_insert_child_after(inbox, row.Wipe.Outer, after);
                changed = true;
                // A bee's result waits behind black until the bee drops it
                // off in the scene; anything else wipes in now.
                if (first) { }
                else if (item.Id.StartsWith("task:") && sprites.TryGetValue(item.Bee, out var s) && s.TaskId == item.Key && Smooth && slots.Count > 0)
                {
                    row.Held = Clock;
                    row.Wipe.Hold();
                    After(4000, flushHeld);
                }
                else row.Wipe.In();
            }
            N.gtk_box_reorder_child_after(inbox, row.Wipe.Outer, after);
            after = row.Wipe.Outer;
        }
        N.gtk_widget_set_visible(inboxEmpty, list.Count == 0);
        inboxCount.Set(list.Count);
        if (changed && !first) Fx.Spark(inboxPanel);
        N.adw_view_stack_page_set_badge_number(hivePage, (uint)items.Count);
        N.adw_view_stack_page_set_needs_attention(hivePage, items.Count > 0);
        N.gtk_label_set_text(inboxBadge, items.Count.ToString());
        if (items.Count > 0) N.gtk_widget_remove_css_class(inboxButton, "empty");
        else N.gtk_widget_add_css_class(inboxButton, "empty");
        Said(inboxButton, items.Count > 0 ? $"Inbox, {items.Count} waiting" : "Inbox, nothing waits");
    }

    bool inboxFilled;

    // An item, as the mockup has it: a glyph for its kind (≡ a review, ✓
    // finished, ✗ failed, ◇ a card change), what it is, who and when, and
    // the one button that acts on it; ✕ takes it off the list. Clicking the
    // text opens it.
    InboxRow InboxRowFor(Item item)
    {
        var (icon, kind) = item.Id[..item.Id.IndexOf(':')] switch
        {
            "molt" or "applied" => ("◇", "molt"),
            "run" => ("≡", "review"),
            _ => item.Failed ? ("✗", "failed") : ("✓", "done"),
        };
        var title = (Css(Label(item.Title), "inbox-title"));
        N.gtk_label_set_lines(title, 2);
        N.gtk_label_set_ellipsize(title, 3);
        var sub = (Css(Label(SubLine(item)), "inbox-sub"));
        N.gtk_label_set_lines(sub, 2);
        N.gtk_label_set_ellipsize(sub, 3);
        var text = Expand(Box(Vertical, 2, title, sub), true, false);
        OnClick(text, (_, _) => Open(inboxShown.TryGetValue(item.Id, out var r) ? r.Item : item));
        var act = Button(item.Act.Length > 0 ? item.Act : "open", () => { if (item.Act is "Give honey" or "Undo") Act(item); else Open(item); },
            item.Id.StartsWith("run:") || item.Act == "Give honey" ? "suggested-action" : "flat");
        N.gtk_widget_set_valign(act, Start);
        var dismiss = Css(TextButton("✕", () => Dismiss(item.Id), "close"), "ghost");
        Said(dismiss, "Dismiss");
        N.gtk_widget_set_tooltip_text(dismiss, "Take it off the list. It doesn't count against the bee.");
        N.gtk_widget_set_valign(dismiss, Start);
        var glyph = Css(ArtLabel(icon), "inbox-icon", kind);
        N.gtk_widget_set_valign(glyph, Start);
        var row = Css(Box(Horizontal, 12, glyph, text, act, dismiss), "inbox-item");
        return new InboxRow(item, new Wipe(row), title, sub);
    }

    static string SubLine(Item item) => (item.Sub.Length > 0 ? item.Sub + " · " : "") + Ago(item.When);

    // " · 19 findings, 4 high" from an overseer's report, if it numbers them.
    static string ReviewCounts(string report)
    {
        var (findings, high) = Md.Findings(report);
        return findings == 0 ? "" : $" · {findings} finding{(findings == 1 ? "" : "s")}" + (high > 0 ? $", {high} high" : "");
    }

    // A bee dropped this task's result at the inbox (the scene's delivery): it wipes in.
    void Arrived(string taskId)
    {
        if (inboxShown.TryGetValue("task:" + taskId, out var row) && row.Held >= 0)
        {
            row.Held = -1;
            row.Wipe.In(0.45);
        }
    }

    // A result whose bee never got there (it went elsewhere) shows anyway.
    void FlushHeld()
    {
        foreach (var row in inboxShown.Values)
            if (row.Held >= 0 && Clock - row.Held > 3.5)
            {
                row.Held = -1;
                row.Wipe.In();
            }
    }

    void Open(Item item)
    {
        N.gtk_popover_popdown(inboxPop); // the page or task it opens shows, not the popover over it
        switch (item.Id[..item.Id.IndexOf(':')])
        {
            case "molt" or "applied": ShowBee(item.Bee); Go("bees"); Reveal(beeSplit); break;
            case "run": ShowRun(item.Key); Go("reviews"); Reveal(reviewSplit); break;
            default: ShowTask(item.Key); break;
        }
    }

    // Approve a molt, undo one that applied at once (it waits for you then),
    // or give a finished task honey.
    void Act(Item item)
    {
        if (item.Id.StartsWith("molt:"))
        {
            DecideMolt(item.Key, true, item.Bee + "'s card is changed.");
            return;
        }
        if (item.Id.StartsWith("applied:"))
        {
            store.ReopenMolt(item.Key); // a card changed again since says so, in red
            Say($"Undone: {item.Bee}'s card is back, and the change waits for you.");
            Refresh();
            return;
        }
        Tasks.Reward(store, item.Key, "", true);
        Honeyed(item.Bee + " earned honey");
        Refresh();
    }

    void Dismiss(string id)
    {
        File.AppendAllText(DismissedPath, id + "\n");
        RefreshInbox();
        SyncScene(); // a bee lying under its station since its task failed flies home
    }
}
