using System.Net;
using System.Text;

namespace Provenance.Common.Game.ThirdParty;

internal sealed class MarkdownInlines(Markdown.Budget budget, Dictionary<string, string> linkNames)
{
    private const int MaxOpen = 16, MaxLink = 2048, MaxTitle = 1000;

    public Markdown.Budget Budget => budget;
    private Dictionary<string, string> LinkNames => linkNames;

    private sealed class Open
    {
        public int At, Count, Source;
        public char Mark;
        public bool Image;
    }

    public List<MdInline> Parse(string text) => new Run(this, text).Read();

    // a whole http or https link and nothing else. No user@ part either, that's only ever there to mislead
    internal static Uri? Web(string target)
    {
        if (target.Length > MaxLink || !Uri.TryCreate(target, UriKind.Absolute, out var uri)) return null;
        return (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.Host.Length > 0 && uri.UserInfo.Length == 0 ? uri : null;
    }

    private sealed class Run(MarkdownInlines owner, string text)
    {
        private readonly List<MdInline> _nodes = [];
        private readonly StringBuilder _pending = new();
        private readonly List<Open> _marks = [], _brackets = [];
        private readonly HashSet<int> _unclosed = [];
        private int _i;

        public List<MdInline> Read()
        {
            while (_i < text.Length)
            {
                char c = text[_i];
                if (c == '\\') Escape();
                else if (c == '`') Code();
                else if (c == '\n') NewLine();
                else if (c is '*' or '_') Marks(c);
                else if (c == '[' || c == '!' && At(_i + 1) == '[') Bracket(c == '!');
                else if (c == ']') CloseBracket();
                else if (c == '<') AngleLink();
                else if (c == '&') Entity();
                else if (c is not ('h' or 'H') || char.IsLetterOrDigit(At(_i - 1)) || !BareLink()) Take(1);
            }
            Flush();
            return Tidy(_nodes);
        }

        private char At(int i) => i >= 0 && i < text.Length ? text[i] : ' ';

        private void Take(int count)
        {
            _pending.Append(text, _i, count);
            _i += count;
        }

        private void Flush()
        {
            if (_pending.Length == 0) return;
            Add(new MdText(_pending.ToString()));
            _pending.Clear();
        }

        private void Add(MdInline node)
        {
            owner.Budget.Spend(node is MdLink ? 8 : 1);
            _nodes.Add(node);
        }

        private void Escape()
        {
            char next = At(_i + 1);
            if (next == '\n' && _i + 1 < text.Length)
            {
                Flush();
                Add(new MdBreak());
            }
            else if (_i + 1 < text.Length && (char.IsPunctuation(next) || char.IsSymbol(next)) && next < 128) _pending.Append(next);
            else
            {
                Take(1);
                return;
            }
            _i += 2;
        }

        private void NewLine()
        {
            int spaces = 0;
            while (spaces < _pending.Length && _pending[_pending.Length - 1 - spaces] == ' ') spaces++;
            _pending.Length -= spaces;
            if (spaces >= 2)
            {
                Flush();
                Add(new MdBreak());
            }
            else _pending.Append(' ');
            _i++;
        }

        private int RunLength(int from, char mark)
        {
            int end = from;
            while (end < text.Length && text[end] == mark) end++;
            return end - from;
        }

        private void Code()
        {
            int ticks = RunLength(_i, '`');
            int from = _i + ticks, close = -1;
            for (int p = from; !_unclosed.Contains(ticks) && p < text.Length; p++)
            {
                if (text[p] != '`') continue;
                int length = RunLength(p, '`');
                if (length == ticks)
                {
                    close = p;
                    break;
                }
                p += length;
            }
            if (close < 0)
            {
                _unclosed.Add(ticks);
                Take(ticks);
                return;
            }
            var code = text[from..close].Replace('\n', ' ');
            if (code.Length > 1 && code[0] == ' ' && code[^1] == ' ' && code.AsSpan().ContainsAnyExcept(' ')) code = code[1..^1];
            Flush();
            Add(new MdCodeSpan(code));
            _i = close + ticks;
        }

        private void Marks(char mark)
        {
            int count = RunLength(_i, mark);
            // no more than three, so one opener can't be closed over and over into a thousand layers
            if (count > 3)
            {
                Take(count);
                return;
            }
            char before = At(_i - 1), after = At(_i + count);
            bool spaceBefore = char.IsWhiteSpace(before), spaceAfter = char.IsWhiteSpace(after);
            bool markBefore = char.IsPunctuation(before) || char.IsSymbol(before), markAfter = char.IsPunctuation(after) || char.IsSymbol(after);
            bool left = !spaceAfter && (!markAfter || spaceBefore || markBefore);
            bool right = !spaceBefore && (!markBefore || spaceAfter || markAfter);
            bool opens = left && (mark == '*' || !right || markBefore), closes = right && (mark == '*' || !left || markAfter);
            _i += count;
            Flush();
            while (closes && count > 0 && _marks.FindLastIndex(m => m.Mark == mark) is >= 0 and var at)
            {
                var open = _marks[at];
                int used = open.Count >= 2 && count >= 2 ? 2 : 1;
                var inner = Cut(open.At + 1);
                _marks.RemoveRange(at + 1, _marks.Count - at - 1);
                _brackets.RemoveAll(b => b.At > open.At);
                open.Count -= used;
                count -= used;
                _nodes[open.At] = new MdText(new string(mark, open.Count));
                if (open.Count == 0) _marks.RemoveAt(at);
                Add(new MdStyle(used == 2, inner));
            }
            if (count == 0) return;
            if (opens && _marks.Count < MaxOpen) _marks.Add(new Open { At = _nodes.Count, Count = count, Mark = mark });
            Add(new MdText(new string(mark, count)));
        }

        private List<MdInline> Cut(int from)
        {
            var cut = _nodes.GetRange(from, _nodes.Count - from);
            _nodes.RemoveRange(from, cut.Count);
            return Tidy(cut);
        }

        private void Bracket(bool image)
        {
            int length = image ? 2 : 1;
            if (_brackets.Count >= MaxOpen)
            {
                Take(length);
                return;
            }
            Flush();
            _brackets.Add(new Open { At = _nodes.Count, Source = _i + length, Image = image });
            Add(new MdText(text.Substring(_i, length)));
            _i += length;
        }

        private void CloseBracket()
        {
            var open = _brackets.Count > 0 ? _brackets[^1] : null;
            if (open is not null) _brackets.RemoveAt(_brackets.Count - 1);
            if (open is null || Target(open) is not { } found)
            {
                Take(1);
                return;
            }
            var (target, end) = found;
            Flush();
            var inner = Unlinked(Cut(open.At + 1));
            _nodes.RemoveAt(open.At);
            _marks.RemoveAll(m => m.At >= open.At);
            _i = end;
            var web = Web(target);
            if (open.Image) inner = Plain(inner) is { Length: > 0 } words ? [new MdText(words)] : [];
            else if (inner.Count == 0 && web is not null) inner = [new MdText(target)];
            if (web is null || inner.Count == 0) foreach (var node in inner) Add(node);
            else Add(new MdLink(inner, web));
        }

        private (string Target, int End)? Target(Open open)
        {
            int p = _i + 1;
            if (At(p) == '(' && p < text.Length) return Inline(p);
            var label = text[open.Source.._i];
            int end = p;
            if (At(p) == '[' && p < text.Length)
            {
                int close = text.AsSpan(p, Math.Min(MaxTitle, text.Length - p)).IndexOf(']');
                if (close < 0) return null;
                if (close > 1) label = text[(p + 1)..(p + close)];
                end = p + close + 1;
            }
            return owner.LinkNames.TryGetValue(label.Trim(), out var target) ? (target, end) : null;
        }

        private (string Target, int End)? Inline(int open)
        {
            int p = Skip(open + 1);
            string target;
            if (At(p) == '<')
            {
                int close = text.AsSpan(p, Math.Min(MaxLink, text.Length - p)).IndexOfAny('>', '\n');
                if (close < 0 || text[p + close] != '>') return null;
                target = text[(p + 1)..(p + close)];
                p += close + 1;
            }
            else
            {
                int start = p, depth = 0;
                for (; p < text.Length && p - start <= MaxLink && text[p] is not (' ' or '\n'); p++)
                {
                    if (text[p] == '\\' && p + 1 < text.Length) p++;
                    else if (text[p] == '(') depth++;
                    else if (text[p] == ')' && depth-- == 0) break;
                }
                if (p - start > MaxLink) return null;
                target = Unescaped(text[start..Math.Min(p, text.Length)]);
            }
            p = Skip(p);
            if (At(p) is '"' or '\'' or '(' && p < text.Length)
            {
                int close = text.AsSpan(p + 1, Math.Min(MaxTitle, text.Length - p - 1)).IndexOf(text[p] == '(' ? ')' : text[p]);
                if (close < 0) return null;
                p = Skip(p + close + 2);
            }
            return p < text.Length && text[p] == ')' ? (target, p + 1) : null;
        }

        private int Skip(int p)
        {
            while (p < text.Length && text[p] is ' ' or '\n') p++;
            return p;
        }

        private static string Unescaped(string target)
        {
            if (!target.Contains('\\')) return target;
            var sb = new StringBuilder(target.Length);
            for (int i = 0; i < target.Length; i++)
            {
                if (target[i] == '\\' && i + 1 < target.Length && (char.IsPunctuation(target[i + 1]) || char.IsSymbol(target[i + 1]))) i++;
                sb.Append(target[i]);
            }
            return sb.ToString();
        }

        // any other <tag ...> stays as typed up to its >, so nothing inside one is formatted or made a link
        private void AngleLink()
        {
            int close = text.AsSpan(_i, Math.Min(MaxLink, text.Length - _i)).IndexOf('>');
            if (close > 1 && text.AsSpan(_i, close).IndexOfAny(' ', '\n') < 0 && Web(text[(_i + 1)..(_i + close)]) is { } web)
            {
                Flush();
                Add(new MdLink([new MdText(text[(_i + 1)..(_i + close)])], web));
            }
            else if (close > 1 && (char.IsAsciiLetter(text[_i + 1]) || text[_i + 1] is '/' or '!' or '?'))
                _pending.Append(text, _i, close + 1).Replace('\n', ' ', _pending.Length - close - 1, close + 1);
            else
            {
                Take(1);
                return;
            }
            _i += close + 1;
        }

        private bool BareLink()
        {
            var rest = text.AsSpan(_i);
            if (!rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
            int end = _i;
            while (end < text.Length && end - _i <= MaxLink && !char.IsWhiteSpace(text[end]) && text[end] is not ('<' or '>' or '`')) end++;
            int spare = text.AsSpan(_i, end - _i).Count(')') - text.AsSpan(_i, end - _i).Count('(');
            for (; end > _i; end--)
            {
                char last = text[end - 1];
                if (last == ')' && spare-- > 0) continue;
                if (last is not ('.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"' or '*' or '_')) break;
            }
            if (Web(text[_i..end]) is not { } web) return false;
            Flush();
            Add(new MdLink([new MdText(text[_i..end])], web));
            _i = end;
            return true;
        }

        private void Entity()
        {
            int end = text.AsSpan(_i, Math.Min(34, text.Length - _i)).IndexOf(';');
            var named = end > 1 ? text.Substring(_i, end + 1) : "";
            var decoded = named.Length > 0 && named.AsSpan(1, named.Length - 2).IndexOfAny(' ', '&', '\n') < 0 ? WebUtility.HtmlDecode(named) : named;
            if (decoded == named || decoded.Any(c => char.IsControl(c) || c is >= (char)0x202A and <= (char)0x202E or >= (char)0x2066 and <= (char)0x2069))
            {
                Take(1);
                return;
            }
            _pending.Append(decoded);
            _i += named.Length;
        }

        private static List<MdInline> Unlinked(List<MdInline> nodes) =>
            nodes.SelectMany(n => n switch
            {
                MdLink link => Unlinked([.. link.Inner]),
                MdStyle style => [style with { Inner = Unlinked([.. style.Inner]) }],
                _ => (IEnumerable<MdInline>)[n],
            }).ToList();

        private static string Plain(IEnumerable<MdInline> nodes) =>
            string.Concat(nodes.Select(n => n switch
            {
                MdText t => t.Text,
                MdCodeSpan c => c.Text,
                MdStyle s => Plain(s.Inner),
                MdLink l => Plain(l.Inner),
                _ => " ",
            })).Trim();

        private static List<MdInline> Tidy(List<MdInline> nodes)
        {
            // text that ended up side by side goes back together, through a builder or 20,000 bits of it take seconds
            var tidy = new List<MdInline>(nodes.Count);
            var words = new StringBuilder();
            void Join()
            {
                if (words.Length > 0) tidy.Add(new MdText(words.ToString()));
                words.Clear();
            }
            foreach (var node in nodes)
            {
                if (node is MdText text) words.Append(text.Text);
                else
                {
                    Join();
                    tidy.Add(node);
                }
            }
            Join();
            return tidy;
        }
    }
}
