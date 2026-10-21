using System.Text;

namespace Hive;

// Honey and misses: an entry or a task can be marked as honey (reward: yes)
// or a miss (reward: no), with who gave it (user, queen: the main session
// through bee_reward, or hive: Reviews.Award) and a note. A bee's briefing
// lists its honeyed work; bee_review gives a bee's whole record, and the bee
// with the evolver role reviews bees from it (Tasks.Evolve, AutoEvolve).
sealed partial class Store
{
    // ---- honey -------------------------------------------------------------

    // Honey (reward: yes) or, with miss, a miss (reward: no) for an entry or a
    // task (task:<id>), from by: user, queen or hive. The note is the user's,
    // or the evidence it rests on. give false takes either back.
    public void RewardEntry(string id, string note, bool give, string by = "user", bool miss = false)
    {
        if (id.Trim().StartsWith("task:")) { Tasks.Reward(this, id.Trim()[5..], note, give, by, miss); return; }
        var (found, path) = Locate(id) ?? throw new HiveException($"Nothing has the id {id}.");
        if (found.Contains(':') || found == "queen") throw new HiveException("Only entries and tasks can earn honey.");
        var doc = Doc.Load(path);
        if (FindBee(doc.Get("bee")) == null)
            throw new HiveException($"Only work a bee did can earn honey; this was written by {doc.Get("bee", "nobody")}.");
        SetReward(doc, note, give, by, miss);
        doc.Save(path);
        Sync();
        if (give) AutoEvolve(doc.Get("bee"));
    }

    internal static void SetReward(Doc doc, string note, bool give, string by = "user", bool miss = false)
    {
        if (give)
        {
            doc.Set("reward", miss ? "no" : "yes");
            doc.Set("reward_by", by);
            doc.Set("rewarded", Text.Now);
            if (note.Trim().Length > 0) doc.Set("reward_note", note); else doc.Remove("reward_note");
        }
        else
        {
            doc.Remove("reward");
            doc.Remove("reward_by");
            doc.Remove("rewarded");
            doc.Remove("reward_note");
        }
    }

    // bee_reward: the main session (queen) gives honey or a miss, with its
    // evidence. A bee's own work is refused, and so is a bee as the giver;
    // what the user or the hive (Reviews.Award) already recorded is kept:
    // only the user changes or takes back those.
    public string Award(string id, string outcome, string evidence, string by)
    {
        if (outcome is not ("honey" or "miss")) throw new HiveException("outcome is honey or miss.");
        evidence = Text.OneLine(evidence);
        if (evidence.Length == 0) throw new HiveException("evidence is required: one line on what it rests on.");
        id = id.Trim();
        var doc = id.StartsWith("task:") ? Tasks.Load(this, id[5..]).Doc
            : Locate(id) is var (found, path) && !found.Contains(':') && found != "queen" ? Doc.Load(path)
            : throw new HiveException($"Nothing that can earn honey has the id {id}.");
        var owner = doc.Get("bee");
        by = by.Trim().Length > 0 ? by.Trim() : "queen";
        if (by.Equals(owner, StringComparison.OrdinalIgnoreCase))
            throw new HiveException($"No bee rewards its own work: {id} is {owner}'s.");
        if (!by.Equals("queen", StringComparison.OrdinalIgnoreCase))
            throw new HiveException("bee_reward records the main session's judgement (by: \"queen\"); a bee can't give it.");
        if (doc.Get("reward").Length > 0 && doc.Get("reward_by", "user") is "user" or "hive")
            throw new HiveException(doc.Get("reward_by", "user") == "user"
                ? $"The user already judged {id}; that stays theirs."
                : $"The hive already judged {id} from its outcome ({doc.Get("reward_note")}); only the user can change that.");
        RewardEntry(id, evidence, true, "queen", outcome == "miss");
        return outcome == "honey" ? $"{owner} earned honey for {id}." : $"{id} is recorded as a miss for {owner}.";
    }

    // How many new honey or miss outcomes a bee needs since its last review
    // before the evolver bee reviews it by itself: the evolver card's
    // evolve_after (0 turns it off), else this.
    public const int EvolveAfter = 5;

    public static int EvolveAfterFor(Bee evolver) =>
        int.TryParse(evolver.Doc.Get("evolve_after"), out var n) && n >= 0 ? n : EvolveAfter;

    // A bee's settings the hive acts on, in its card's front matter:
    // evolve_after (a number, for the evolver) and deposit_before_stop (yes or
    // no: Hooks sends it back once if it stops having saved nothing).
    public Bee SetSetting(string beeName, string key, string value)
    {
        var bee = RequireBee(beeName);
        if (SettingValue(key, value) is { } set) bee.Doc.Set(key, set); else bee.Doc.Remove(key);
        bee.Doc.Save(bee.Path);
        return LoadBee(bee.Path);
    }

    // What a setting's value is saved as (null: removed), or a HiveException
    // for one it can't be; the CLI checks every option with it before changing anything.
    public static string? SettingValue(string key, string value)
    {
        value = value.Trim().ToLowerInvariant();
        return key switch
        {
            "evolve_after" when int.TryParse(value, out var n) && n >= 0 => n.ToString(),
            "evolve_after" => throw new HiveException("evolve-after is a number of outcomes, 0 for never."),
            "deposit_before_stop" when value is "yes" or "no" => value == "yes" ? "yes" : null,
            "deposit_before_stop" => throw new HiveException("deposit-before-stop is yes or no."),
            _ => throw new HiveException($"{key} isn't a bee setting."),
        };
    }

    // After honey or a miss: once the bee has that many new ones since its
    // last review, the evolver bee reviews it in the background (a read
    // task), unless a review of it is still running. The evolver itself is
    // never reviewed this way, so its own reviews can't set off more of them.
    // The review task, if one started.
    public BeeTask? AutoEvolve(string beeName)
    {
        if (FindBee(beeName) is not { } bee || RoleBee("evolver") is not { } evolver || bee.Name == evolver.Name) return null;
        var after = EvolveAfterFor(evolver);
        if (after == 0) return null;
        // One process at a time from the check to the start: two judging the
        // 5th and 6th outcome at once would each see no review running.
        using var held = Hold(Path.Combine(Root, ".tasks", ".evolve-lock"));
        if (held == null) return null;
        BeeTask? last = null;
        foreach (var t in Tasks.List(this)) // newest first; any bee's, as the evolver role can move mid-review
        {
            if (!Tasks.IsReviewOf(t, bee.Name)) continue;
            if (t.Running) return null;
            last ??= t;
        }
        // "rewarded" and "created" are to the minute: one in the same minute as the last review doesn't count.
        var since = last?.Doc.Get("created") ?? "";
        if (Outcomes(bee).Count(o => string.CompareOrdinal(o.When, since) > 0) < after) return null;
        try { return Tasks.Evolve(this, bee.Name, auto: true); }
        catch (HiveException) { return null; } // the honey is given whether or not the review could start
    }

    // An exclusive lock on the file (flock, freed when the process dies), or
    // null after about 5 s of waiting for it.
    static FileStream? Hold(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (int tries = 0; ; tries++)
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (tries < 100) { Thread.Sleep(50); }
            catch (IOException) { return null; }
    }

    // Per bee (lower case): how much honey it has earned, from entries and tasks.
    public Dictionary<string, int> Honey()
    {
        Sync();
        var honey = Db.Query("SELECT lower(bee), count(*) FROM docs WHERE kind = 'entry' AND reward = 'yes' GROUP BY lower(bee)",
                             r => (Bee: r.Text(0), Count: (int)r.Long(1)))
                      .ToDictionary(x => x.Bee, x => x.Count);
        foreach (var t in Tasks.List(this).Where(t => t.Rewarded))
            honey[t.Bee.ToLowerInvariant()] = honey.GetValueOrDefault(t.Bee.ToLowerInvariant()) + 1;
        return honey;
    }

    // Honey or a miss: what, from whom (user, queen, hive), its note or
    // evidence, and when.
    sealed record Outcome(string Id, string Title, bool Honey, string By, string Note, string When)
    {
        public string From => By switch { "queen" => "the Queen", "hive" => "the hive", _ => "the user" };
    }

    // A bee's honey and misses, from entries and tasks, newest first.
    List<Outcome> Outcomes(Bee bee)
    {
        Sync();
        var all = new List<Outcome>();
        foreach (var e in Db.Query("SELECT id, path, title FROM docs WHERE kind = 'entry' AND reward IN ('yes', 'no') AND bee = ? COLLATE NOCASE",
                                   r => (Id: r.Text(0), Path: r.Text(1), Title: r.Text(2)), bee.Name))
            all.Add(OutcomeOf(e.Id, e.Title, Doc.Load(P(e.Path))));
        foreach (var t in Tasks.List(this, bee.Name))
            if (t.Doc.Get("reward") is "yes" or "no") all.Add(OutcomeOf("task:" + t.Id, t.Title, t.Doc));
        return all.OrderByDescending(o => o.When, StringComparer.Ordinal).ToList();
    }

    static Outcome OutcomeOf(string id, string title, Doc d) =>
        new(id, title, d.Get("reward") == "yes", d.Get("reward_by", "user"), d.Get("reward_note"), d.Get("rewarded"));

    static string Said(Outcome o) => o.Note.Length > 0 ? $" — {o.From}: \"{o.Note}\"" : $" — from {o.From}";

    string BestWork(Bee bee)
    {
        var best = Outcomes(bee).Where(o => o.Honey).Take(5).ToList();
        if (best.Count == 0) return "";
        var sb = new StringBuilder("## Your work that earned honey\n\nNewest first, with who gave it and why. hive_read an id to see the work.\n");
        foreach (var o in best) sb.AppendLine($"- [{o.Id}] {o.Title}" + Said(o));
        return sb.ToString();
    }

    // ---- a bee's record (bee_review) ---------------------------------------

    public string Review(string beeName)
    {
        using var batch = Reads();
        Sync();
        var bee = RequireBee(beeName);
        var sb = new StringBuilder();
        var outcomes = Outcomes(bee);
        var honey = Honey().GetValueOrDefault(bee.Name.ToLowerInvariant());
        sb.AppendLine($"# Review: {bee.Name}").AppendLine();
        sb.AppendLine($"Specialty: {bee.Specialty}");
        sb.AppendLine($"Projects: {(bee.Projects.Count > 0 ? string.Join(", ", bee.Projects) : "any")}");
        sb.AppendLine($"Honey earned: {honey} · misses: {outcomes.Count(o => !o.Honey)}");
        sb.AppendLine().AppendLine("## Card now").AppendLine().AppendLine(bee.Doc.Body.Trim()).AppendLine();

        sb.AppendLine("## How the card changed");
        var changes = CardChanges(bee);
        sb.AppendLine(changes.Count == 0 ? "It has never changed." : string.Join('\n', changes)).AppendLine();

        var tasks = Tasks.List(this, bee.Name).Take(15).ToList();
        sb.AppendLine("## Its tasks (newest first)");
        if (tasks.Count == 0) sb.AppendLine("None yet.");
        foreach (var t in tasks)
        {
            var judged = t.Doc.Get("reward") switch
            {
                "yes" => " · EARNED HONEY",
                "no" => " · MISS",
                _ => " · no honey",
            };
            if (t.Doc.Get("reward").Length > 0) judged += Said(OutcomeOf("", "", t.Doc));
            sb.AppendLine($"- [task:{t.Id}] {t.Doc.Get("created")} · {t.Status}" + (t.Doc.Get("took") is { Length: > 0 } took ? $" in {took}" : "")
                          + judged + $" — {t.Title}");
            if (!t.Running && Text.OneLine(t.Result) is { Length: > 0 } r) sb.AppendLine("    result: " + (r.Length > 280 ? r[..280] + "…" : r));
        }
        sb.AppendLine();

        if (outcomes.Count > 0)
        {
            sb.AppendLine("## Honey and misses (newest first; who gave each, and its note or evidence)");
            foreach (var o in outcomes.Take(20)) sb.AppendLine($"- [{o.Id}] {(o.Honey ? "HONEY" : "MISS")} · {o.When} — {o.Title}" + Said(o));
            sb.AppendLine();
        }
        sb.AppendLine("## Its recent entries").AppendLine(Lines(Search("", bee: bee.Name, limit: 12), "None yet.")).AppendLine();
        sb.AppendLine("## Lessons other bees learned in this area")
          .AppendLine(Lines(Search(bee.Specialty, type: "lesson", limit: 8).Where(h => !h.Bee.Equals(bee.Name, StringComparison.OrdinalIgnoreCase)), "None yet."));
        sb.AppendLine().AppendLine("""
            Honey is reward: yes and a miss reward: no; each says who gave it (the user, queen for the main session, or the hive from a review's count of confirmed findings) and its note. A card change proposed with bee_molt by the bee with the evolver role, for another bee, that only adds list items at the end of "## Rules I've learned" (in place of a lone "None yet."), each citing two or more of the outcome ids above, applies at once and the user can undo it; any other change waits for the user's approval.
            """);
        return Text.Cap(sb.ToString(), 16000);
    }

    // ---- card history ------------------------------------------------------

    // Snapshots of a bee's card, taken before each change, oldest first:
    // bees/.history/<Name>-yyyyMMdd-HHmmss.md, then -2, -3... for more in the same second.
    List<string> Snapshots(Bee bee) =>
        Directory.Exists(P("bees", ".history"))
            ? Directory.EnumerateFiles(P("bees", ".history"), bee.Name + "-*.md")
                .Select(f => (Path: f, Stamp: Path.GetFileNameWithoutExtension(f)[(bee.Name.Length + 1)..]))
                .Where(s => s.Stamp.Length >= 15 && s.Stamp[8] == '-' && s.Stamp[..15].Remove(8, 1).All(char.IsAsciiDigit)
                            && (s.Stamp.Length == 15 || s.Stamp.Length is > 16 and <= 22 && s.Stamp[15] == '-' && s.Stamp[16..].All(char.IsAsciiDigit)))
                .OrderBy(s => s.Stamp[..15], StringComparer.Ordinal).ThenBy(s => s.Stamp.Length == 15 ? 1 : int.Parse(s.Stamp[16..]))
                .Select(s => s.Path).ToList()
            : [];

    // The card changes proposed for a bee, oldest first, each with how it
    // was decided ("decided: approved" or "rejected", not saved).
    public List<Doc> CardDecisions(Bee bee)
    {
        var decided = new List<Doc>();
        if (Directory.Exists(P("bees", ".history")))
            foreach (var f in Directory.EnumerateFiles(P("bees", ".history"), "*.md").Order())
            {
                var name = Path.GetFileName(f);
                if (!(name.EndsWith(".approved.md") || name.EndsWith(".rejected.md"))) continue;
                var d = Doc.Load(f);
                if (!d.Get("bee").Equals(bee.Name, StringComparison.OrdinalIgnoreCase)) continue;
                d.Set("decided", name.EndsWith(".approved.md") ? "approved" : "rejected");
                decided.Add(d);
            }
        return decided;
    }

    List<string> CardChanges(Bee bee)
    {
        var lines = new List<string>();
        foreach (var d in CardDecisions(bee))
            lines.Add($"- {d.Get("created")} · {(d.Get("applied") == "auto" ? "applied at once (rules backed by outcomes)" : d.Get("decided"))} · proposed by {d.Get("by", d.Get("bee"))}: {d.Get("reason")}");
        if (bee.Doc.Get("molted") is { Length: > 0 } molted) lines.Add($"- last changed {molted}");
        return lines;
    }

    // When the card last changed, if it can be undone.
    public string? LastCardChange(string beeName)
    {
        var snap = Snapshots(RequireBee(beeName)).LastOrDefault();
        return snap == null ? null : File.GetLastWriteTime(snap).ToString("yyyy-MM-dd HH:mm");
    }

    // Puts back the card from before the last change. Repeating it walks
    // further back: the restored snapshot and the undone card are set aside.
    public void UndoCard(string beeName, bool fields = false)
    {
        var bee = RequireBee(beeName);
        var snap = Snapshots(bee).LastOrDefault() ?? throw new HiveException($"{bee.Name}'s card has no earlier version.");
        // With fields, the whole snapshot, so its specialty and projects come
        // back too (undoing a molt whose front matter changed them).
        ApplyCard(bee, fields ? File.ReadAllText(snap) : Doc.Load(snap).Body, ".undone");
        File.Move(snap, Path.ChangeExtension(snap, ".restored.md"), overwrite: true);
    }
}
