using System.Text;

namespace Hive;

// A Markdown file with front matter. It reads the YAML the hive writes plus
// what Obsidian writes when you edit properties: scalars (also wrapped onto
// more lines, and | or > block scalars, read as one line), inline lists
// [a, b], block lists (- a), and nested maps, read as parent.key and written
// back nested.
sealed class Doc
{
    readonly OrderedDictionary<string, string> meta = new();
    readonly HashSet<string> lists = [];
    public string Body = "";
    public bool HasFields => meta.Count > 0;

    public string Get(string key, string fallback = "") =>
        meta.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

    public List<string> GetList(string key) => ParseList(Get(key));

    public void Set(string key, string value)
    {
        meta[key] = Text.OneLine(value);
        lists.Remove(key);
    }

    public void Remove(string key)
    {
        meta.Remove(key);
        lists.Remove(key);
    }

    public void SetList(string key, IEnumerable<string> items)
    {
        meta[key] = FormatList(items);
        lists.Add(key);
    }

    public static Doc Load(string path) => Parse(File.ReadAllText(path));

    // Written to a temp file of its own and moved into place, so two
    // processes saving the same file can't mix their writes.
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, ToString());
        File.Move(tmp, path, overwrite: true);
    }

    static readonly string[] BlockIndicators = ["|", "|-", "|+", ">", ">-", ">+"];

    public static Doc Parse(string text)
    {
        var doc = new Doc();
        text = text.Replace("\r\n", "\n");
        var lines = text.Split('\n');
        int close = -1;
        if (lines.Length > 1 && lines[0].TrimEnd() == "---")
            for (int i = 1; i < lines.Length; i++)
                if (lines[i].TrimEnd() == "---") { close = i; break; }
        if (close < 0) { doc.Body = text; return doc; }

        string? key = null;      // the last top-level key
        bool open = false;       // it had no value: a nested map, a list or a wrapped value follows
        string? listKey = null;  // where "- item" lines go
        string? child = null;    // the last nested key that had a value (or a | or > block scalar)
        int childIndent = 0;     // lines indented deeper than it continue its value
        var items = new Dictionary<string, List<string>>();

        for (int i = 1; i < close; i++)
        {
            var line = lines[i];
            var t = line.Trim();
            if (t.Length == 0 || t[0] == '#') continue;

            if (child != null && line.Length - line.TrimStart().Length > childIndent) { doc.Append(child, t); continue; }
            child = null;

            if (t == "-" || t.StartsWith("- "))
            {
                if (listKey == null && key != null && !open && char.IsWhiteSpace(line[0])) { doc.Append(key, t); continue; }
                if (listKey != null && t.Length > 2)
                {
                    if (!items.TryGetValue(listKey, out var list)) items[listKey] = list = [];
                    list.Add(Text.OneLine(Unquote(t[2..].Trim())));
                }
                continue;
            }

            if (char.IsWhiteSpace(line[0]) && key != null)
            {
                // Under a key that had a value (or a | or > block scalar), an
                // indented line continues that value, whatever it contains.
                if (!open) { doc.Append(key, t); continue; }
                var c = ColonAt(t);
                if (c <= 0) { doc.Append(key, t); continue; }
                var name = key + "." + t[..c].Trim();
                var v = t[(c + 1)..].Trim();
                if (v.Length == 0 || BlockIndicators.Contains(v))
                {
                    // A placeholder keeps its place in the key order until its list or text comes.
                    doc.meta[name] = "";
                    doc.lists.Remove(name);
                }
                else doc.Put(name, v);
                if (v.Length == 0) listKey = name;
                else { child = name; childIndent = line.Length - line.TrimStart().Length; }
                continue;
            }

            var colon = ColonAt(t);
            if (colon <= 0) continue;
            key = t[..colon].Trim();
            var value = t[(colon + 1)..].Trim();
            listKey = null;
            open = false;
            if (value.Length == 0)
            {
                open = true;
                listKey = key;
                doc.meta[key] = "";
                doc.lists.Remove(key);
            }
            else if (BlockIndicators.Contains(value))
            {
                doc.meta[key] = "";
                doc.lists.Remove(key);
            }
            else doc.Put(key, value);
        }
        foreach (var (k, list) in items)
        {
            if (list.Count == 0) continue;
            if (doc.meta.TryGetValue(k, out var placeholder) && placeholder.Length > 0) continue;
            doc.SetList(k, list);
        }
        // A parent that turned out to hold nested keys isn't a value of its own.
        foreach (var k in doc.meta.Keys.Where(k => doc.meta[k] == "" && doc.meta.Keys.Any(c => c.StartsWith(k + "."))).ToList())
            doc.meta.Remove(k);
        doc.Body = string.Join('\n', lines[(close + 1)..]).TrimStart('\n');
        return doc;
    }

    // The colon that ends a key: followed by a space or the end of the line.
    static int ColonAt(string t)
    {
        for (int i = 0; i < t.Length; i++)
            if (t[i] == ':' && (i + 1 == t.Length || t[i + 1] == ' ')) return i;
        return -1;
    }

    void Put(string key, string value)
    {
        if (value.StartsWith('[') && value.EndsWith(']')) SetList(key, ParseList(value));
        else
        {
            meta[key] = Text.OneLine(Unquote(value));
            lists.Remove(key);
        }
    }

    void Append(string key, string more)
    {
        if (lists.Contains(key)) return;
        var now = meta.TryGetValue(key, out var v) ? v : "";
        meta[key] = Text.OneLine(now.Length == 0 ? more : now + " " + more);
    }

    public override string ToString()
    {
        var sb = new StringBuilder("---\n");
        var written = new HashSet<string>();
        foreach (var (key, _) in meta)
        {
            var dot = key.IndexOf('.');
            var name = dot > 0 ? key[..dot] : key;
            if (!written.Add(name)) continue;
            var children = meta.Keys.Where(k => k.StartsWith(name + ".")).ToList();
            if (children.Count == 0) { Line(sb, key, meta[key], lists.Contains(key), ""); continue; }
            sb.Append(name).Append(":\n");
            foreach (var c in children) Line(sb, c[(name.Length + 1)..], meta[c], lists.Contains(c), "  ");
        }
        sb.Append("---\n\n").Append(Body.TrimStart('\n'));
        if (sb[^1] != '\n') sb.Append('\n');
        return sb.ToString();
    }

    static void Line(StringBuilder sb, string key, string value, bool list, string indent)
    {
        sb.Append(indent).Append(key).Append(':');
        if (value.Length > 0) sb.Append(' ').Append(list ? value : Quote(value));
        sb.Append('\n');
    }

    public static List<string> ParseList(string v)
    {
        v = v.Trim();
        if (v.Length == 0) return [];
        if (!(v.StartsWith('[') && v.EndsWith(']'))) return [v];
        var list = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        bool escaped = false;
        foreach (var c in v[1..^1])
        {
            if (quote != '\0')
            {
                sb.Append(c);
                if (escaped) escaped = false;
                else if (c == '\\' && quote == '"') escaped = true;
                else if (c == quote) quote = '\0';
            }
            // A quote only opens at the start of an item: "don't" is a word.
            else if (c is '"' or '\'' && sb.ToString().Trim().Length == 0) { quote = c; sb.Append(c); }
            else if (c == ',') Add();
            else sb.Append(c);
        }
        Add();
        return list;

        void Add()
        {
            var s = Text.OneLine(Unquote(sb.ToString().Trim()));
            if (s.Length > 0) list.Add(s);
            sb.Clear();
        }
    }

    static string FormatList(IEnumerable<string> items) =>
        "[" + string.Join(", ", items.Select(Text.OneLine).Where(i => i.Length > 0).Select(i => Quote(i, inList: true))) + "]";

    static string Quote(string s, bool inList = false)
    {
        bool plain = s.Length > 0 && s == s.Trim() && !s.Contains(": ") && !s.Contains(" #") && !s.EndsWith(':')
            && "[]{}\"'#&*!|>%@`,-?".IndexOf(s[0]) < 0
            && !(inList && s.IndexOfAny([',', '[', ']', '{', '}', '"', '\'', ':']) >= 0);
        return plain ? s : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    static string Unquote(string v)
    {
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
        {
            var sb = new StringBuilder();
            for (int i = 1; i < v.Length - 1; i++)
            {
                if (v[i] == '\\' && i + 1 < v.Length - 1)
                {
                    i++;
                    switch (v[i])
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case '"' or '\\': sb.Append(v[i]); break;
                        default: sb.Append('\\').Append(v[i]); break; // keep what we don't know
                    }
                }
                else sb.Append(v[i]);
            }
            return sb.ToString();
        }
        if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'') return v[1..^1].Replace("''", "'");
        return v;
    }
}
