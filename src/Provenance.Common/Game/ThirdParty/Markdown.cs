using System.Globalization;
using System.Text;

namespace Provenance.Common.Game.ThirdParty;

public static class Markdown
{
    public const int MaxChars = 200_000;
    internal const int MaxNodes = 6_000, MaxDepth = 8, MaxColumns = 32;

    internal sealed class TooMuchException : Exception;

    internal sealed class Budget
    {
        private int _nodes;

        public void Spend(int weight = 1)
        {
            if ((_nodes += weight) > MaxNodes) throw new TooMuchException();
        }
    }

    private readonly record struct FenceMark(char Mark, int Count, int Indent);

    private readonly record struct ListMark(bool Ordered, int Start, int Content, bool Empty);

    public static MarkdownDoc? Parse(string text)
    {
        if (text.Length > MaxChars) return null;
        var lines = Clean(text).Split('\n').ToList();
        var inlines = new MarkdownInlines(new Budget(), LinkNames(lines));
        try { return new MarkdownDoc(Blocks(lines, 0, inlines)); }
        // more in it than any readme has, so the window shows the file as it is
        catch (TooMuchException) { return null; }
    }

    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Replace("\r\n", "\n"))
        {
            if (c == '\t') sb.Append("    ");
            else if (c is >= (char)0x202A and <= (char)0x202E or >= (char)0x2066 and <= (char)0x2069) continue;
            else if (c is '\r' or (char)0x2028 or (char)0x2029) sb.Append('\n');
            else if (c == '\n' || !char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private static Dictionary<string, string> LinkNames(List<string> lines)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        FenceMark? open = null;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (open is { } fence)
            {
                if (Closes(line, fence)) open = null;
                continue;
            }
            if ((open = Fence(line)) is not null) continue;
            int at = Indent(line);
            if (at > 3 || at >= line.Length || line[at] != '[') continue;
            int close = line.IndexOf("]:", at, StringComparison.Ordinal);
            if (close <= at + 1) continue;
            var rest = line[(close + 2)..].Trim();
            int end = rest.IndexOf(' ');
            var target = (end < 0 ? rest : rest[..end]).Trim('<', '>');
            var title = end < 0 ? "" : rest[end..].Trim();
            // "[note]: see below" is a sentence, a title after the link has to be in quotes or brackets
            if (target.Length == 0 || target.IndexOfAny(['/', '#']) < 0 && MarkdownInlines.Web(target) is null || title.Length == 1 || title.Length > 1 && (title[0], title[^1]) is not (('"', '"') or ('\'', '\'') or ('(', ')'))) continue;
            names.TryAdd(line[(at + 1)..close].Trim(), target);
            lines[i] = "";
        }
        return names;
    }

    private static List<MdBlock> Blocks(List<string> lines, int depth, MarkdownInlines inlines)
    {
        var blocks = new List<MdBlock>();
        int i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (Blank(line))
            {
                i++;
                continue;
            }
            inlines.Budget.Spend();
            if (Fence(line) is { } fence)
            {
                var code = new List<string>();
                for (i++; i < lines.Count && !Closes(lines[i], fence); i++)
                {
                    inlines.Budget.Spend();
                    code.Add(Outdent(lines[i], fence.Indent));
                }
                i++;
                blocks.Add(new MdCode(string.Join('\n', code)));
            }
            else if (Heading(line, loose: true) is { } heading)
            {
                blocks.Add(new MdHeading(heading.Level, inlines.Parse(heading.Text)));
                i++;
            }
            else if (Rule(line))
            {
                blocks.Add(new MdRule());
                i++;
            }
            else if (depth < MaxDepth && Quoted(line))
            {
                var inner = new List<string>();
                for (; i < lines.Count && Quoted(lines[i]); i++) inner.Add(Unquote(lines[i]));
                blocks.Add(new MdQuote(Blocks(inner, depth + 1, inlines)));
            }
            else if (depth < MaxDepth && ListItem(line) is { } first) blocks.Add(ListOf(lines, ref i, first, depth, inlines));
            else if (Indent(line) >= 4)
            {
                var code = new List<string>();
                for (; i < lines.Count && (Indent(lines[i]) >= 4 || Blank(lines[i])); i++)
                {
                    inlines.Budget.Spend();
                    code.Add(Outdent(lines[i], 4));
                }
                while (Blank(code[^1])) code.RemoveAt(code.Count - 1);
                blocks.Add(new MdCode(string.Join('\n', code)));
            }
            else if (TableColumns(lines, i) is > 0 and var columns) blocks.Add(Table(lines, ref i, columns, inlines));
            else blocks.Add(Paragraph(lines, ref i, depth, inlines));
        }
        return blocks;
    }

    private static MdBlock Paragraph(List<string> lines, ref int i, int depth, MarkdownInlines inlines)
    {
        var text = new StringBuilder(lines[i++].TrimStart());
        for (; i < lines.Count && !Blank(lines[i]); i++)
        {
            var line = lines[i];
            if (Underline(line) is { } level)
            {
                i++;
                return new MdHeading(level, inlines.Parse(text.ToString().Trim()));
            }
            if (Starts(lines, i, depth)) break;
            text.Append('\n').Append(line.TrimStart());
        }
        return new MdParagraph(inlines.Parse(text.ToString().TrimEnd()));
    }

    // whether this line ends the paragraph above it. A numbered list only does from 1, or "in 1986.\n1987. was" splits
    private static bool Starts(List<string> lines, int i, int depth)
    {
        var line = lines[i];
        if (Fence(line) is not null || Heading(line, loose: false) is not null || Rule(line) || TableColumns(lines, i) > 0) return true;
        return depth < MaxDepth && (Quoted(line) || ListItem(line) is { Empty: false } item && (!item.Ordered || item.Start == 1));
    }

    private static MdBlock ListOf(List<string> lines, ref int i, ListMark first, int depth, MarkdownInlines inlines)
    {
        var items = new List<IReadOnlyList<MdBlock>>();
        while (i < lines.Count && !Rule(lines[i]) && ListItem(lines[i]) is { } mark && mark.Ordered == first.Ordered)
        {
            inlines.Budget.Spend();
            var item = new List<string> { mark.Empty ? "" : lines[i][mark.Content..] };
            for (i++; i < lines.Count; i++)
            {
                var line = lines[i];
                if (Blank(line))
                {
                    int next = i;
                    while (next < lines.Count && Blank(lines[next])) next++;
                    if (next == lines.Count || Indent(lines[next]) < mark.Content) break;
                    for (; i < next - 1; i++) item.Add("");
                    item.Add("");
                }
                else if (Indent(line) >= mark.Content) item.Add(line[mark.Content..]);
                else if (Blank(item[^1]) || ListItem(line) is not null || Starts(lines, i, depth)) break;
                else item.Add(line.TrimStart());
            }
            items.Add(Blocks(item, depth + 1, inlines));
            while (i < lines.Count && Blank(lines[i])) i++;
        }
        return new MdList(first.Ordered ? first.Start : null, items);
    }

    private static MdBlock Table(List<string> lines, ref int i, int columns, MarkdownInlines inlines)
    {
        List<IReadOnlyList<MdInline>> Row(string line)
        {
            var cells = Cells(line);
            var row = new List<IReadOnlyList<MdInline>>();
            for (int c = 0; c < columns; c++)
            {
                inlines.Budget.Spend(2);
                row.Add(inlines.Parse(c < cells.Count ? cells[c] : ""));
            }
            return row;
        }
        var header = Row(lines[i]);
        var rows = new List<IReadOnlyList<IReadOnlyList<MdInline>>>();
        for (i += 2; i < lines.Count && !Blank(lines[i]) && Fence(lines[i]) is null && Heading(lines[i], loose: false) is null && !Quoted(lines[i]); i++)
            rows.Add(Row(lines[i]));
        return new MdTable(header, rows);
    }

    private static int TableColumns(List<string> lines, int i)
    {
        if (i + 1 >= lines.Count || !lines[i].Contains('|') || !lines[i + 1].Contains('|') || !lines[i + 1].Contains('-')) return 0;
        var marks = Cells(lines[i + 1]);
        if (marks.Count > MaxColumns || !marks.All(DashCell)) return 0;
        return Cells(lines[i]).Count == marks.Count ? marks.Count : 0;
    }

    private static bool DashCell(string cell)
    {
        var dashes = cell.AsSpan();
        if (dashes.StartsWith(":")) dashes = dashes[1..];
        if (dashes.EndsWith(":")) dashes = dashes[..^1];
        return dashes.Length > 0 && !dashes.ContainsAnyExcept('-');
    }

    private static List<string> Cells(string line)
    {
        var row = line.Trim();
        if (row.StartsWith('|')) row = row[1..];
        if (row.EndsWith('|') && !row.EndsWith("\\|", StringComparison.Ordinal)) row = row[..^1];
        var cells = new List<string>();
        var cell = new StringBuilder();
        for (int i = 0; i < row.Length; i++)
        {
            if (row[i] == '\\' && i + 1 < row.Length && row[i + 1] == '|') cell.Append(row[++i]);
            else if (row[i] == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else cell.Append(row[i]);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }

    private static int Indent(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] == ' ') i++;
        return i;
    }

    private static bool Blank(string line) => line.AsSpan().Trim().IsEmpty;

    private static string Outdent(string line, int by) => line[Math.Min(Indent(line), by)..];

    private static FenceMark? Fence(string line)
    {
        int at = Indent(line);
        if (at > 3 || at >= line.Length || line[at] is not ('`' or '~')) return null;
        int end = at;
        while (end < line.Length && line[end] == line[at]) end++;
        if (end - at < 3 || line[at] == '`' && line.IndexOf('`', end) >= 0) return null;
        return new FenceMark(line[at], end - at, at);
    }

    private static bool Closes(string line, FenceMark fence)
    {
        int at = Indent(line);
        int end = at;
        while (end < line.Length && line[end] == fence.Mark) end++;
        return at <= 3 && end - at >= fence.Count && Blank(line[end..]);
    }

    private static (int Level, string Text)? Heading(string line, bool loose)
    {
        int at = Indent(line);
        int end = at;
        while (end < line.Length && line[end] == '#') end++;
        int level = end - at;
        if (at > 3 || level is 0 or > 6) return null;
        bool spaced = end == line.Length || line[end] == ' ';
        if (!spaced && !loose) return null;
        var text = line[end..].Trim();
        int last = text.Length;
        while (last > 0 && text[last - 1] == '#') last--;
        if (last < text.Length && (last == 0 || text[last - 1] == ' ' || !spaced)) text = text[..last].TrimEnd();
        return (level, text);
    }

    private static int? Underline(string line)
    {
        var marks = line.AsSpan().Trim();
        if (Indent(line) > 3 || marks.Length == 0 || marks[0] is not ('=' or '-') || marks.ContainsAnyExcept(marks[0])) return null;
        return marks[0] == '=' ? 1 : 2;
    }

    private static bool Rule(string line)
    {
        int at = Indent(line);
        if (at > 3 || at >= line.Length || line[at] is not ('-' or '*' or '_')) return false;
        int marks = 0;
        for (int i = at; i < line.Length; i++)
        {
            if (line[i] == line[at]) marks++;
            else if (line[i] != ' ') return false;
        }
        return marks >= 3;
    }

    private static bool Quoted(string line)
    {
        int at = Indent(line);
        return at <= 3 && at < line.Length && line[at] == '>';
    }

    private static string Unquote(string line)
    {
        int at = Indent(line) + 1;
        return at < line.Length && line[at] == ' ' ? line[(at + 1)..] : line[at..];
    }

    private static ListMark? ListItem(string line)
    {
        int at = Indent(line);
        if (at > 3 || at >= line.Length) return null;
        int i = at, start = 1;
        bool ordered = false;
        if (line[i] is '-' or '*' or '+') i++;
        else
        {
            while (i < line.Length && i - at < 9 && char.IsAsciiDigit(line[i])) i++;
            if (i == at || i >= line.Length || line[i] is not ('.' or ')')) return null;
            start = int.Parse(line.AsSpan(at, i - at), CultureInfo.InvariantCulture);
            ordered = true;
            i++;
        }
        if (i == line.Length) return new ListMark(ordered, start, i + 1, true);
        if (line[i] != ' ') return null;
        int spaces = 0;
        while (i + spaces < line.Length && line[i + spaces] == ' ') spaces++;
        bool empty = i + spaces == line.Length;
        return new ListMark(ordered, start, i + (empty || spaces > 4 ? 1 : spaces), empty);
    }
}
