namespace Hive;

record ReviewRun(string Id, List<BeeTask> Reviewers, BeeTask? Overseer)
{
    public IEnumerable<BeeTask> All => Overseer == null ? Reviewers : Reviewers.Append(Overseer);
    public bool Running => All.Any(t => t.Running);
    public string Focus => All.Select(t => t.Doc.Get("focus")).FirstOrDefault(f => f.Length > 0) ?? "A team review";
    public string Project => All.First().Doc.Get("project");
    public string Created => All.First().Doc.Get("created");

    public string Stage =>
        Reviewers.Any(t => t.Running) ? $"reviewers working: {Reviewers.Count(t => !t.Running)} of {Reviewers.Count} done"
        : Overseer is { Running: true } ? $"{Overseer.Bee} is going through the reports"
        : Overseer?.Status == "done" ? "done"
        : Overseer?.Status ?? "stopped";
}

// A team review: the bees with the reviewer role look at the same thing on
// their own, and the bee with the overseer role gets all their reports and
// writes the one report. Reviewers are blind: they save nothing during a run,
// so none can see another's findings; only the overseer reads all of them.
// A bee's role is in its card's front matter (role: reviewer / overseer);
// how each one reviews is up to its card.
static class Reviews
{
    public static List<Bee> TeamReviewers(Store store) => store.Bees().Where(b => b.Doc.Get("role") == "reviewer").ToList();
    public static Bee? TeamOverseer(Store store) => store.Bees().FirstOrDefault(b => b.Doc.Get("role") == "overseer");

    // The team from names the user gives: every one but the last a reviewer,
    // the last the overseer. A bee that exists gets the role; a name that
    // doesn't becomes a new bee with that role and an empty card.
    public static List<Bee> SetUp(Store store, string[] names)
    {
        if (TeamReviewers(store).Count > 0 || TeamOverseer(store) != null)
            throw new HiveException("There's already a review team: the bees with the reviewer and overseer roles (`hive bee <Name> --role`).");
        names = names.Select(n => n.Trim()).ToArray();
        if (names.Length < 2 || names.Any(n => n.Length == 0)) throw new HiveException("The team needs at least two names: the reviewers, then the overseer.");
        if (names.Select(n => n.ToLowerInvariant()).Distinct().Count() != names.Length) throw new HiveException("The names must differ.");
        // All of them first: a name refused halfway would leave half a team.
        foreach (var name in names)
            if (store.FindBee(name) is not { } bee) store.CheckNewBee(name);
            else if (bee.Doc.Get("role") is { Length: > 0 } had) // a bee has one role: the team's would silently take this one
                throw new HiveException($"{bee.Name} has the {had} role; on the team it would lose it. Name other bees, or take its role away first (`hive bee {bee.Name} --role none`).");
        var bees = new List<Bee>();
        for (int i = 0; i < names.Length; i++)
        {
            var role = i == names.Length - 1 ? "overseer" : "reviewer";
            bees.Add(store.FindBee(names[i]) is { } bee
                ? store.UpdateBee(bee.Name, null, null, null, role: role)
                : store.CreateBee(names[i], role, [], "", "approve", role: role));
        }
        return bees;
    }

    // A run in the background: each bee is its own headless session, and
    // the overseer's runner waits for the reviewers.
    public static ReviewRun Start(Store store, string project, string focus)
    {
        var run = Create(store, project, focus);
        foreach (var t in run.All) Tasks.Start(t);
        return run;
    }

    static ReviewRun Create(Store store, string project, string focus)
    {
        var reviewers = TeamReviewers(store);
        var overseer = TeamOverseer(store);
        if (reviewers.Count == 0 || overseer == null) throw new HiveException("There's no review team yet.");
        focus = focus.Trim().Length > 0 ? focus.Trim() : "the whole project";
        project = project.Trim().ToLowerInvariant();
        var run = $"review-{DateTime.Now:yyyyMMdd-HHmmss}";
        var names = string.Join(", ", reviewers.Select(r => r.Name));

        var tasks = reviewers.Select(r => Tasks.Create(store, r.Name, $"""
            Team review {run}. Review: {focus}

            {(reviewers.Count == 1 ? "You're the only reviewer" : $"This is a blind review: {reviewers.Count - 1} other reviewer{(reviewers.Count == 2 ? " is" : "s are")} looking at the same thing on their own")}, so save nothing to the hive during it{(reviewers.Count == 1 ? "" : " and don't look for their reports")}. Your reply is your report; it goes to {overseer.Name}.
            """, project, "read", run, "reviewer")).ToList();

        var over = Tasks.Create(store, overseer.Name, $"""
            Team review {run}: {reviewers.Count} reviewers ({names}) looked at "{focus}" on their own. Their reports follow. Your reply is the review's report.

            {ReviewersTable}
            """, project, "read", run, "overseer", waitsFor: tasks.Select(t => t.Id));

        foreach (var t in tasks.Append(over))
        {
            t.Doc.Set("focus", focus);
            t.Doc.Save(t.Path);
        }
        return new ReviewRun(run, tasks, over);
    }

    // ---- a run led by a Claude session ----------------------------------------
    //
    // The session is the Queen: it spawns the reviewers as subagents side by
    // side, hands each report in with Report, and gets back the overseer's
    // prompt once the last one is in. The run is recorded like any other, so it
    // shows in the window, earns honey, and counts in bee_review. A run nobody
    // finishes reads as stopped after six hours, or when stopped in the window.

    public static string Begin(Store store, string project, string focus, string cwd)
    {
        if (project.Trim().Length == 0) project = store.ProjectFor(cwd)?.Name ?? "";
        var run = Create(store, project, focus);
        var folder = run.Reviewers[0].Doc.Get("folder");
        foreach (var t in run.All)
        {
            t.Doc.Set("status", t == run.Overseer ? "waiting" : "running");
            t.Doc.Set("led_by", "session");
            if (t != run.Overseer) t.Doc.Set("started", Text.Now);
            t.Doc.Save(t.Path);
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Review {run.Id} begins: \"{run.Focus}\"{(project.Length > 0 ? $" in project {project}" : "")} ({folder}).");
        sb.AppendLine();
        sb.AppendLine($"Now spawn all {run.Reviewers.Count} reviewers in ONE message, so they run side by side, with the Agent tool. Give each one exactly its prompt below; add nothing, and never show one reviewer another's report.");
        foreach (var t in run.Reviewers)
            sb.AppendLine().AppendLine($"### subagent_type: {Agents.Name(t.Bee)}").AppendLine().AppendLine(t.Prompt + $"\n\n(You are working in {folder}. Reply with your full report.)");
        sb.AppendLine().AppendLine($"As each reviewer finishes, pass its whole reply to team_review_report (run: \"{run.Id}\", bee: its name). When the last one is in, team_review_report gives you {run.Overseer!.Bee}'s prompt.");
        return sb.ToString();
    }

    public static string Report(Store store, string runId, string beeName, string report)
    {
        var run = Find(store, runId);
        var task = run.All.FirstOrDefault(t => t.Bee.Equals(beeName.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new HiveException($"{beeName} isn't in review {run.Id}; its bees are {string.Join(", ", run.All.Select(t => t.Bee))}.");
        if (report.Trim().Length == 0) throw new HiveException("report is the bee's whole reply.");
        Tasks.Record(store, task, report, "done");

        if (task == run.Overseer)
            return $"Review {run.Id} is done and saved. {Award(store, Find(store, runId))}".TrimEnd();

        run = Find(store, runId);
        var waiting = run.Reviewers.Where(t => t.Running).Select(t => t.Bee).ToList();
        if (waiting.Count > 0) return $"Got {task.Bee}'s report. Still waiting for {string.Join(", ", waiting)}; don't start {run.Overseer!.Bee} yet.";

        var over = run.Overseer!;
        over.Doc.Set("status", "running");
        over.Doc.Set("started", Text.Now);
        over.Doc.Save(over.Path);
        return $"""
            All {run.Reviewers.Count} reports are in. Now spawn {over.Bee} with the Agent tool (subagent_type: {Agents.Name(over.Bee)}), with exactly this prompt, then pass its whole reply to team_review_report (run: "{run.Id}", bee: "{over.Bee}"):

            {over.Prompt}

            (You are working in {over.Doc.Get("folder")}.)

            {Reports(store, over)}
            """;
    }

    public static List<ReviewRun> Runs(Store store) =>
        Tasks.List(store).Where(t => t.Doc.Get("run").Length > 0)
            .GroupBy(t => t.Doc.Get("run"))
            .Select(g => new ReviewRun(g.Key,
                g.Where(t => t.Doc.Get("role") == "reviewer").OrderBy(t => t.Bee).ToList(),
                g.FirstOrDefault(t => t.Doc.Get("role") == "overseer")))
            .OrderByDescending(r => r.Id)
            .ToList();

    public static ReviewRun Find(Store store, string id) =>
        Runs(store).FirstOrDefault(r => r.Id == id.Trim()) ?? throw new HiveException($"No review {id}. `hive team runs` lists them.");

    public static void Stop(Store store, string id)
    {
        foreach (var t in Find(store, id).All.Where(t => t.Running)) Tasks.Stop(store, t.Id);
    }

    // What the overseer is told about the one table the hive reads from its report.
    const string ReviewersTable = "If your report has a table under a heading \"## Reviewers\" whose first column is a reviewer's name and second how many of its findings you confirmed (| reviewer | confirmed | …), the hive records honey for each reviewer with one or more and a miss for one with none.";

    // Once the overseer's report is in, each reviewer that finished gets honey
    // from the hive if the overseer confirmed any of its findings, and a miss if
    // it confirmed none: no opinion, only its count. The count is read from the
    // report's "## Reviewers" table (| reviewer | confirmed | …), which the
    // overseer's prompt describes: nothing else records findings per reviewer.
    // A reviewer with no row there, or no number in it, gets nothing, and so
    // does one the user (or the main session) already judged. Says what it gave.
    public static string Award(Store store, ReviewRun run)
    {
        if (run.Overseer is not { Status: "done" } over) return "";
        var counts = Confirmed(over.Result);
        var given = new List<string>();
        foreach (var t in run.Reviewers)
        {
            if (t.Status != "done" || t.Doc.Get("reward").Length > 0) continue;
            if (!counts.TryGetValue(t.Bee, out var n)) continue;
            var evidence = n > 0 ? $"{over.Bee} confirmed {n} of its findings in {run.Id} (task:{over.Id})"
                                 : $"{over.Bee} confirmed none of its findings in {run.Id} (task:{over.Id})";
            Tasks.Reward(store, t.Id, evidence, true, "hive", miss: n == 0);
            given.Add(n > 0 ? $"honey for {t.Bee}" : $"a miss for {t.Bee}");
        }
        return given.Count > 0 ? $"The hive gave {string.Join(", ", given)}, from {over.Bee}'s count of confirmed findings." : "";
    }

    // Reviewer -> confirmed, from the rows of the report's "## Reviewers"
    // table: the first cell is the reviewer, the column headed "confirmed"
    // starts with how many of its findings were confirmed ("10", "10 of 12
    // (one lowered)"). A table with no such column gives nothing.
    static Dictionary<string, int> Confirmed(string report)
    {
        var rows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var inTable = false;
        var column = -1; // the "confirmed" column: 0 until a Reviewers table's header row is read, -1 when it has none
        foreach (var raw in report.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#')) { inTable = line.TrimStart('#').Trim().StartsWith("Reviewers", StringComparison.OrdinalIgnoreCase); column = 0; continue; }
            if (!inTable || !line.StartsWith('|')) continue;
            var cells = line.Split('|');
            if (column == 0) { column = Array.FindIndex(cells, c => c.Trim().Trim('*', '`', ' ').Equals("confirmed", StringComparison.OrdinalIgnoreCase)); continue; }
            if (column < 2 || cells.Length < 4 || column >= cells.Length) continue;
            var name = cells[1].Trim().Trim('*', '`', ' ');
            var confirmed = cells[column].Trim().TrimStart('*');
            var digits = 0;
            while (digits < confirmed.Length && char.IsAsciiDigit(confirmed[digits])) digits++;
            if (name.Length > 0 && digits > 0 && int.TryParse(confirmed.AsSpan(0, digits), out var n)) rows.TryAdd(name, n);
        }
        return rows;
    }

    // What the overseer gets: each reviewer's report, in full up to a cap.
    public static string Reports(Store store, BeeTask overseer)
    {
        var parts = overseer.Doc.GetList("waits_for").Select(id => Tasks.Load(store, id)).Select(t =>
        {
            var result = t.Result.Trim();
            if (result.Length > 15000) result = result[..15000] + $"\n…(cut short; hive_read task:{t.Id} has the rest)";
            return $"## {t.Bee}'s report ({t.Status}, task:{t.Id})\n\n" + (t.Status == "done" ? result : $"{t.Bee} didn't finish ({t.Status}). " + result);
        });
        return string.Join("\n\n", parts);
    }
}
