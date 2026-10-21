using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hive;

static class Json
{
    static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonWriterOptions Pretty = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = true };

    public static byte[] Bytes(JsonNode node, bool indented = false)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, indented ? Pretty : Compact)) node.WriteTo(w);
        return ms.ToArray();
    }

    public static string Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToJsonString() ?? "";
}

// The Model Context Protocol over stdio: one JSON-RPC message per line. Only
// what a tools server needs: initialize, ping, tools/list and tools/call.
static class Mcp
{
    // Set by Tasks.Run for a task's session: the bee it runs as. A card change
    // from that session is that bee's, whatever `by` says, so a task can't
    // pass itself off as another bee changing its own card.
    public const string TaskBeeVariable = "HIVE_TASK_BEE";

    public const string Instructions = """
        The Hive is the user's long-term memory on this computer, kept as Markdown: entries (facts, decisions, worklogs, lessons, preferences, notes, reviews), bees (Claude Code subagents, each with a card), projects with briefs, and background tasks. None of it is in your context until a tool returns it.
        - hive_recall searches it, one line per hit; hive_read returns a whole entry, card (bee:Name), brief (project:name), task (task:id) or the queen page; hive_deposit saves an entry, and its supersedes replaces older ones; hive_brief returns a session's or a bee's briefing.
        - The hive's files change through these tools, which keep its search index current.
        - "(earned honey)" and "(a miss)" on a hit mean the work was judged: by the user, with bee_reward, or from a team review's count of confirmed findings.
        - bee_list, bee_create, bee_review, bee_reward and bee_molt work on bees. A card change waits for the user's approval unless the bee may change its own card.
        - team_review_begin and team_review_report run a review by the bees with the reviewer and overseer roles.
        """;

    static readonly JsonArray Tools = JsonNode.Parse("""
        [
          {
            "name": "hive_brief",
            "description": "The briefing. Without bee: the queen page, the bees, the projects and the latest in this session's project. With bee: that bee's card, its projects, the hive's lessons and others' recent work.",
            "inputSchema": { "type": "object", "properties": {
              "project": { "type": "string", "description": "Project name. Left out, it's the project whose folder this session is in." },
              "bee": { "type": "string", "description": "A bee's name, for that bee's briefing." } } }
          },
          {
            "name": "hive_recall",
            "description": "Search the hive: entries, bee cards, project briefs and notes. One line per hit, best first; hive_read an id for the whole thing. An empty query lists the newest entries.",
            "inputSchema": { "type": "object", "properties": {
              "query": { "type": "string", "description": "Words to look for. Any of them can match; better matches come first." },
              "project": { "type": "string", "description": "Only this project (hive-wide entries still show)." },
              "bee": { "type": "string", "description": "Only what this bee wrote." },
              "type": { "type": "string", "enum": ["fact", "decision", "worklog", "lesson", "preference", "note", "review"] },
              "limit": { "type": "integer", "description": "Most hits to return, default 8." },
              "include_superseded": { "type": "boolean", "description": "Also show entries that were replaced." } },
              "required": ["query"] }
          },
          {
            "name": "hive_read",
            "description": "The whole of one entry, bee card (bee:Name), project brief (project:name), task with its result (task:id) or the Queen's page (queen).",
            "inputSchema": { "type": "object", "properties": { "id": { "type": "string" } }, "required": ["id"] }
          },
          {
            "name": "hive_deposit",
            "description": "Save something so it outlives this session. Returns the new entry's id.",
            "inputSchema": { "type": "object", "properties": {
              "bee": { "type": "string", "description": "Who is writing: a bee's name, or queen for the main session." },
              "type": { "type": "string", "enum": ["fact", "decision", "worklog", "lesson", "preference", "note", "review"],
                        "description": "fact: true about the user or a project. decision: a choice and why. worklog: what was done, why, what's left. lesson: what others should know next time. preference: how the user likes things. review: a team review's final report. note: anything else." },
              "title": { "type": "string", "description": "A short title." },
              "summary": { "type": "string", "description": "One line that says the thing; it's what search results show." },
              "body": { "type": "string", "description": "Details in Markdown: the why, the how, file paths." },
              "project": { "type": "string", "description": "The project it belongs to; left out, it's hive-wide." },
              "tags": { "type": "array", "items": { "type": "string" } },
              "supersedes": { "type": "array", "items": { "type": "string" }, "description": "Ids this replaces. They stay readable but drop out of search." } },
              "required": ["bee", "type", "title", "summary"] }
          },
          {
            "name": "hive_tidy",
            "description": "What needs tidying: likely duplicates, broken links, entries without summaries, molts waiting. Fix duplicates with hive_deposit and supersedes.",
            "inputSchema": { "type": "object", "properties": {} }
          },
          {
            "name": "bee_list",
            "description": "The bees: name, specialty, projects, and whether each may change its own card.",
            "inputSchema": { "type": "object", "properties": {} }
          },
          {
            "name": "bee_create",
            "description": "Create a bee and its Claude Code subagent. Its card changes need the user's approval unless the user lets it molt freely (`hive bee <name> --molt free`). The subagent may only appear in a new session.",
            "inputSchema": { "type": "object", "properties": {
              "name": { "type": "string", "description": "A letter, then letters, digits or dashes." },
              "specialty": { "type": "string", "description": "What it's for, in one line." },
              "projects": { "type": "array", "items": { "type": "string" }, "description": "Projects it works on; none means any." },
              "instructions": { "type": "string", "description": "How it should work, in Markdown. It becomes the card's 'How I work'." } },
              "required": ["name", "specialty"] }
          },
          {
            "name": "bee_review",
            "description": "Everything about one bee: its card, how the card changed, its tasks and how they went, its honey and misses (who gave each, with the note), and lessons other bees learned in its area.",
            "inputSchema": { "type": "object", "properties": { "bee": { "type": "string" } }, "required": ["bee"] }
          },
          {
            "name": "bee_reward",
            "description": "Record honey or a miss for a bee's entry or task (task:<id>), from the main session, with a note on what it rests on. Refused for a bee's own work, from a bee, and where the user or a team review's count already judged it.",
            "inputSchema": { "type": "object", "properties": {
              "id": { "type": "string", "description": "The entry's id, or task:<id>." },
              "outcome": { "type": "string", "enum": ["honey", "miss"] },
              "evidence": { "type": "string", "description": "One line: what it rests on." },
              "by": { "type": "string", "description": "Who gives it: queen, for the main session." } },
              "required": ["id", "outcome", "evidence", "by"] }
          },
          {
            "name": "bee_molt",
            "description": "Change a bee's card: how it works and the rules it has learned. Pass the whole new card. A bee the user lets molt freely may change its own card at once. A change the bee with the evolver role proposes for another bee that only adds list items at the end of \"## Rules I've learned\" (in place of a lone \"None yet.\"), each citing two or more of that bee's honey or miss ids, applies at once (the user can undo it). Any other change waits for the user's approval.",
            "inputSchema": { "type": "object", "properties": {
              "bee": { "type": "string", "description": "The bee whose card changes." },
              "by": { "type": "string", "description": "Who proposes it: your own bee name (the same as bee for your own card), or queen." },
              "reason": { "type": "string", "description": "Why, in one line." },
              "card": { "type": "string", "description": "The whole new card, in Markdown." } },
              "required": ["bee", "by", "reason", "card"] }
          },
          {
            "name": "team_review_begin",
            "description": "Start a team review led by this session: the bees with the reviewer role look on their own, then the bee with the overseer role gets their reports. Returns each reviewer's prompt, to spawn as subagents.",
            "inputSchema": { "type": "object", "properties": {
              "focus": { "type": "string", "description": "What to review: files, a folder, a diff, a task id." },
              "project": { "type": "string", "description": "The project; left out, it's the one whose folder this session is in." } },
              "required": ["focus"] }
          },
          {
            "name": "team_review_report",
            "description": "Hand in one bee's whole reply in a team review this session leads. After the last reviewer, it returns the overseer's prompt; after the overseer, the review is done.",
            "inputSchema": { "type": "object", "properties": {
              "run": { "type": "string", "description": "The review id from team_review_begin." },
              "bee": { "type": "string" },
              "report": { "type": "string", "description": "The bee's whole reply, unedited." } },
              "required": ["run", "bee", "report"] }
          },
          {
            "name": "project_set",
            "description": "Create or update a project: its one-line summary, the folders it lives in (sessions started there get its briefing) and its brief.",
            "inputSchema": { "type": "object", "properties": {
              "name": { "type": "string" },
              "summary": { "type": "string" },
              "paths": { "type": "array", "items": { "type": "string" } },
              "brief": { "type": "string", "description": "Markdown: what it is, where things are, how to build it. Replaces the old brief." } },
              "required": ["name"] }
          }
        ]
        """)!.AsArray();

    public static int Serve(Store store)
    {
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var output = Console.OpenStandardOutput();
        string? line;
        while ((line = input.ReadLine()) != null)
        {
            if (line.Trim().Length == 0) continue;
            JsonObject? msg;
            try { msg = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { Send(output, Error(null, -32700, "Parse error")); continue; }
            if (msg?["method"] is not JsonValue m || !m.TryGetValue<string>(out var method)) continue; // a response
            var id = msg["id"];
            if (id == null) continue; // a notification
            var args = msg["params"] as JsonObject ?? [];
            JsonObject reply;
            try
            {
                reply = method switch
                {
                    "initialize" => Result(id, new JsonObject
                    {
                        ["protocolVersion"] = Json.Str(args["protocolVersion"]) is { Length: > 0 } v ? v : "2025-06-18",
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "hive", ["version"] = Product.Version },
                        ["instructions"] = Instructions,
                    }),
                    "ping" => Result(id, new JsonObject()),
                    "tools/list" => Result(id, new JsonObject { ["tools"] = Tools.DeepClone() }),
                    "tools/call" => Result(id, Call(store, Json.Str(args["name"]), args["arguments"] as JsonObject ?? [])),
                    _ => Error(id, -32601, "Method not found: " + method),
                };
            }
            catch (Exception e)
            {
                reply = Error(id, -32603, e.Message);
            }
            Send(output, reply);
        }
        return 0;
    }

    static JsonObject Call(Store store, string name, JsonObject a)
    {
        string text;
        bool failed = false;
        try { text = Run(store, name, a); }
        catch (HiveException e) { text = e.Message; failed = true; }
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = failed,
        };
    }

    static string Run(Store s, string name, JsonObject a) => name switch
    {
        "hive_brief" => s.Brief(S(a, "project"), S(a, "bee"), Environment.CurrentDirectory),
        "hive_recall" => Store.Lines(
            s.Search(S(a, "query"), S(a, "project"), S(a, "bee"), S(a, "type"), Math.Clamp(I(a, "limit", 8), 1, 50), B(a, "include_superseded")),
            "Nothing in the hive matches. Try other words or fewer filters.", snippets: true),
        "hive_read" => s.Read(S(a, "id")),
        "hive_deposit" => "Deposited " + s.Deposit(S(a, "bee"), S(a, "type"), S(a, "title"), S(a, "summary"), S(a, "body"),
                                                    S(a, "project"), L(a, "tags") ?? [], L(a, "supersedes") ?? []),
        "hive_tidy" => s.Tidy(),
        "team_review_begin" => Reviews.Begin(s, S(a, "project"), S(a, "focus"), Environment.CurrentDirectory),
        "team_review_report" => Reviews.Report(s, S(a, "run"), S(a, "bee"), S(a, "report")),
        "bee_list" => s.Bees() is { Count: > 0 } bees ? string.Join('\n', bees.Select(Store.BeeLine)) : "No bees yet.",
        "bee_create" => Store.BeeLine(s.CreateBee(S(a, "name"), S(a, "specialty"), L(a, "projects") ?? [], S(a, "instructions"), "approve")),
        "bee_molt" => s.Molt(S(a, "bee"), S(a, "reason"), S(a, "card"),
                             Environment.GetEnvironmentVariable(TaskBeeVariable) is { Length: > 0 } taskBee ? taskBee : S(a, "by")),
        "bee_review" => s.Review(S(a, "bee")),
        "bee_reward" => s.Award(S(a, "id"), S(a, "outcome"), S(a, "evidence"),
                                Environment.GetEnvironmentVariable(TaskBeeVariable) is { Length: > 0 } giver ? giver : S(a, "by")),
        "project_set" => s.SetProject(S(a, "name"), a.ContainsKey("summary") ? S(a, "summary") : null, L(a, "paths"),
                                      a.ContainsKey("brief") ? S(a, "brief") : null) is var p
                         ? $"Project {p.Name}: {p.Summary}" : "",
        _ => throw new HiveException("There's no tool called " + name),
    };

    static string S(JsonObject a, string key) => a[key] is null ? "" : Json.Str(a[key]);

    static List<string>? L(JsonObject a, string key) => a[key] switch
    {
        JsonArray list => list.Select(Json.Str).Where(x => x.Length > 0).ToList(),
        JsonValue v when v.TryGetValue<string>(out var s) => s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
        _ => null,
    };

    static int I(JsonObject a, string key, int fallback) =>
        a[key] is JsonValue v && (v.TryGetValue<int>(out var n) || (v.TryGetValue<string>(out var s) && int.TryParse(s, out n))) ? n : fallback;

    static bool B(JsonObject a, string key) =>
        a[key] is JsonValue v && ((v.TryGetValue<bool>(out var b) && b) || (v.TryGetValue<string>(out var s) && s == "true"));

    static JsonObject Result(JsonNode id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };

    static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    static void Send(Stream output, JsonObject message)
    {
        output.Write(Json.Bytes(message));
        output.WriteByte((byte)'\n');
        output.Flush();
    }
}
