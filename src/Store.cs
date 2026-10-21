using System.IO.Enumeration;
using System.Text;

namespace Hive;

record Bee(string Name, string Specialty, List<string> Projects, string Molt, string Path, Doc Doc)
{
    public bool MoltsFreely => Molt == "free";
}

record Project(string Name, string Summary, List<string> Paths, string Path, Doc Doc);

record Hit(string Id, string Kind, string Type, string Project, string Bee, string Created,
           string Title, string Summary, string Snippet, string SupersededBy, string Reward = "")
{
    public bool Rewarded => Reward == "yes";
}

// The hive on disk. Markdown files are the truth; .index.db is a search index
// rebuilt from them, so editing a file by hand (in Obsidian, say) is fine.
//
//   queen.md                  what every session is told
//   bees/<Name>.md            a worker's card
//   bees/.molts/<id>.md       card changes waiting for the user
//   bees/.history/            old cards and decided molts
//   projects/<name>/brief.md  a project's summary, folders and brief
//   comb/<project>/<id>.md    entries: facts, decisions, worklogs, lessons...
sealed partial class Store(string root) : IDisposable
{
    public static readonly string[] Types = ["fact", "decision", "worklog", "lesson", "preference", "note", "review"];
    const int SchemaVersion = 2;

    public readonly string Root = root;
    Db? db;

    public static string DefaultRoot =>
        Environment.GetEnvironmentVariable("HIVE_HOME") is { Length: > 0 } home ? Text.Expand(home) : Path.Combine(Text.Home, "Hive");

    public bool Exists => File.Exists(QueenPath);
    public string QueenPath => P("queen.md");
    string P(params string[] parts) => Path.Combine([Root, .. parts]);

    public void Init()
    {
        foreach (var dir in new[] { "bees", "projects", "comb/global" }) Directory.CreateDirectory(P(dir));
        if (!File.Exists(P(".gitignore"))) File.WriteAllText(P(".gitignore"), ".index.db*\n*.tmp\n");
        if (!File.Exists(QueenPath))
            File.WriteAllText(QueenPath, """
                # Queen

                <!-- This page is read at the start of every Claude session, so keep it short:
                who the user is and what every session must know. Everything else belongs
                in the comb, where Claude searches for it when it needs it. -->

                ## About the user

                ## Always

                """);
    }

    Db Db
    {
        get
        {
            if (db != null) return db;
            if (!Exists) throw new HiveException($"There is no hive at {Text.Tilde(Root)} yet. Run `hive init`.");
            db = new Db(P(".index.db"));
            db.Script("PRAGMA journal_mode=WAL;");
            if (db.Query("PRAGMA user_version", r => r.Long(0))[0] != SchemaVersion)
                db.Script($"""
                    DROP TABLE IF EXISTS docs;
                    DROP TABLE IF EXISTS fts;
                    CREATE TABLE docs(rid INTEGER PRIMARY KEY, id TEXT UNIQUE NOT NULL, path TEXT UNIQUE NOT NULL,
                        mtime INTEGER NOT NULL, kind TEXT, title TEXT, type TEXT, bee TEXT, project TEXT, tags TEXT,
                        created TEXT, summary TEXT, superseded_by TEXT, reward TEXT);
                    CREATE VIRTUAL TABLE fts USING fts5(title, summary, tags, body, tokenize='porter unicode61');
                    PRAGMA user_version={SchemaVersion};
                    """);
            dataVersion = db.Query("PRAGMA data_version", r => r.Long(0))[0];
            return db;
        }
    }

    public void Dispose() => db?.Dispose();

    // ---- the index ---------------------------------------------------------

    // Brings the index up to date with the files: a stat of each, and a re-read
    // of the ones that changed. Cheap enough to run before every read. Says
    // whether anything changed.
    public bool Sync()
    {
        if (synced) return false;
        // What the index holds, kept between syncs until another process
        // commits to it (data_version only counts other connections' commits).
        var version = Db.Query("PRAGMA data_version", r => r.Long(0))[0];
        if (known == null || version != knownVersion)
        {
            known = Db.Query("SELECT path, mtime FROM docs", r => (Path: r.Text(0), MTime: r.Long(1)))
                .ToDictionary(x => x.Path, x => x.MTime);
            knownVersion = version;
        }
        var seen = new HashSet<string>();
        var changed = new List<(string Rel, long MTime)>();
        foreach (var (rel, mtime) in Walk(Root, ""))
        {
            seen.Add(rel);
            if (!known.TryGetValue(rel, out var old) || old != mtime) changed.Add((rel, mtime));
        }
        var gone = known.Keys.Where(k => !seen.Contains(k)).ToList();
        var unreadable = 0;
        if (changed.Count > 0 || gone.Count > 0)
        {
            Db.Exec("BEGIN IMMEDIATE");
            try
            {
                foreach (var rel in gone) { ForgetPath(rel); known.Remove(rel); }
                // Cards and briefs first so an entry can't take their id.
                foreach (var (rel, mtime) in changed.OrderBy(c => c.Rel.StartsWith("comb/") ? 1 : 0))
                {
                    // A file that can't be read (a broken symlink, no permission,
                    // deleted since the walk) stays out of the index and out of
                    // known, so it's tried again next time; it doesn't stop the rest.
                    try { IndexFile(rel, mtime); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { unreadable++; continue; }
                    known[rel] = mtime;
                }
                Db.Exec("COMMIT");
            }
            catch
            {
                Db.Exec("ROLLBACK");
                known = null;
                throw;
            }
        }
        synced = batches > 0;
        var any = changed.Count > unreadable || gone.Count > 0;
        if (any) unseen = true;
        return any;
    }

    Dictionary<string, long>? known;
    long knownVersion;

    // A briefing runs a dozen searches and a refresh of the window a handful,
    // and every read syncs first. Inside a batch only the first read syncs;
    // the rest trust the index until the batch ends. Batches are for reads:
    // a write inside one isn't indexed until the next sync after it.
    int batches;
    bool synced;

    public Batch Reads()
    {
        if (batches++ == 0) synced = false;
        return new Batch(this);
    }

    public readonly struct Batch(Store store) : IDisposable
    {
        public void Dispose()
        {
            if (--store.batches == 0) store.synced = false;
        }
    }

    long dataVersion;
    bool unseen; // a sync changed the index since Changed() last asked

    // For the window: did the files change, or did another process (a Claude
    // session, a bee) commit to the index since we last asked? That counts
    // what any read synced in between (a search, a bee's activity), not only
    // this call's own sync.
    public bool Changed()
    {
        Sync();
        var version = Db.Query("PRAGMA data_version", r => r.Long(0))[0];
        var changed = unseen || version != dataVersion;
        (unseen, dataVersion) = (false, version);
        return changed;
    }

    // Did this bee save an entry since then (UTC ticks)?
    public bool SavedSince(string bee, long sinceTicks)
    {
        Sync();
        return Db.Query("SELECT 1 FROM docs WHERE kind = 'entry' AND bee = ? COLLATE NOCASE AND mtime >= ? LIMIT 1",
                        _ => 1, bee, sinceTicks).Count > 0;
    }

    public void Reindex()
    {
        Db.Exec("DELETE FROM docs");
        Db.Exec("DELETE FROM fts");
        known = null;
        synced = false;
        Sync();
    }

    // Each .md file under dir and its mtime, by its path from the root. Folders
    // starting with a dot (.history, .molts, .tasks) are left out.
    static IEnumerable<(string Rel, long MTime)> Walk(string dir, string rel)
    {
        // One stat per file, taken while listing the folder. A folder that
        // can't be read is skipped.
        var files = new FileSystemEnumerable<(string, long)>(dir,
            (ref FileSystemEntry e) => (rel + e.FileName.ToString(), e.LastWriteTimeUtc.UtcTicks), WalkOptions)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && e.FileName.EndsWith(".md"),
        };
        foreach (var file in files) yield return file;
        foreach (var sub in Directory.EnumerateDirectories(dir, "*", WalkOptions))
            if (Path.GetFileName(sub) is var name && !name.StartsWith('.'))
                foreach (var file in Walk(sub, rel + name + "/")) yield return file;
    }

    static readonly EnumerationOptions WalkOptions = new() { AttributesToSkip = 0, IgnoreInaccessible = true };

    void ForgetPath(string rel)
    {
        foreach (var rid in Db.Query("SELECT rid FROM docs WHERE path=?", r => r.Long(0), rel))
        {
            Db.Exec("DELETE FROM fts WHERE rowid=?", rid);
            Db.Exec("DELETE FROM docs WHERE rid=?", rid);
        }
    }

    void IndexFile(string rel, long mtime)
    {
        ForgetPath(rel);
        var doc = Doc.Load(P(rel));
        var parts = rel.Split('/');
        var stem = Path.GetFileNameWithoutExtension(rel);
        string kind, id, title, type = "", bee = "", project = "", summary = "", superseded = "";
        var created = doc.Get("created");
        var tags = string.Join(' ', doc.GetList("tags"));

        if (rel == "queen.md") { kind = "queen"; id = "queen"; title = "Queen"; }
        else if (parts is ["bees", _])
        {
            kind = "bee"; bee = doc.Get("name", stem); id = "bee:" + bee; title = bee;
            summary = doc.Get("specialty"); project = string.Join(", ", doc.GetList("projects"));
        }
        else if (parts is ["projects", var name, "brief.md"])
        {
            kind = "project"; id = "project:" + name; title = name; project = name; summary = doc.Get("summary");
        }
        else if (parts[0] == "comb")
        {
            kind = "entry"; id = stem; title = doc.Get("title", stem); type = doc.Get("type", "note");
            bee = doc.Get("bee"); project = doc.Get("project", parts.Length > 2 ? parts[1] : "global");
            summary = doc.Get("summary"); superseded = doc.Get("superseded_by");
        }
        else
        {
            kind = "note"; id = "note:" + rel; title = stem; summary = doc.Get("summary", doc.Get("description"));
        }

        // Two files claiming one id: keep both findable by giving this one its path.
        if (Db.Query("SELECT 1 FROM docs WHERE id=?", _ => 1, id).Count > 0) id = kind + ":" + rel;

        var rid = Db.Query("""
            INSERT INTO docs(id, path, mtime, kind, title, type, bee, project, tags, created, summary, superseded_by, reward)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?) RETURNING rid
            """, r => r.Long(0), id, rel, mtime, kind, title, type, bee, project, tags, created, summary, superseded,
            kind == "entry" ? doc.Get("reward") : "")[0];
        Db.Exec("INSERT INTO fts(rowid, title, summary, tags, body) VALUES(?,?,?,?,?)", rid, title, summary, tags, doc.Body);
    }

    // ---- search and read ---------------------------------------------------

    // Any word can match; BM25 puts the best first, weighting title over
    // summary over tags over body. An empty query lists the newest.
    public List<Hit> Search(string query, string? project = null, string? bee = null, string? type = null,
                            int limit = 8, bool includeSuperseded = false, bool withGlobal = true, string? kind = null)
    {
        Sync();
        var args = new List<object?>();
        var sql = new StringBuilder();
        var words = Text.Tokens(query).Distinct().ToList();
        const string cols = "d.id, d.kind, d.type, d.project, d.bee, d.created, d.title, d.summary";
        if (words.Count > 0)
        {
            sql.Append($"SELECT {cols}, snippet(fts, 3, '«', '»', '…', 14), d.superseded_by, d.reward FROM fts JOIN docs d ON d.rid = fts.rowid WHERE fts MATCH ?");
            args.Add(string.Join(" OR ", words.Select(w => "\"" + w + "\"")));
        }
        else sql.Append($"SELECT {cols}, '', d.superseded_by, d.reward FROM docs d WHERE d.kind = 'entry'");

        if (project is { Length: > 0 })
        {
            project = project.Trim().ToLowerInvariant();
            sql.Append(withGlobal ? " AND (d.project = ? OR d.project = 'global' OR d.kind IN ('queen', 'note'))" : " AND d.project = ?");
            args.Add(project);
        }
        if (bee is { Length: > 0 }) { sql.Append(" AND d.bee = ? COLLATE NOCASE"); args.Add(bee.Trim()); }
        if (type is { Length: > 0 }) { sql.Append(" AND d.type = ?"); args.Add(type.Trim().ToLowerInvariant()); }
        if (kind is { Length: > 0 }) { sql.Append(" AND d.kind = ?"); args.Add(kind); }
        if (!includeSuperseded) sql.Append(" AND d.superseded_by = ''");
        sql.Append(words.Count > 0 ? " ORDER BY bm25(fts, 10.0, 5.0, 3.0, 1.0)" : " ORDER BY d.created DESC, d.id DESC");
        sql.Append(" LIMIT ?");
        args.Add(limit);

        return Db.Query(sql.ToString(), r => new Hit(r.Text(0), r.Text(1), r.Text(2), r.Text(3), r.Text(4), r.Text(5),
                                                     r.Text(6), r.Text(7), r.Text(8), r.Text(9), r.Text(10)), [.. args]);
    }

    // How many live entries match, by type or by project (column), for the
    // same words and filters Search takes: Memory's filter chips and project list.
    public Dictionary<string, int> CountBy(string column, string query, string? project = null, string? type = null)
    {
        if (column is not ("type" or "project")) throw new ArgumentException(column);
        Sync();
        var args = new List<object?>();
        var sql = new StringBuilder();
        var words = Text.Tokens(query).Distinct().ToList();
        if (words.Count > 0)
        {
            sql.Append($"SELECT d.{column}, count(*) FROM fts JOIN docs d ON d.rid = fts.rowid WHERE fts MATCH ? AND d.kind = 'entry'");
            args.Add(string.Join(" OR ", words.Select(w => "\"" + w + "\"")));
        }
        else sql.Append($"SELECT d.{column}, count(*) FROM docs d WHERE d.kind = 'entry'");
        if (project is { Length: > 0 }) { sql.Append(" AND d.project = ?"); args.Add(project.Trim().ToLowerInvariant()); }
        if (type is { Length: > 0 }) { sql.Append(" AND d.type = ?"); args.Add(type.Trim().ToLowerInvariant()); }
        sql.Append($" AND d.superseded_by = '' GROUP BY d.{column}");
        var counts = new Dictionary<string, int>();
        // As "key\ncount" strings: a tuple here would add another generic List to the binary.
        foreach (var row in Db.Query(sql.ToString(), r => r.Text(0) + "\n" + r.Long(1), [.. args]))
            counts[row[..row.LastIndexOf('\n')]] = int.Parse(row[(row.LastIndexOf('\n') + 1)..]);
        return counts;
    }

    public static string Line(Hit h, bool snippet = false)
    {
        var sb = new StringBuilder($"- [{h.Id}] ");
        sb.Append(h.Kind == "entry" ? h.Type : h.Kind);
        if (h.Kind == "entry" && h.Project is not ("" or "global")) sb.Append(" · ").Append(h.Project);
        if (h.Kind == "entry" && h.Bee.Length > 0) sb.Append(" · ").Append(h.Bee);
        if (h.Created.Length >= 10) sb.Append(" · ").Append(h.Created[..10]);
        sb.Append(" — **").Append(h.Title).Append("**");
        if (h.Summary.Length > 0) sb.Append(": ").Append(h.Summary);
        if (h.Rewarded) sb.Append(" (earned honey)");
        else if (h.Reward == "no") sb.Append(" (a miss)");
        if (h.SupersededBy.Length > 0) sb.Append($" (superseded by {h.SupersededBy})");
        if (snippet && h.Snippet.Length > 0) sb.Append("\n    ").Append(Text.OneLine(h.Snippet));
        return sb.ToString();
    }

    public static string Lines(IEnumerable<Hit> hits, string none, bool snippets = false)
    {
        var lines = hits.Select(h => Line(h, snippets)).ToList();
        return lines.Count == 0 ? none : string.Join('\n', lines);
    }

    public string Read(string id)
    {
        if (id.Trim().StartsWith("task:")) return Tasks.Describe(this, id.Trim()[5..]);
        var (found, path) = Locate(id) ?? throw new HiveException($"Nothing has the id {id}. Ids look like 2026-10-18-some-title, bee:Name, project:name or queen; hive_recall finds them.");
        return $"[{found}] {Text.Tilde(path)}\n\n" + File.ReadAllText(path);
    }

    // The id as the index knows it, and the file's full path.
    public (string Id, string Path)? Locate(string id)
    {
        Sync();
        id = id.Trim();
        foreach (var candidate in new[] { id, "bee:" + id, "project:" + id })
        {
            var row = Db.Query("SELECT id, path FROM docs WHERE id = ? COLLATE NOCASE", r => (r.Text(0), r.Text(1)), candidate);
            if (row.Count > 0) return (row[0].Item1, P(row[0].Item2));
        }
        return null;
    }

    public (long Entries, long Bees, long Projects) Counts()
    {
        Sync();
        return (Db.Query("SELECT count(*) FROM docs WHERE kind = 'entry' AND superseded_by = ''", r => r.Long(0))[0],
                Bees().Count, Projects().Count);
    }

    // Per bee: how many live entries it wrote, and when it last wrote one.
    public Dictionary<string, (long Count, string Last)> BeeActivity()
    {
        Sync();
        return Db.Query("SELECT lower(bee), count(*), max(created) FROM docs WHERE kind = 'entry' AND superseded_by = '' GROUP BY lower(bee)",
                        r => (Bee: r.Text(0), Count: r.Long(1), Last: r.Text(2)))
                 .ToDictionary(x => x.Bee, x => (x.Count, x.Last));
    }

    // ---- entries -----------------------------------------------------------

    public string Deposit(string bee, string type, string title, string summary, string body,
                          string project, List<string> tags, List<string> supersedes)
    {
        Sync();
        bee = bee.Trim();
        bee = bee.Length == 0 || bee.Equals("queen", StringComparison.OrdinalIgnoreCase) ? "queen" : RequireBee(bee).Name;
        type = type.Trim().ToLowerInvariant();
        if (!Types.Contains(type)) throw new HiveException($"type is one of {string.Join(", ", Types)}.");
        title = Text.OneLine(title);
        summary = Text.OneLine(summary);
        if (title.Length == 0 || summary.Length == 0) throw new HiveException("An entry needs a title and a one-line summary.");
        project = project.Trim().ToLowerInvariant();
        if (project.Length == 0) project = "global";
        RequireProject(project);

        var replaced = new List<(string Id, string Path)>();
        foreach (var old in supersedes.Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            var row = Db.Query("SELECT id, path FROM docs WHERE id = ? AND kind = 'entry'", r => (r.Text(0), r.Text(1)), old);
            if (row.Count == 0) throw new HiveException($"supersedes: no entry has the id {old}.");
            replaced.Add(row[0]);
        }

        var stem = Text.Today + "-" + Text.Slug(title);
        var id = Unique(n => n == 1 ? stem : $"{stem}-{n}",
                        c => Db.Query("SELECT 1 FROM docs WHERE id = ?", _ => 1, c).Count > 0 || File.Exists(P("comb", project, c + ".md")));
        var doc = new Doc();
        doc.Set("title", title);
        doc.Set("type", type);
        doc.Set("bee", bee);
        doc.Set("project", project);
        doc.SetList("tags", tags.Select(t => string.Join('-', Text.Words(t))).Where(t => t.Length > 0).Distinct());
        doc.Set("created", Text.Now);
        doc.Set("summary", summary);
        if (replaced.Count > 0) doc.SetList("supersedes", replaced.Select(r => r.Id));
        doc.Body = body.Trim() + "\n";
        doc.Save(P("comb", project, id + ".md"));

        foreach (var (_, path) in replaced)
        {
            var old = Doc.Load(P(path));
            old.Set("superseded_by", id);
            old.Save(P(path));
        }
        Sync();
        return id;
    }

    static string Unique(Func<int, string> make, Func<string, bool> taken)
    {
        for (int n = 1; ; n++)
            if (make(n) is var candidate && !taken(candidate)) return candidate;
    }

    // ---- settings ------------------------------------------------------------

    // The hive's own settings: settings.md's front matter, if there is one.
    //   edit_commands: [make, ./build.sh]   what an edit task may run (each
    //                                       allows "<command> <anything>"; [] for none)
    public string SettingsPath => P("settings.md");

    public static readonly string[] DefaultEditCommands = ["make", "./build.sh"];

    // A command is words of letters, digits and . / - _ : = + (no wildcards,
    // quotes, commas or shell operators, which would change what it allows);
    // anything else is left out.
    public List<string> EditCommands()
    {
        Doc settings;
        try { settings = File.Exists(SettingsPath) ? Doc.Load(SettingsPath) : new Doc(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new HiveException($"Couldn't read {Text.Tilde(SettingsPath)}: {e.Message}"); }
        if (settings.Get("edit_commands").Length == 0) return [.. DefaultEditCommands];
        return CleanCommands(settings.GetList("edit_commands"));
    }

    // The commands that pass that rule, tidied, without repeats. Tasks.Args
    // checks a task file's list again with it, in case it was edited since.
    public static List<string> CleanCommands(IEnumerable<string> commands) =>
        commands.Select(c => string.Join(' ', c.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Where(c => c.Length > 0 && c.All(ch => char.IsAsciiLetterOrDigit(ch) || " ./-_:=+".Contains(ch))).Distinct().ToList();

    // ---- bees --------------------------------------------------------------

    public List<Bee> Bees() =>
        Directory.Exists(P("bees")) ? Directory.EnumerateFiles(P("bees"), "*.md").Order().Select(LoadBee).ToList() : [];

    static Bee LoadBee(string path)
    {
        var d = Doc.Load(path);
        return new Bee(d.Get("name", Path.GetFileNameWithoutExtension(path)), d.Get("specialty"), d.GetList("projects"),
                       d.Get("molt") == "free" ? "free" : "approve", path, d);
    }

    // What a bee's card can say it is (front matter role:), which the hive acts on:
    // reviewer and overseer make up the review team (Reviews.cs), evolver reviews
    // other bees' records (Tasks.Evolve, AutoEvolve) and its small rules apply
    // at once (SmallRule), keeper may tidy the hive and set project briefs in a
    // read task (Tasks.Args).
    public static readonly string[] Roles = ["reviewer", "overseer", "evolver", "keeper"];

    // The first bee with this role, if any.
    public Bee? RoleBee(string role) => Bees().FirstOrDefault(b => b.Doc.Get("role") == role);

    public Bee? FindBee(string name) =>
        Bees().FirstOrDefault(b => b.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public Bee RequireBee(string name) =>
        FindBee(name) ?? throw new HiveException($"No bee is named {name}. The bees: {Names(Bees().Select(b => b.Name))}.");

    static string Names(IEnumerable<string> names) => names.Any() ? string.Join(", ", names) : "none yet";

    public static string BeeLine(Bee b) =>
        $"- **{b.Name}** (subagent `{Agents.Name(b.Name)}`) — {b.Specialty}" +
        (b.Projects.Count > 0 ? $" · projects: {string.Join(", ", b.Projects)}" : "") +
        (b.MoltsFreely ? " · may change its own card" : " · card changes need the user's approval");

    // Throws unless a new bee can have this name.
    public void CheckNewBee(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 32 || !char.IsAsciiLetter(name[0]) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new HiveException("A bee's name starts with a letter and has only letters, digits and dashes, up to 32 long.");
        // "queen" is the main session's name: the briefing and deposits treat it as the Queen.
        if (name.Equals("queen", StringComparison.OrdinalIgnoreCase)) throw new HiveException("Queen is the main session's name; give the bee another.");
        if (FindBee(name) is { } existing) throw new HiveException($"There is already a bee named {existing.Name}.");
        Agents.CheckFree(name);
    }

    public Bee CreateBee(string name, string specialty, List<string> projects, string instructions, string molt, string role = "", string access = "")
    {
        name = name.Trim();
        CheckNewBee(name);
        specialty = Text.OneLine(specialty);
        if (specialty.Length == 0) throw new HiveException("A bee needs a specialty.");
        projects = projects.Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0).Distinct().ToList();
        foreach (var p in projects) RequireProject(p);
        if (molt is not ("free" or "approve")) throw new HiveException("molt is free or approve.");

        var doc = new Doc();
        doc.Set("name", name);
        doc.Set("specialty", specialty);
        doc.SetList("projects", projects);
        doc.Set("molt", molt);
        if (role.Length > 0) doc.Set("role", role);
        if (access.Length > 0) doc.Set("access", access);
        doc.Set("created", Text.Today);
        if (role.Length > 0 && !Roles.Contains(role)) throw new HiveException($"A role is one of {string.Join(", ", Roles)}.");
        var how = instructions.Trim();
        doc.Body = $"# {name}\n\n## How I work\n\n{(how.Length > 0 ? how + "\n\n" : "")}## Rules I've learned\n\nNone yet.\n";
        var path = P("bees", name + ".md");
        doc.Save(path);
        var bee = LoadBee(path);
        Agents.Write(bee);
        return bee;
    }

    // role: one of Roles, or "" for none.
    public Bee UpdateBee(string name, string? molt, string? specialty, List<string>? projects, List<string>? skills = null, string? role = null)
    {
        var bee = RequireBee(name);
        if (role != null)
        {
            role = role.Trim().ToLowerInvariant();
            if (role is "" or "none") bee.Doc.Remove("role");
            else if (Roles.Contains(role)) bee.Doc.Set("role", role);
            else throw new HiveException($"A role is one of {string.Join(", ", Roles)}, or none.");
        }
        if (molt != null)
        {
            if (molt is not ("free" or "approve")) throw new HiveException("molt is free or approve.");
            bee.Doc.Set("molt", molt);
        }
        if (specialty != null) bee.Doc.Set("specialty", specialty);
        if (projects != null)
        {
            projects = projects.Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0).Distinct().ToList();
            foreach (var p in projects) RequireProject(p);
            bee.Doc.SetList("projects", projects);
        }
        if (skills != null)
        {
            skills = skills.Select(k => k.Trim().TrimStart('/')).Where(k => k.Length > 0).Distinct().ToList();
            if (skills.Count > 0) bee.Doc.SetList("skills", skills); else bee.Doc.Remove("skills");
        }
        bee.Doc.Save(bee.Path);
        bee = LoadBee(bee.Path);
        Agents.Write(bee);
        return bee;
    }

    // The user changing a card by hand: no molt, but the old card is kept.
    public void EditCard(string beeName, string card) => ApplyCard(RequireBee(beeName), card);

    // Retiring keeps the card in .history and removes the bee's subagent.
    public void RetireBee(string beeName)
    {
        var bee = RequireBee(beeName);
        Directory.CreateDirectory(P("bees", ".history"));
        File.Move(bee.Path, P("bees", ".history", $"{bee.Name}-retired-{DateTime.Now:yyyyMMdd-HHmmss}.md"));
        Agents.Remove(bee.Name);
    }

    // Undoes RetireBee: the card the bee was last retired with comes back,
    // and its subagent with it.
    public void RestoreBee(string beeName)
    {
        if (FindBee(beeName) is { } there) throw new HiveException($"There is already a bee named {there.Name}.");
        var retired = Directory.Exists(P("bees", ".history"))
            ? Directory.EnumerateFiles(P("bees", ".history"), beeName.Trim() + "-retired-*.md").Order().LastOrDefault() : null;
        if (retired == null) throw new HiveException($"No retired bee is named {beeName}.");
        var path = P("bees", Path.GetFileName(retired)[..(beeName.Trim().Length)] + ".md");
        File.Move(retired, path);
        Agents.Write(LoadBee(path));
    }

    public string QueenText => File.ReadAllText(QueenPath);
    // Every session's hook reads this page, so it's written to a temp file and
    // moved into place: a reader sees the old page or the new one, never half.
    public void SaveQueen(string text)
    {
        var tmp = $"{QueenPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, text.TrimEnd() + "\n");
        File.Move(tmp, QueenPath, overwrite: true);
    }

    // ---- molts: a bee changing its own card ----------------------------------

    // A bee may change its own card if the user lets it. A change proposed by
    // anyone else waits for the user, unless it comes from the bee with the
    // evolver role and is a small rule (SmallRule) for another bee: that
    // applies at once, recorded as approved with applied: auto, and
    // ReopenMolt undoes it. A bee's own small rule, or the Queen's, waits.
    public string Molt(string beeName, string reason, string card, string by = "")
    {
        var bee = RequireBee(beeName);
        if (card.Trim().Length == 0) throw new HiveException("card is the bee's whole new card, in Markdown.");
        reason = Text.OneLine(reason);
        by = by.Trim();
        if (by.Length == 0 || by.Equals(bee.Name, StringComparison.OrdinalIgnoreCase)) by = bee.Name;
        else if (!by.Equals("queen", StringComparison.OrdinalIgnoreCase)) by = RequireBee(by).Name;
        var own = by == bee.Name;
        if (own && bee.MoltsFreely)
        {
            ApplyCard(bee, card);
            return $"{bee.Name}'s card is changed. The old one is in {Text.Tilde(P("bees", ".history"))}.";
        }
        var stem = $"{Text.Today}-{bee.Name.ToLowerInvariant()}";
        // Decided molts keep their id in .history, so a new one can't reuse it.
        var id = Unique(n => n == 1 ? stem : $"{stem}-{n}", c => File.Exists(P("bees", ".molts", c + ".md"))
            || File.Exists(P("bees", ".history", c + ".approved.md")) || File.Exists(P("bees", ".history", c + ".rejected.md")));
        var doc = new Doc();
        doc.Set("bee", bee.Name);
        doc.Set("by", by);
        doc.Set("reason", reason);
        doc.Set("created", Text.Now);
        doc.Body = card.Trim() + "\n";
        if (!own && FindBee(by)?.Doc.Get("role") == "evolver" && SmallRule(bee, card))
        {
            ApplyCard(bee, card);
            doc.Set("applied", "auto");
            doc.Save(P("bees", ".history", id + ".approved.md"));
            return $"It only adds rules backed by {bee.Name}'s honey and misses, so molt {id} applied at once. The user sees it in their inbox and can undo it.";
        }
        doc.Save(P("bees", ".molts", id + ".md"));
        return own
            ? $"{bee.Name} needs the user's approval to change its card, so molt {id} is waiting for them (`hive molts`)."
            : $"Changes to another bee's card wait for the user, so molt {id} ({by} → {bee.Name}) is waiting for them (`hive molts`).";
    }

    // A card may come with front matter of its own: a bee that reads its card
    // with hive_read gets the whole file, under an "[bee:Name] path" line, and
    // proposes it back. Only the text under it is the card. Of its fields, the
    // specialty and projects apply; whether a bee may molt, and its access, are
    // the user's to set. More blocks under the first (a card saved that way
    // before) are dropped.
    static string SplitCard(string card, out string? specialty, out List<string>? projects)
    {
        (specialty, projects) = (null, null);
        card = card.Trim();
        if (card.StartsWith("[bee:") && card.IndexOf('\n') is var nl and > 0) card = card[(nl + 1)..].Trim();
        for (bool first = true; card.StartsWith("---"); first = false)
        {
            var d = Doc.Parse(card);
            if (!d.HasFields) break;
            if (first)
            {
                if (d.Get("specialty") is { Length: > 0 } sp) specialty = sp;
                if (d.Get("projects").Length > 0) projects = d.GetList("projects");
            }
            card = d.Body.Trim();
        }
        return card + "\n";
    }

    // A change small enough to apply without the user: the card stays the
    // same (specialty and projects too) except for lines added at the end of
    // its "## Rules I've learned" section (in place of a lone "None yet."),
    // and every one of those is a list item (never a heading) that cites two
    // or more of the bee's outcomes (honey or misses, by id).
    bool SmallRule(Bee bee, string card)
    {
        var body = SplitCard(card, out var specialty, out var projects);
        if (specialty != null && specialty != bee.Specialty) return false;
        if (projects != null && string.Join(",", projects.Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0).Distinct()) != string.Join(",", bee.Projects)) return false;
        var old = bee.Doc.Body.Trim().Split('\n');
        var now = body.Trim().Split('\n');
        var heading = Array.FindIndex(old, l => l.Trim().Replace('\u2019', '\'') == "## Rules I've learned");
        if (heading < 0) return false;
        // The new lines go after the section's last line, before the next heading.
        var end = heading + 1;
        while (end < old.Length && !old[end].TrimStart().StartsWith('#')) end++;
        while (end > heading + 1 && old[end - 1].Trim().Length == 0) end--;
        // A section that only says "None yet." is empty: the first rules take that line's place.
        var said = -1;
        for (int i = heading + 1; i < end; i++)
            if (old[i].Trim().Length > 0) said = said == -1 ? i : -2;
        if (said >= 0 && old[said].Trim().TrimEnd('.').Equals("None yet", StringComparison.OrdinalIgnoreCase))
        {
            old = [.. old[..said], .. old[(said + 1)..]];
            end = said;
        }
        var added = now.Length - old.Length;
        if (added <= 0) return false;
        for (int i = 0; i < old.Length; i++)
            if (old[i] != now[i < end ? i : i + added]) return false;
        var outcomes = new HashSet<string>();
        foreach (var o in Outcomes(bee)) outcomes.Add(o.Id);
        var rules = 0;
        for (int i = end; i < end + added; i++)
        {
            if (now[i].Trim().Length == 0) continue;
            if (!now[i].TrimStart().StartsWith("- ") && !now[i].TrimStart().StartsWith("* ")) return false; // a rule is a list item
            var cited = new HashSet<string>();
            foreach (var word in now[i].Split([' ', '\t', '[', ']', '(', ')', ',', ';', '.', '`', '"', '\'', '*'], StringSplitOptions.RemoveEmptyEntries))
                if (outcomes.Contains(word)) cited.Add(word);
                else if (outcomes.Contains("task:" + word)) cited.Add("task:" + word);
            if (cited.Count < 2) return false;
            rules++;
        }
        return rules > 0;
    }

    // Card changes that applied at once (SmallRule), oldest first, for the inbox.
    public List<(string Id, Doc Doc)> AutoApplied()
    {
        var applied = new List<(string Id, Doc Doc)>();
        if (Directory.Exists(P("bees", ".history")))
            foreach (var f in Directory.EnumerateFiles(P("bees", ".history"), "*.approved.md").Order())
                if (Doc.Load(f) is var d && d.Get("applied") == "auto")
                    applied.Add((Path.GetFileName(f)[..^".approved.md".Length], d));
        return applied;
    }

    void ApplyCard(Bee bee, string card, string snapshotTag = "")
    {
        card = SplitCard(card, out var specialty, out var projects);
        projects = projects?.Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0).Distinct().ToList();
        foreach (var p in projects ?? []) RequireProject(p);
        Directory.CreateDirectory(P("bees", ".history"));
        // Two changes in one second get <stamp>-2, -3...: each keeps its snapshot (Snapshots sorts them).
        var stamp = $"{bee.Name}-{DateTime.Now:yyyyMMdd-HHmmss}";
        var snapshot = Unique(n => n == 1 ? stamp : $"{stamp}-{n}", c => File.Exists(P("bees", ".history", c + snapshotTag + ".md")));
        File.Copy(bee.Path, P("bees", ".history", snapshot + snapshotTag + ".md"));
        bee.Doc.Body = card;
        if (specialty != null) bee.Doc.Set("specialty", specialty);
        if (projects != null) bee.Doc.SetList("projects", projects);
        bee.Doc.Set("molted", Text.Now);
        bee.Doc.Save(bee.Path);
        Agents.Write(LoadBee(bee.Path));
    }

    public List<(string Id, Doc Doc)> PendingMolts() =>
        Directory.Exists(P("bees", ".molts"))
            ? Directory.EnumerateFiles(P("bees", ".molts"), "*.md")
                .Select(f => (Id: Path.GetFileNameWithoutExtension(f), Doc: Doc.Load(f)))
                .OrderBy(m => m.Doc.Get("created")).ThenBy(m => m.Id.Length).ThenBy(m => m.Id).ToList()
            : [];

    // A waiting molt as a diff against the bee's card now, with the
    // specialty and projects on top when its front matter changes them.
    public string MoltDiff(Doc molt)
    {
        if (FindBee(molt.Get("bee")) is not { } bee) return "(that bee is gone)";
        var card = SplitCard(molt.Body, out var specialty, out var projects);
        if (specialty == null && projects == null) return Text.Diff(bee.Doc.Body.Trim() + "\n", card);
        string Head(string sp, List<string> ps) => $"specialty: {sp}\nprojects: {string.Join(", ", ps)}\n\n";
        return Text.Diff(Head(bee.Specialty, bee.Projects) + bee.Doc.Body.Trim() + "\n",
                         Head(specialty ?? bee.Specialty, projects ?? bee.Projects) + card);
    }

    public Doc PendingMolt(string id)
    {
        var path = P("bees", ".molts", id.Trim() + ".md");
        if (!File.Exists(path)) throw new HiveException($"No molt {id} is waiting. `hive molts` lists them.");
        return Doc.Load(path);
    }

    public void DecideMolt(string id, bool approve)
    {
        var doc = PendingMolt(id);
        if (approve) ApplyCard(RequireBee(doc.Get("bee")), doc.Body);
        Directory.CreateDirectory(P("bees", ".history"));
        File.Move(P("bees", ".molts", id + ".md"), P("bees", ".history", $"{id}.{(approve ? "approved" : "rejected")}.md"), overwrite: true);
    }

    // Undoes DecideMolt: the molt waits again, and an approved one's card
    // change is undone (unless the card changed again since).
    public void ReopenMolt(string id)
    {
        id = id.Trim();
        var approved = P("bees", ".history", id + ".approved.md");
        var decided = File.Exists(approved) ? approved : P("bees", ".history", id + ".rejected.md");
        if (!File.Exists(decided)) throw new HiveException($"Molt {id} wasn't decided.");
        if (decided == approved)
        {
            var doc = Doc.Load(approved);
            var bee = RequireBee(doc.Get("bee"));
            var card = SplitCard(doc.Body, out var specialty, out var projects);
            if (bee.Doc.Body.Trim() != card.Trim()) throw new HiveException($"{bee.Name}'s card changed again since; undo that first.");
            UndoCard(bee.Name, fields: specialty != null || projects != null);
        }
        Directory.CreateDirectory(P("bees", ".molts"));
        File.Move(decided, P("bees", ".molts", id + ".md"));
        // One that applied at once waits now like any other.
        if (Doc.Load(P("bees", ".molts", id + ".md")) is var waits && waits.Get("applied").Length > 0)
        {
            waits.Remove("applied");
            waits.Save(P("bees", ".molts", id + ".md"));
        }
    }

    // ---- projects ----------------------------------------------------------

    public List<Project> Projects() =>
        Directory.Exists(P("projects"))
            ? Directory.EnumerateDirectories(P("projects")).Order()
                .Where(d => File.Exists(Path.Combine(d, "brief.md")))
                .Select(d => LoadProject(Path.Combine(d, "brief.md"))).ToList()
            : [];

    static Project LoadProject(string path)
    {
        var d = Doc.Load(path);
        return new Project(Path.GetFileName(Path.GetDirectoryName(path)!), d.Get("summary"), d.GetList("paths"), path, d);
    }

    public Project? FindProject(string name) => Projects().FirstOrDefault(p => p.Name == name.Trim().ToLowerInvariant());

    public void RequireProject(string name)
    {
        if (name != "global" && FindProject(name) == null)
            throw new HiveException($"No project is named {name}. The projects: {Names(Projects().Select(p => p.Name))} (or global).");
    }

    public Project SetProject(string name, string? summary, List<string>? paths, string? brief)
    {
        name = name.Trim().ToLowerInvariant();
        if (name.Length == 0 || name == "global" || !char.IsAsciiLetterOrDigit(name[0])
            || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new HiveException("A project's name is lower-case letters, digits, dashes, dots or underscores (and not 'global').");
        var path = P("projects", name, "brief.md");
        var isNew = !File.Exists(path);
        if (isNew && string.IsNullOrWhiteSpace(summary)) throw new HiveException("A new project needs a one-line summary.");
        var doc = isNew ? new Doc { Body = $"# {name}\n" } : Doc.Load(path);
        if (summary != null) doc.Set("summary", summary);
        if (paths != null) doc.SetList("paths", paths.Select(Text.Tilde));
        if (isNew) doc.Set("created", Text.Today);
        if (brief != null) doc.Body = brief.Trim() + "\n";
        doc.Save(path);
        Directory.CreateDirectory(P("comb", name));
        return LoadProject(path);
    }

    // The project whose folder holds cwd; the deepest one wins.
    public Project? ProjectFor(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return null;
        var full = Text.Expand(cwd);
        return Projects()
            .SelectMany(p => p.Paths.Select(path => (Project: p, Path: Text.Expand(path))))
            .Where(x => full == x.Path || full.StartsWith(x.Path + "/"))
            .OrderByDescending(x => x.Path.Length)
            .Select(x => x.Project)
            .FirstOrDefault();
    }

    // ---- briefings ---------------------------------------------------------

    public string Brief(string? projectName, string? beeName, string? cwd) => Brief(projectName, beeName, cwd, null, null, true);

    // The briefing as it would be with this Queen page or this project brief
    // (unsaved text in the window), and, uncapped, how long it would run.
    public string Brief(string? projectName, string? beeName, string? cwd, string? queenText, string? projectBrief, bool cap)
    {
        using var batch = Reads();
        Sync();
        var current = projectName is { Length: > 0 }
            ? FindProject(projectName) ?? throw new HiveException($"No project is named {projectName}.")
            : ProjectFor(cwd);
        if (beeName is { Length: > 0 } && !beeName.Equals("queen", StringComparison.OrdinalIgnoreCase))
            return BeeBrief(RequireBee(beeName), current, projectBrief, cap);

        var sb = new StringBuilder();
        sb.AppendLine("# The Hive: the Queen's briefing").AppendLine();
        sb.AppendLine($"The user's long-term memory is kept in {Text.Tilde(Root)}. This is the short version; hive_recall finds the rest.").AppendLine();
        // The page's own "# Queen" heading would only repeat the one above.
        var queen = Text.StripComments(queenText ?? Doc.Load(QueenPath).Body).Trim();
        if (queen.StartsWith("# ")) queen = queen[(queen.IndexOf('\n') is var nl and >= 0 ? nl : queen.Length)..].Trim();
        if (queen.Length > 0) sb.AppendLine(Text.Cap(queen, 2500)).AppendLine();

        var bees = Bees();
        sb.AppendLine("## Bees").AppendLine();
        if (bees.Count == 0) sb.AppendLine("No bees yet (`hive new-bee`, or bee_create).");
        else foreach (var b in bees) sb.AppendLine(BeeLine(b));
        sb.AppendLine();

        var projects = Projects();
        if (projects.Count > 0)
        {
            sb.AppendLine("## Projects").AppendLine();
            foreach (var p in projects)
                sb.AppendLine($"- **{p.Name}** — {p.Summary}" +
                              (p.Paths.Count > 0 ? $" ({string.Join(", ", p.Paths)})" : "") +
                              (p.Name == current?.Name ? " ← this session is here" : ""));
            sb.AppendLine();
        }

        if (current != null)
        {
            sb.AppendLine($"## This session's project: {current.Name}").AppendLine();
            var brief = (projectBrief ?? current.Doc.Body).Trim();
            if (HasBrief(current.Name, brief)) sb.AppendLine(Text.Cap(brief, 3000)).AppendLine();
            sb.AppendLine($"### Latest in {current.Name}");
            sb.AppendLine(Lines(Search("", current.Name, limit: 6, withGlobal: false), "Nothing yet.")).AppendLine();
        }
        else
        {
            sb.AppendLine("## Latest in the hive");
            sb.AppendLine(Lines(Search("", limit: 6), "Nothing yet.")).AppendLine();
        }

        // What waits for the user is never the part that gets cut.
        var waiting = PendingMolts().Count is var n and > 0
            ? $"\n## Waiting for the user\n\n{n} card change{(n == 1 ? "" : "s")} proposed by bees wait{(n == 1 ? "s" : "")} for the user's approval: the Hive window's inbox, the bee's page, or `hive molts`.\n"
            : "";
        var text = sb.ToString().TrimEnd() + "\n";
        return (cap ? Text.Cap(text, BriefLimit - waiting.Length) : text) + waiting;
    }

    // Claude Code hands a hook's context over whole only up to 10,000
    // characters; past that, Claude gets a 2,000-character preview.
    public const int BriefLimit = 9000;

    static bool HasBrief(Project p) => HasBrief(p.Name, p.Doc.Body.Trim());
    static bool HasBrief(string name, string brief) => brief.Length > 0 && brief != "# " + name;

    string BeeBrief(Bee bee, Project? current, string? projectBrief = null, bool cap = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Bee brief: {bee.Name}").AppendLine();
        sb.AppendLine($"Specialty: {bee.Specialty}");
        sb.AppendLine($"Projects: {(bee.Projects.Count > 0 ? string.Join(", ", bee.Projects) : "any")}");
        sb.AppendLine(bee.MoltsFreely
            ? "Your card: you may change it yourself with bee_molt."
            : "Your card: changes you propose with bee_molt wait for the user's approval.");
        sb.AppendLine().AppendLine("## Your card").AppendLine().AppendLine(Text.Cap(bee.Doc.Body.Trim(), 3500)).AppendLine();
        if (BestWork(bee) is { Length: > 0 } best) sb.Append(best).AppendLine();

        var projects = bee.Projects.Select(FindProject).OfType<Project>().ToList();
        if (current != null && projects.All(p => p.Name != current.Name)) projects.Insert(0, current);
        foreach (var p in projects)
        {
            sb.AppendLine($"## Project {p.Name}: {p.Summary}");
            var brief = (p.Name == current?.Name ? projectBrief ?? p.Doc.Body : p.Doc.Body).Trim();
            if (p.Name == current?.Name && HasBrief(p.Name, brief)) sb.AppendLine().AppendLine(Text.Cap(brief, 2500));
            sb.AppendLine();
        }

        var lessons = Search(bee.Specialty, type: "lesson", limit: 6)
            .Concat(projects.SelectMany(p => Search("", p.Name, type: "lesson", limit: 4)))
            .Concat(Search("", "global", type: "lesson", limit: 3, withGlobal: false))
            .DistinctBy(h => h.Id).Take(12).ToList();
        sb.AppendLine("## Lessons the hive has learned").AppendLine(Lines(lessons, "None yet.")).AppendLine();

        var others = projects.SelectMany(p => Search("", p.Name, type: "worklog", limit: 6, withGlobal: false))
            .Where(h => !h.Bee.Equals(bee.Name, StringComparison.OrdinalIgnoreCase)).DistinctBy(h => h.Id).Take(6);
        sb.AppendLine("## Recent work by others in your projects").AppendLine(Lines(others, "None yet.")).AppendLine();
        sb.AppendLine("## Your recent work").AppendLine(Lines(Search("", bee: bee.Name, type: "worklog", limit: 5), "None yet."));
        return cap ? Text.Cap(sb.ToString(), BriefLimit) : sb.ToString();
    }

    // ---- tidying -----------------------------------------------------------

    public string Tidy()
    {
        Sync();
        var sb = new StringBuilder();
        var live = Db.Query("SELECT id, project, title, summary FROM docs WHERE kind = 'entry' AND superseded_by = ''",
                            r => (Id: r.Text(0), Project: r.Text(1), Words: Text.Tokens(r.Text(2) + " " + r.Text(3)).ToHashSet()));

        var pairs = new List<(double Score, string A, string B)>();
        foreach (var group in live.GroupBy(e => e.Project))
        {
            var items = group.ToList();
            for (int i = 0; i < items.Count; i++)
                for (int j = i + 1; j < items.Count; j++)
                {
                    var (a, b) = (items[i].Words, items[j].Words);
                    if (a.Count == 0 || b.Count == 0) continue;
                    var shared = a.Count(b.Contains);
                    var score = (double)shared / (a.Count + b.Count - shared);
                    if (score >= 0.5) pairs.Add((score, items[i].Id, items[j].Id));
                }
        }
        sb.AppendLine("## Likely duplicates");
        sb.AppendLine(pairs.Count == 0 ? "None." : string.Join('\n', pairs.OrderByDescending(p => p.Score).Take(25)
            .Select(p => $"- {p.A} and {p.B} ({p.Score * 100:0}% of their words shared)")));
        if (pairs.Count > 0) sb.AppendLine("Merge them with hive_deposit (supersedes = both ids) when they say the same thing.");
        sb.AppendLine();

        var broken = Db.Query("SELECT id, superseded_by FROM docs WHERE kind = 'entry' AND superseded_by <> '' AND superseded_by NOT IN (SELECT id FROM docs)",
                              r => $"- {r.Text(0)} says it was superseded by {r.Text(1)}, which isn't in the hive");
        if (broken.Count > 0) sb.AppendLine("## Broken links").AppendLine(string.Join('\n', broken)).AppendLine();

        var bare = Db.Query("SELECT id FROM docs WHERE kind = 'entry' AND superseded_by = '' AND summary = ''", r => r.Text(0));
        if (bare.Count > 0) sb.AppendLine("## Entries with no summary").AppendLine(string.Join('\n', bare.Select(b => "- " + b))).AppendLine();

        var restored = new List<string>();
        foreach (var bee in Bees())
            if (!Agents.Exists(bee.Name)) { Agents.Write(bee); restored.Add(bee.Name); }
        if (restored.Count > 0) sb.AppendLine($"## Subagents restored\n{string.Join(", ", restored)}").AppendLine();

        var counts = Db.Query("SELECT count(*), sum(superseded_by <> '') FROM docs WHERE kind = 'entry'", r => (r.Long(0), r.Long(1)))[0];
        sb.AppendLine($"The comb holds {counts.Item1} entr{(counts.Item1 == 1 ? "y" : "ies")}, {counts.Item2} of them superseded.");
        if (PendingMolts().Count is var n and > 0) sb.AppendLine($"{n} molt{(n == 1 ? "" : "s")} wait for the user (`hive molts`).");
        return sb.ToString().TrimEnd();
    }
}
