using static Hive.Gui.Ui;

namespace Hive.Gui;

// The Markdown the bees write, rendered as the mockups show it instead of
// as raw text: headings, paragraphs with **bold**, `code`, *emphasis* and
// [links](…), lists, fenced code, tables and rules. Only that subset; the
// hive's notes, cards and reports use nothing else. In a review (an
// overseer's report), "**Verdict: …**" becomes the verdict box and each
// numbered "**[bug high]** `path:line`: …" a finding with its tag, its
// file link, its "label: text" rows, its code and who found it.
static class Md
{
    // How many findings an overseer's report confirms (its numbered items
    // under "## Confirmed", or every numbered item when it has no such
    // heading) and how many of them are high.
    public static (int Findings, int High) Findings(string report)
    {
        int findings = 0, high = 0;
        var inConfirmed = !report.Contains("\n## Confirmed", StringComparison.OrdinalIgnoreCase);
        foreach (var raw in report.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## ")) { inConfirmed = line[3..].Trim().StartsWith("Confirmed", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inConfirmed || Numbered(line) is not { } item) continue;
            findings++;
            if (Severity(item).Sev == "high") high++;
        }
        return (findings, high);
    }

    // The verdict line of a report ("don't merge yet"), if it has one.
    public static string Verdict(string report)
    {
        foreach (var line in report.Split('\n'))
            if (line.TrimStart().StartsWith("**Verdict:", StringComparison.OrdinalIgnoreCase))
            {
                var rest = line.TrimStart()[10..];
                var end = rest.IndexOf("**", StringComparison.Ordinal);
                return (end >= 0 ? rest[..end] : rest).Trim().TrimEnd('.');
            }
        return "";
    }

    // Markdown as plain text, for a summary line: no "#" heading marks, ** or `.
    public static string Unmarked(string md) =>
        string.Join("\n", md.Split('\n').Select(l => l.TrimStart() is var t && t.StartsWith('#') ? t.TrimStart('#').TrimStart() : l)).Replace("**", "").Replace("`", "");

    // Its first "# " heading, without "Review: ".
    public static string Title(string md)
    {
        foreach (var line in md.Split('\n'))
            if (line.StartsWith("# ")) return line[2..].Trim() is var t && t.StartsWith("Review: ") ? t[8..] : t;
        return "";
    }

    // "12. text" → "text", or null.
    public static string? Numbered(string line)
    {
        var i = 0;
        while (i < line.Length && char.IsAsciiDigit(line[i])) i++;
        return i > 0 && i < line.Length - 1 && line[i] == '.' && line[i + 1] == ' ' ? line[(i + 2)..].Trim() : null;
    }

    // "**[bug medium, pre-existing]** rest" → ("bug", "medium", "pre-existing", "rest").
    public static (string Kind, string Sev, string Note, string Text) Severity(string item)
    {
        if (!item.StartsWith("**[")) return ("", "", "", item);
        var end = item.IndexOf("]**", StringComparison.Ordinal);
        if (end < 0) return ("", "", "", item);
        var tag = item[3..end];
        var comma = tag.IndexOf(',');
        var note = comma >= 0 ? tag[(comma + 1)..].Trim() : "";
        var words = (comma >= 0 ? tag[..comma] : tag).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sev = "";
        foreach (var w in words) if (w.ToLowerInvariant() is "high" or "medium" or "low" or "nit") sev = w.ToLowerInvariant();
        return (words.Length > 0 ? words[0].ToLowerInvariant() : "", sev, note, item[(end + 3)..].Trim());
    }

    // ---- inline ---------------------------------------------------------------

    // One line of Markdown as Pango markup: `code` honey, **bold** bright,
    // *emphasis* italic, [text](url) a link, [[id]] honey.
    public static string Inline(string text, uint color = Palette.Text)
    {
        // Code spans first, each held by a placeholder, so **bold** around
        // `code` still pairs up; then the rest; then the code put back.
        var code = new List<string>();
        var held = new System.Text.StringBuilder();
        var parts = text.Split('`');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 1 && i < parts.Length - 1)
            {
                held.Append('\uE000').Append((char)(0xE100 + code.Count));
                code.Add(Palette.Span(Palette.Pulse, parts[i]));
                continue;
            }
            if (i % 2 == 1) held.Append('`');
            held.Append(parts[i]);
        }
        var markup = Plain(held.ToString());
        for (int k = 0; k < code.Count; k++) markup = markup.Replace($"\uE000{(char)(0xE100 + k)}", code[k]);
        // Markers that overlap (**a*b** c*) nest wrongly, and GTK shows a label
        // with bad markup as blank: then the line goes in as plain text.
        if (!N.pango_parse_markup(markup, -1, 0, 0, 0, 0, 0)) markup = Markup(text);
        return $"<span foreground=\"#{color:X6}\">{markup}</span>";
    }

    static string Plain(string s)
    {
        var e = Markup(s);
        e = Pair(e, "**", $"<span foreground=\"#{Palette.Bright:X6}\" weight=\"bold\">", "</span>");
        e = Pair(e, "__", "<b>", "</b>");
        e = Pair(e, "*", "<i>", "</i>");
        // [text](url) and [[id]]
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < e.Length; i++)
        {
            if (e[i] == '[' && i + 1 < e.Length && e[i + 1] == '[' && e.IndexOf("]]", i + 2, StringComparison.Ordinal) is var close and > 0)
            {
                sb.Append($"<span foreground=\"#{Palette.Pulse:X6}\">").Append(e[(i + 2)..close]).Append("</span>");
                i = close + 1;
                continue;
            }
            if (e[i] == '[' && e.IndexOf("](", i, StringComparison.Ordinal) is var mid and > 0 && e.IndexOf(')', mid) is var end and > 0 && !e[i..mid].Contains('\n'))
            {
                var url = e[(mid + 2)..end];
                if (url.StartsWith("http"))
                {
                    sb.Append($"<a href=\"{url.Replace("\"", "&quot;")}\">{e[(i + 1)..mid]}</a>");
                    i = end;
                    continue;
                }
            }
            sb.Append(e[i]);
        }
        return sb.ToString();
    }

    // Replaces each pair of a marker with open ... close (an unpaired one stays).
    static string Pair(string s, string marker, string open, string close)
    {
        var sb = new System.Text.StringBuilder();
        var at = 0;
        while (true)
        {
            var a = s.IndexOf(marker, at, StringComparison.Ordinal);
            if (a < 0) break;
            var b = s.IndexOf(marker, a + marker.Length, StringComparison.Ordinal);
            // A lone * between spaces is a times sign, not emphasis.
            if (b < 0 || b == a + marker.Length || marker == "*" && (a + 1 < s.Length && s[a + 1] == ' ')) break;
            sb.Append(s, at, a - at).Append(open).Append(s, a + marker.Length, b - a - marker.Length).Append(close);
            at = b + marker.Length;
        }
        return sb.Append(s, at, s.Length - at).ToString();
    }

    // ---- blocks ---------------------------------------------------------------

    // A wrapped label of inline Markdown.
    public static nint Para(string text, uint color = Palette.Text, params string[] css)
    {
        var l = Css(N.gtk_label_new(""), css);
        N.gtk_label_set_markup(l, Inline(text, color));
        N.gtk_label_set_xalign(l, 0);
        N.gtk_label_set_wrap(l, true);
        N.gtk_label_set_wrap_mode(l, 2); // words, then characters (long paths)
        return l;
    }

    // Markdown as widgets, top to bottom. review: an overseer's report.
    // skipTitle leaves out the first "# " heading (the page shows it).
    // open: what a finding's file link does.
    // rules: "## " headings with a rule and a count (a report); without, the
    // small caps label a bee's card has.
    public static nint Render(string md, bool review = false, bool skipTitle = false, Action<string>? open = null, bool rules = true)
    {
        var box = Box(Vertical, 10);
        var lines = md.Replace("\r\n", "\n").Split('\n');
        var section = "";
        var titled = false;
        var para = new System.Text.StringBuilder();
        void Flush()
        {
            if (para.Length == 0) return;
            var text = para.ToString().Trim();
            para.Clear();
            if (review && text.StartsWith("**Verdict:", StringComparison.OrdinalIgnoreCase)) N.gtk_box_append(box, VerdictBox(text));
            else N.gtk_box_append(box, Para(text));
        }
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var t = line.Trim();
            if (t.Length == 0) { Flush(); continue; }
            if (t.StartsWith("```"))
            {
                Flush();
                var code = new System.Text.StringBuilder();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```"); i++) code.Append(lines[i]).Append('\n');
                N.gtk_box_append(box, Code(code.ToString().TrimEnd('\n')));
                continue;
            }
            if (line.StartsWith("# "))
            {
                Flush();
                if (skipTitle && !titled) { titled = true; continue; }
                titled = true;
                N.gtk_box_append(box, Para(line[2..].Trim(), Palette.Bright, "title-2"));
                continue;
            }
            if (line.StartsWith("## "))
            {
                Flush();
                section = line[3..].Trim();
                N.gtk_box_append(box, rules ? Section(section, review && section.StartsWith("Confirmed", StringComparison.OrdinalIgnoreCase) ? Findings(md).Findings : -1)
                    : Css(Para(section.ToUpperInvariant(), Palette.Dim), "caps", "card-section"));
                continue;
            }
            if (line.StartsWith("### "))
            {
                Flush();
                N.gtk_box_append(box, Css(Para(line[4..].Trim(), Palette.Dim), "caps", "sub-section"));
                continue;
            }
            if (t is "---" or "***")
            {
                Flush();
                N.gtk_box_append(box, Css(N.gtk_box_new(Horizontal, 0), "group-rule"));
                continue;
            }
            if (t.StartsWith('|'))
            {
                Flush();
                var rows = new List<string>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('|'); i++) rows.Add(lines[i].Trim());
                i--;
                N.gtk_box_append(box, Table(rows));
                continue;
            }
            if (Numbered(t) != null || t.StartsWith("- ") || t.StartsWith("* "))
            {
                Flush();
                // The item, with the lines indented under it (its own bullets and code).
                var item = new List<string> { line };
                for (i++; i < lines.Length && (lines[i].StartsWith("  ") || lines[i].StartsWith('\t') || lines[i].Trim().Length == 0 && i + 1 < lines.Length && lines[i + 1].StartsWith("   ")); i++)
                    item.Add(lines[i]);
                i--;
                N.gtk_box_append(box, review && Numbered(t) is { } n && n.StartsWith("**[") ? Finding(t, item, open) : Item(t, item));
                continue;
            }
            if (t.StartsWith("> "))
            {
                Flush();
                N.gtk_box_append(box, Css(Para("│ " + t[2..], Palette.Dim), "quote"));
                continue;
            }
            para.Append(t).Append(' ');
        }
        Flush();
        return box;
    }

    // "CONFIRMED ──────────── 4"
    static nint Section(string title, int count)
    {
        var l = Css(N.gtk_label_new(""), "md-section");
        N.gtk_label_set_xalign(l, 0);
        N.gtk_label_set_markup(l, Palette.Span(Palette.Dim, title.ToUpperInvariant() + " ") + Palette.Span(Palette.Ghost, new string('─', Math.Max(6, 32 - title.Length)))
            + (count >= 0 ? Palette.Span(Palette.Dim, " " + count) : ""));
        return l;
    }

    // "**Verdict: don't merge yet.** With this PR…": a red box when it says
    // not to merge, else a quiet one.
    static nint VerdictBox(string text)
    {
        var body = text[10..];
        var end = body.IndexOf("**", StringComparison.Ordinal);
        var verdict = (end >= 0 ? body[..end] : body).Trim().TrimEnd('.');
        var rest = end >= 0 ? body[(end + 2)..].Trim() : "";
        var bad = verdict.Contains("n't", StringComparison.OrdinalIgnoreCase) || verdict.Contains("not ", StringComparison.OrdinalIgnoreCase) || verdict.Contains("block", StringComparison.OrdinalIgnoreCase);
        var title = Css(N.gtk_label_new((bad ? "✗ " : "✓ ") + char.ToUpperInvariant(verdict.FirstOrDefault(' ')) + (verdict.Length > 1 ? verdict[1..] : "")), "verdict-title", bad ? "bad" : "good");
        N.gtk_label_set_xalign(title, 0);
        var box = Css(Box(Vertical, 4, title), "verdict", bad ? "bad" : "ok");
        if (rest.Length > 0) N.gtk_box_append(box, Para(rest));
        return box;
    }

    // A code block: monospace on black, scrolled sideways when it's wide.
    public static nint Code(string code)
    {
        var l = Css(N.gtk_label_new(code), "code-text");
        N.gtk_label_set_xalign(l, 0);
        N.gtk_label_set_selectable(l, true);
        var sw = N.gtk_scrolled_window_new();
        N.gtk_scrolled_window_set_policy(sw, 1, 2); // sideways when needed, never down
        N.gtk_scrolled_window_set_propagate_natural_height(sw, true);
        N.gtk_scrolled_window_set_child(sw, l);
        return Css(sw, "code");
    }

    // A list item: its number (honey) or ▸, its text, and what's indented under it.
    static nint Item(string first, List<string> lines, int depth = 0)
    {
        var n = Numbered(first);
        var text = n ?? first[2..].Trim();
        var mark = Css(N.gtk_label_new(n != null ? first[..first.IndexOf('.')] : "▸"), n != null ? "md-number" : "md-bullet");
        N.gtk_widget_set_valign(mark, Start);
        var content = Box(Vertical, 6, Para(text));
        AddUnder(content, lines, 1);
        return Box(Horizontal, 12, mark, Expand(content, true, false));
    }

    // What's indented under an item: bullets, more text, code.
    static void AddUnder(nint box, List<string> lines, int from, Action<string, string>? row = null)
    {
        for (int i = from; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("```"))
            {
                var code = new System.Text.StringBuilder();
                for (i++; i < lines.Count && !lines[i].Trim().StartsWith("```"); i++) code.Append(lines[i].Length >= 3 && lines[i].StartsWith("   ") ? lines[i][3..] : lines[i].TrimStart()).Append('\n');
                N.gtk_box_append(box, Code(code.ToString().TrimEnd('\n')));
                continue;
            }
            if (t.StartsWith("- ") || t.StartsWith("* ") || Numbered(t) != null)
            {
                var item = t.StartsWith("- ") || t.StartsWith("* ") ? t[2..].Trim() : Numbered(t)!;
                // "**Label:** text" or "Fix: text": a row with its label.
                var (label, rest) = Labelled(item);
                if (row != null) row(label, rest);
                else N.gtk_box_append(box, Box(Horizontal, 10, Css(N.gtk_label_new(t.StartsWith('-') || t.StartsWith('*') ? "▸" : t[..t.IndexOf('.')]), "md-bullet"), Expand(Para(item), true, false)));
                continue;
            }
            N.gtk_box_append(box, Para(t));
        }
    }

    // "**What happens:** text" → ("what happens", "text"); "Fix: text" → ("fix", "text"); else ("", item).
    static (string Label, string Text) Labelled(string item)
    {
        if (item.StartsWith("**") && item.IndexOf(":**", StringComparison.Ordinal) is var a and > 2 && a < 48)
            return (item[2..a].ToLowerInvariant(), item[(a + 3)..].Trim());
        if (item.IndexOf(": ", StringComparison.Ordinal) is var b and > 0 && b < 16 && !item[..b].Contains(' ') && !item[..b].Contains('`'))
            return (item[..b].ToLowerInvariant(), item[(b + 2)..].Trim());
        return ("", item);
    }

    // A finding: "01", its tag, its file:line, what's wrong, its rows, who found it.
    static nint Finding(string first, List<string> lines, Action<string>? open)
    {
        var number = first[..first.IndexOf('.')].PadLeft(2, '0');
        var (kind, sev, note, text) = Severity(Numbered(first)!);
        // `path:line`: at the start is where it is.
        var where = "";
        if (text.StartsWith('`') && text.IndexOf('`', 1) is var close and > 0)
        {
            where = text[1..close];
            text = text[(close + 1)..].TrimStart(':', ' ');
            // More places: "`a.cs:1` with `b.cs:2`: …"
            while (text.StartsWith("with `") || text.StartsWith("and `") || text.StartsWith(", `"))
            {
                var o = text.IndexOf('`');
                var c = text.IndexOf('`', o + 1);
                if (c < 0) break;
                where += ", " + text[(o + 1)..c];
                text = text[(c + 1)..].TrimStart(':', ' ');
            }
        }
        // "(found by A, B and C)" goes under it.
        var found = "";
        if (text.LastIndexOf("(found by ", StringComparison.Ordinal) is var f and >= 0 && text.IndexOf(')', f) is var fe and > 0)
        {
            found = text[(f + 10)..fe].Replace(" and ", ", ").Replace(", ", " · ");
            text = (text[..f].TrimEnd() + " " + text[(fe + 1)..].Trim()).Trim();
        }
        var tag = Css(N.gtk_label_new(string.Join(" · ", new[] { kind, sev, note }.Where(s => s.Length > 0))), "tag", sev == "high" ? "bad" : sev == "medium" ? "honey" : "");
        var head = Box(Horizontal, 10, Css(N.gtk_label_new(number), "faint"), tag);
        if (where.Length > 0)
        {
            var link = Css(TextButton(where, () => open?.Invoke(where.Split(',')[0].Trim())), "link", "small");
            N.gtk_widget_set_tooltip_text(link, "Open " + where.Split(',')[0].Trim());
            N.gtk_box_append(head, link);
        }
        var body = Box(Vertical, 6, Para(text, Palette.Bright));
        var rows = Box(Vertical, 4);
        AddUnder(rows, lines, 1, (label, rest) =>
        {
            var l = Css(N.gtk_label_new(label), "dim-label", "small");
            N.gtk_label_set_xalign(l, 0);
            N.gtk_label_set_yalign(l, 0);
            N.gtk_label_set_wrap(l, true);
            N.gtk_widget_set_size_request(l, 110, -1);
            N.gtk_box_append(rows, Box(Horizontal, 16, l, Expand(Css(Para(rest), "small"), true, false)));
        });
        N.gtk_box_append(body, rows);
        if (found.Length > 0) N.gtk_box_append(body, Css(Para("found by " + found, Palette.Faint), "tiny"));
        N.gtk_widget_set_margin_start(body, 30);
        return Css(Box(Vertical, 6, head, body), "finding");
    }

    // A table as rows: the first cell bright, then "header value" pairs.
    static nint Table(List<string> rows)
    {
        static string[] Cells(string row) => row.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
        var box = Box(Vertical, 4);
        if (rows.Count == 0) return box;
        var head = Cells(rows[0]);
        foreach (var row in rows.Skip(1))
        {
            var cells = Cells(row);
            if (cells.All(c => c.Trim('-', ':').Length == 0)) continue; // |---|---|
            var sb = new System.Text.StringBuilder(Inline(cells[0], Palette.Bright));
            for (int k = 1; k < cells.Length; k++)
                sb.Append(Palette.Span(Palette.Ghost, "  ·  ")).Append(k < head.Length ? Palette.Span(Palette.Dim, head[k] + " ") : "").Append(Inline(cells[k]));
            var l = Css(N.gtk_label_new(""), "small");
            N.gtk_label_set_markup(l, sb.ToString());
            N.gtk_label_set_xalign(l, 0);
            N.gtk_label_set_wrap(l, true);
            N.gtk_label_set_wrap_mode(l, 2);
            N.gtk_box_append(box, l);
        }
        return box;
    }
}
