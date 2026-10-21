// Checks for src/Doc.cs, the front matter parser and writer.
// Run: dotnet run --project tests/DocTests
// Each check prints PASS or FAIL; the exit code is the number of failures (capped at 1).
// "#n" in a name is the finding number in the team review review rv-e.
using Hive;

var checks = new List<(string Name, Action Run)>();
void Check(string name, Action run) => checks.Add((name, run));

// ---- #1 block scalars and wrapped values ----

Check("#1 | block scalar is read as one line", () =>
{
    var d = Doc.Parse("""
        ---
        description: |
          First line
          Note: second
        title: t
        ---
        body
        """);
    Eq(d.Get("description"), "First line Note: second");
    Eq(d.Get("title"), "t");
    Eq(d.Get("Note"), "");
    NotContains(d.ToString(), "\nNote:");
});

Check("#1 >- folded scalar is read as one line, not \">-\"", () =>
{
    var d = Doc.Parse("""
        ---
        name: x
        description: >-
          Long text that
          wraps here
        type: user
        ---
        """);
    Eq(d.Get("description"), "Long text that wraps here");
    Eq(d.Get("type"), "user");
});

Check("#1 every block indicator (| |- |+ > >- >+) is dropped", () =>
{
    foreach (var ind in new[] { "|", "|-", "|+", ">", ">-", ">+" })
    {
        var d = Doc.Parse($"---\nk: {ind}\n  text\n---\n");
        Eq(d.Get("k"), "text", ind);
    }
});

Check("#1 block scalar lines that look like list items stay text", () =>
{
    var d = Doc.Parse("""
        ---
        notes: |
          - one
          - two
        ---
        """);
    Eq(d.Get("notes"), "- one - two");
    Eq(d.GetList("notes"), ["- one - two"]);
});

Check("#1 block scalar with a blank line inside", () =>
{
    var d = Doc.Parse("---\nk: |\n  a\n\n  b\nz: 1\n---\n");
    Eq(d.Get("k"), "a b");
    Eq(d.Get("z"), "1");
});

Check("#1 wrapped plain scalar is joined", () =>
{
    var d = Doc.Parse("""
        ---
        summary: first part
          second part: with a colon
        title: t
        ---
        """);
    Eq(d.Get("summary"), "first part second part: with a colon");
    Eq(d.Get("second part"), "");
    Eq(d.Get("title"), "t");
});

Check("#1 empty key followed by indented text keeps the text", () =>
{
    var d = Doc.Parse("---\nsummary:\n  wrapped text\n  more\n---\n");
    Eq(d.Get("summary"), "wrapped text more");
});

Check("#1 block scalar is saved as a quoted one-liner and survives", () =>
{
    var d = Doc.Parse("---\ndescription: |\n  a: b\n  c\n---\n");
    var saved = d.ToString();
    Contains(saved, "description: \"a: b c\"\n");
    Eq(Doc.Parse(saved).Get("description"), "a: b c");
});

// ---- #2 quoted \n ----

Check("#2 quoted \\n becomes a space, not a raw newline", () =>
{
    var d = Doc.Parse("""
        ---
        summary: "a\nb"
        ---
        """);
    Eq(d.Get("summary"), "a b");
    var saved = d.ToString();
    NotContains(saved, "\nb\n");
    Eq(Doc.Parse(saved).Get("summary"), "a b");
});

Check("#2 quoted \\n before a key-like text makes no new key", () =>
{
    var d = Doc.Parse("""
        ---
        summary: "first\nsecond: x"
        ---
        """);
    var again = Doc.Parse(d.ToString());
    Eq(again.Get("summary"), "first second: x");
    Eq(again.Get("second"), "");
});

Check("#2 quoted \\n---\\n can't close the front matter early", () =>
{
    var d = Doc.Parse("""
        ---
        summary: "x\n---\ntype: lesson"
        title: t
        ---
        the body
        """);
    var again = Doc.Parse(d.ToString());
    Eq(again.Get("summary"), "x --- type: lesson");
    Eq(again.Get("title"), "t");
    Eq(again.Get("type"), "");
    Eq(again.Body, "the body\n");
});

Check("#2 \\n in a list item becomes a space", () =>
{
    var d = Doc.Parse("""
        ---
        tags: ["a\nb", c]
        ---
        """);
    Eq(d.GetList("tags"), ["a b", "c"]);
});

Check("#2 Set with real newlines writes one line", () =>
{
    var d = new Doc();
    d.Set("summary", "one\ntwo\r\nthree\n---\nx: y");
    Eq(d.Get("summary"), "one two three --- x: y");
    var again = Doc.Parse(d.ToString());
    Eq(again.Get("summary"), "one two three --- x: y");
    Eq(again.Get("x"), "");
});

// ---- #3 nested maps written back nested ----

Check("#3 nested map is read as parent.key and written nested", () =>
{
    var d = Doc.Parse("""
        ---
        name: n
        metadata:
          type: user
          origin: claude
        after: y
        ---
        """);
    Eq(d.Get("metadata.type"), "user");
    Eq(d.Get("metadata.origin"), "claude");
    Eq(d.Get("metadata"), "");
    var saved = d.ToString();
    Contains(saved, "name: n\nmetadata:\n  type: user\n  origin: claude\nafter: y\n");
    NotContains(saved, "metadata.");
});

Check("#3 Set on a dotted key writes a nested map", () =>
{
    var d = new Doc();
    d.Set("metadata.type", "user");
    d.Set("title", "t");
    d.Set("metadata.extra", "a: b");
    Eq(d.ToString(), "---\nmetadata:\n  type: user\n  extra: \"a: b\"\ntitle: t\n---\n\n");
});

Check("#3 nested map with an inline list child", () =>
{
    var d = Doc.Parse("---\nm:\n  tags: [a, b]\n---\n");
    Eq(d.GetList("m.tags"), ["a", "b"]);
    Contains(d.ToString(), "m:\n  tags: [a, b]\n");
});

// ---- #5 two-level lists ----

Check("#5 a list two levels down goes to parent.child", () =>
{
    var d = Doc.Parse("""
        ---
        parent:
          child:
            - x
            - y
        next: z
        ---
        """);
    Eq(d.GetList("parent.child"), ["x", "y"]);
    Eq(d.GetList("parent"), []);
    Eq(d.Get("next"), "z");
    var again = Doc.Parse(d.ToString());
    Eq(again.GetList("parent.child"), ["x", "y"]);
    Eq(again.Get("next"), "z");
});

Check("#5 two nested lists under one parent stay apart", () =>
{
    var d = Doc.Parse("---\np:\n  a:\n    - 1\n  b:\n    - 2\n    - 3\n---\n");
    Eq(d.GetList("p.a"), ["1"]);
    Eq(d.GetList("p.b"), ["2", "3"]);
});

// ---- #6 list then scalar ----

Check("#6 a duplicate key, list then scalar, is saved as a scalar", () =>
{
    var d = Doc.Parse("""
        ---
        tags: [a]
        tags: "#b"
        ---
        """);
    Eq(d.Get("tags"), "#b");
    Eq(d.GetList("tags"), ["#b"]);
    Contains(d.ToString(), "tags: \"#b\"\n");
});

Check("#6 list then scalar with ': ' is quoted on save", () =>
{
    var d = Doc.Parse("---\ntags: [a]\ntags: x: y\n---\n");
    Eq(d.Get("tags"), "x: y");
    Contains(d.ToString(), "tags: \"x: y\"\n");
});

Check("#6 scalar then block list ends as the list", () =>
{
    var d = Doc.Parse("---\ntags: a\ntags:\n  - b\n---\n");
    Eq(d.GetList("tags"), ["b"]);
    Contains(d.ToString(), "tags: [b]\n");
});

Check("#6 Set after SetList drops the list mark", () =>
{
    var d = new Doc();
    d.SetList("tags", ["a"]);
    d.Set("tags", "#b");
    Contains(d.ToString(), "tags: \"#b\"\n");
});

// ---- #7 apostrophes in list items ----

Check("#7 an apostrophe mid-item doesn't open a quote", () =>
{
    var d = Doc.Parse("---\ntags: [don't, x]\n---\n");
    Eq(d.GetList("tags"), ["don't", "x"]);
});

Check("#7 rock 'n' roll and a single-quoted '' escape", () =>
{
    var d = Doc.Parse("---\ntags: [rock 'n' roll, 'it''s', \"a\\\"b\"]\n---\n");
    Eq(d.GetList("tags"), ["rock 'n' roll", "it's", "a\"b"]);
});

Check("#7 apostrophe items survive save", () =>
{
    var d = new Doc();
    d.SetList("tags", ["don't", "x", "it's ok"]);
    Eq(Doc.Parse(d.ToString()).GetList("tags"), ["don't", "x", "it's ok"]);
});

// ---- #8 braces in list items ----

Check("#8 braces mid-item are quoted in a list", () =>
{
    var d = new Doc();
    d.SetList("tags", ["a{b}", "c"]);
    Contains(d.ToString(), "tags: [\"a{b}\", c]\n");
    Eq(Doc.Parse(d.ToString()).GetList("tags"), ["a{b}", "c"]);
});

Check("#8 commas, brackets and quotes in list items round-trip", () =>
{
    string[] items = ["a, b", "[x]", "say \"hi\"", "back\\slash", "{y}", "#tag", "- dash"];
    var d = new Doc();
    d.SetList("tags", items);
    EqList(Doc.Parse(d.ToString()).GetList("tags"), items);
});

// ---- #9 unknown escapes ----

Check("#9 unknown escapes keep their backslash", () =>
{
    var d = Doc.Parse("""
        ---
        k: "a\rb\u00e9\0"
        ---
        """);
    Eq(d.Get("k"), @"a\rb\u00e9\0");
    Eq(Doc.Parse(d.ToString()).Get("k"), @"a\rb\u00e9\0");
});

Check("#9 known escapes: \\t \\\" \\\\", () =>
{
    var d = Doc.Parse("""
        ---
        k: "a\tb \"q\" c\\d"
        ---
        """);
    Eq(d.Get("k"), "a\tb \"q\" c\\d");
    Eq(Doc.Parse(d.ToString()).Get("k"), "a\tb \"q\" c\\d");
});

Check("#9 a trailing lone backslash is kept", () =>
{
    var d = Doc.Parse("---\nk: \"a\\\"\n---\n");
    Eq(d.Get("k"), "a\\");
    Eq(Doc.Parse(d.ToString()).Get("k"), "a\\");
});

// ---- #10 scalar lists ----

Check("#10 GetList on a scalar doesn't unquote twice", () =>
{
    var d = Doc.Parse("---\nprojects: \"'x'\"\n---\n");
    Eq(d.GetList("projects"), ["'x'"]);
});

Check("#10 GetList on a plain scalar and on a missing key", () =>
{
    var d = Doc.Parse("---\nprojects: hive\n---\n");
    Eq(d.GetList("projects"), ["hive"]);
    Eq(d.GetList("nope"), []);
});

// ---- #11 empty list items ----

Check("#11 SetList drops empty and blank items", () =>
{
    var d = new Doc();
    d.SetList("tags", ["", " ", "a", "\n", "b"]);
    Contains(d.ToString(), "tags: [a, b]\n");
});

Check("#11 empty items in an inline list are dropped", () =>
{
    var d = Doc.Parse("---\ntags: [a, , \"\", '', b,]\n---\n");
    Eq(d.GetList("tags"), ["a", "b"]);
});

Check("#11 empty items in a block list are dropped", () =>
{
    var d = Doc.Parse("---\ntags:\n  - a\n  - \"\"\n  -\n  - b\n---\n");
    Eq(d.GetList("tags"), ["a", "b"]);
    Contains(d.ToString(), "tags: [a, b]\n");
});

Check("#11 a list of only empty items is an empty list", () =>
{
    var d = new Doc();
    d.SetList("tags", ["", " "]);
    Contains(d.ToString(), "tags: []\n");
    Eq(Doc.Parse(d.ToString()).GetList("tags"), []);
});

// ---- round-trip stability ----

string[] samples =
[
    "---\ndescription: |\n  First line\n  Note: second\ntitle: t\n---\nbody\n",
    "---\nsummary: \"x\\n---\\ntype: lesson\"\n---\nb\n",
    "---\nmetadata:\n  type: user\n  tags:\n    - a\n    - b\nafter: y\n---\n",
    "---\ntags: [don't, x, \"a{b}\", 'it''s']\nk: \"a\\rb\"\n---\n",
    "---\ntags:\n  - a\n  - \"\"\n  - b: c\ntitle: \"#1: yes\"\n---\n\n\nbody with\n---\nrules\n",
    "---\nk: |\n  - one\n  - two\nurl: https://x.com/a?b=c#d\n---\n",
    "---\nempty:\nnext: v\n---\n",
    "---\n---\n",
    "no front matter\n",
    "",
];

Check("round trip: save(parse(save(parse(x)))) == save(parse(x))", () =>
{
    foreach (var s in samples)
    {
        var once = Doc.Parse(s).ToString();
        var twice = Doc.Parse(once).ToString();
        Eq(twice, once, Show(s));
    }
});

Check("round trip: tricky scalar values come back as set", () =>
{
    string[] values =
    [
        "|", ">", ">-", "-", "-5", "- item", "@x", "a: b", "ends:", "#tag", "a #b", "\"quoted\"", "'single'",
        "back\\slash", "a\\nb", "[not, a, list]", "{x: 1}", "*star", "&anchor", "!tag", "%pct", "`tick`", "?q",
        "https://x.com/a:b", "日本語 ✓ 🐝", "tab\there", "x,y",
    ];
    foreach (var v in values)
    {
        var d = new Doc();
        d.Set("k", v);
        d.Set("after", "z");
        var again = Doc.Parse(d.ToString());
        Eq(again.Get("k"), v, Show(v));
        Eq(again.Get("after"), "z", Show(v));
    }
});

Check("round trip: the body comes back as is, --- lines included", () =>
{
    var d = new Doc { Body = "# Title\n\n---\nnot: front matter\n---\n\nend\n" };
    d.Set("title", "t");
    var again = Doc.Parse(d.ToString());
    Eq(again.Body, d.Body);
    Eq(again.Get("not"), "");
});

Check("round trip: a 100k value and a 5000-item list", () =>
{
    var big = string.Concat(Enumerable.Repeat("word: \"x\" ", 10_000));
    var items = Enumerable.Range(0, 5000).Select(i => $"item {i}, 'q'").ToList();
    var d = new Doc();
    d.Set("big", big);
    d.SetList("items", items);
    var again = Doc.Parse(d.ToString());
    Eq(again.Get("big"), big.Trim());
    Eq(again.GetList("items"), items);
});

// ---- concurrent saves ----

Check("concurrent saves: no errors, no temp files left, last file is whole", () =>
{
    var dir = Directory.CreateTempSubdirectory("hive-doctests-");
    try
    {
        var path = Path.Combine(dir.FullName, "sub", "x.md");
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var done = false;
        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref done))
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var d = Doc.Load(path);
                    if (d.Get("title") != "t" || !d.Body.TrimEnd().EndsWith("#" + d.Get("n")))
                        errors.Add("torn read: " + d.Get("n"));
                }
                catch (Exception e) { errors.Add("read: " + e.Message); }
            }
        });
        Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            try
            {
                var d = new Doc { Body = new string('x', 4000) + "#" + i };
                d.Set("title", "t");
                d.Set("n", i.ToString());
                d.Save(path);
            }
            catch (Exception e) { errors.Add("save: " + e.Message); }
        });
        Volatile.Write(ref done, true);
        reader.Wait();
        Eq(string.Join("; ", errors.Distinct().Take(3)), "");
        Eq(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length, 0);
        var last = Doc.Load(path);
        Eq(last.Get("title"), "t");
        True(int.TryParse(last.Get("n"), out var n) && n is >= 0 and < 400, "n is one of the writes");
    }
    finally { dir.Delete(recursive: true); }
});

// ---- edge cases of my own ----

Check("edge: CRLF files parse like LF files", () =>
{
    var lf = "---\ntitle: t\ntags:\n  - a\n  - b\ndescription: >-\n  x\n  y\n---\nbody\n";
    var crlf = Doc.Parse(lf.Replace("\n", "\r\n"));
    Eq(crlf.Get("title"), "t");
    Eq(crlf.GetList("tags"), ["a", "b"]);
    Eq(crlf.Get("description"), "x y");
    Eq(crlf.ToString(), Doc.Parse(lf).ToString());
});

Check("edge: no front matter, unclosed front matter, empty file", () =>
{
    Eq(Doc.Parse("just text\n").Body, "just text\n");
    var unclosed = Doc.Parse("---\ntitle: t\nno end\n");
    Eq(unclosed.Get("title"), "");
    Eq(unclosed.Body, "---\ntitle: t\nno end\n");
    var empty = Doc.Parse("");
    Eq(empty.Body, "");
    Eq(empty.ToString(), "---\n---\n\n");
});

Check("edge: comment lines and a colon without a space", () =>
{
    var d = Doc.Parse("---\n# a comment\nsource: https://x.com/a\nratio: 1:2\nnokey\n---\n");
    Eq(d.Get("source"), "https://x.com/a");
    Eq(d.Get("ratio"), "1:2");
    Eq(d.Get("# a comment"), "");
    Contains(d.ToString(), "source: https://x.com/a\nratio: 1:2\n");
});

Check("edge: Obsidian block list, indented or not", () =>
{
    Eq(Doc.Parse("---\ntags:\n  - a\n  - 'b c'\n---\n").GetList("tags"), ["a", "b c"]);
    Eq(Doc.Parse("---\ntags:\n- a\n- b\n---\n").GetList("tags"), ["a", "b"]);
});

Check("edge: a top-level block list keeps its place in the key order", () =>
{
    var d = Doc.Parse("---\na: 1\ntags:\n  - x\nb: 2\n---\n");
    Eq(d.ToString(), "---\na: 1\ntags: [x]\nb: 2\n---\n\n");
});

Check("edge: Get fallback, Remove, and an empty key written back bare", () =>
{
    var d = Doc.Parse("---\nempty:\nk: v\ntags: [a]\n---\n");
    Eq(d.Get("empty", "fb"), "fb");
    d.Remove("k");
    d.Remove("tags");
    Eq(d.ToString(), "---\nempty:\n---\n\n");
});

Check("edge: nested block scalar is read into the child", () =>
{
    var d = Doc.Parse("---\nmeta:\n  desc: |\n    text here\nafter: y\n---\n");
    Eq(d.Get("meta.desc"), "text here");
    Eq(d.Get("after"), "y");
});

Check("edge: nested wrapped value is joined into the child", () =>
{
    var d = Doc.Parse("---\nmeta:\n  desc: long\n    wrapped\n---\n");
    Eq(d.Get("meta.desc"), "long wrapped");
});

Check("edge: a nested list keeps its place in the key order", () =>
{
    var d = Doc.Parse("---\nparent:\n  child:\n    - x\nnext: z\n---\n");
    Eq(d.ToString(), "---\nparent:\n  child: [x]\nnext: z\n---\n\n");
});

// ---- run ----

int failed = 0;
foreach (var (name, run) in checks)
{
    try { run(); Console.WriteLine($"PASS  {name}"); }
    catch (Exception e)
    {
        failed++;
        Console.WriteLine($"FAIL  {name}\n      {e.Message.Replace("\n", "\n      ")}");
    }
}
Console.WriteLine($"\n{checks.Count - failed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

static string Show(string s) => s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

static void EqList(IEnumerable<string> a, IEnumerable<string> e, string what = "")
{
    if (!a.SequenceEqual(e))
        throw new Exception($"{what} expected [{string.Join(" | ", e.Select(Show))}]\ngot      [{string.Join(" | ", a.Select(Show))}]");
}

static void Eq<T>(T actual, T expected, string what = "")
{
    if (actual is IEnumerable<string> a && expected is IEnumerable<string> e) { EqList(a, e, what); return; }
    if (!Equals(actual, expected))
        throw new Exception($"{what} expected \"{Show($"{expected}")}\"\ngot      \"{Show($"{actual}")}\"");
}

static void Contains(string text, string part)
{
    if (!text.Contains(part)) throw new Exception($"expected to contain \"{Show(part)}\"\nin \"{Show(text)}\"");
}

static void NotContains(string text, string part)
{
    if (text.Contains(part)) throw new Exception($"expected not to contain \"{Show(part)}\"\nin \"{Show(text)}\"");
}

static void True(bool ok, string what)
{
    if (!ok) throw new Exception("expected " + what);
}
