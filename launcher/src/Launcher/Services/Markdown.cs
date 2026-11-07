using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LeafItAlone.Launcher.Services;

/// <summary>
/// 2b: a tiny, dependency-free GitHub-flavoured Markdown <em>parser</em> used by the
/// "What's new" panel. The app ships no NuGet packages (offline build), so rather than
/// pull in Markdig we recognise the handful of constructs release notes actually use:
/// ATX headings, unordered + ordered lists, blockquotes, fenced and inline code,
/// bold / italic / strike, links and horizontal rules.
///
/// Parsing is deliberately free of any WPF type so it is unit-testable headless
/// (<see cref="Parse"/> returns a plain intermediate model). The WPF conversion lives in
/// <see cref="MarkdownRenderer"/>. Anything unrecognised degrades to readable paragraph
/// text rather than throwing, so a malformed body still shows sensibly.
/// </summary>
public static class Markdown
{
    // ---- Intermediate model (pure, no WPF) ----

    public abstract class Block { }
    public sealed class HeadingBlock : Block { public int Level; public List<Inline> Inlines = new(); }
    public sealed class ParagraphBlock : Block { public List<Inline> Inlines = new(); }
    public sealed class QuoteBlock : Block { public List<Inline> Inlines = new(); }
    public sealed class CodeBlock : Block { public string Text = ""; }
    public sealed class RuleBlock : Block { }
    public sealed class ListBlock : Block { public bool Ordered; public List<List<Inline>> Items = new(); }

    public abstract class Inline { }
    public sealed class TextInline : Inline { public string Text = ""; }
    public sealed class CodeInline : Inline { public string Text = ""; }
    public sealed class BoldInline : Inline { public List<Inline> Children = new(); }
    public sealed class ItalicInline : Inline { public List<Inline> Children = new(); }
    public sealed class StrikeInline : Inline { public List<Inline> Children = new(); }
    public sealed class LinkInline : Inline { public List<Inline> Children = new(); public string Url = ""; }

    // ---- Block-level regexes ----

    private static readonly Regex HeadingRx = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex RuleRx = new(@"^\s{0,3}(?:(?:\*\s*){3,}|(?:-\s*){3,}|(?:_\s*){3,})$", RegexOptions.Compiled);
    private static readonly Regex ListRx = new(@"^\s*(?:([-*+])|(\d{1,9})[.)])\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex QuoteRx = new(@"^\s{0,3}>\s?(.*)$", RegexOptions.Compiled);
    private static readonly Regex FenceRx = new(@"^\s{0,3}(?:```|~~~)", RegexOptions.Compiled);

    /// <summary>Parse markdown into a flat list of blocks. Never throws.</summary>
    public static List<Block> Parse(string markdown)
    {
        var blocks = new List<Block>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        var para = new List<string>();
        var quote = new List<string>();
        ListBlock? list = null;
        var fence = new List<string>();
        bool inFence = false;

        void FlushPara()
        {
            if (para.Count == 0) return;
            var inlines = ParseInline(string.Join(" ", para));
            if (HasContent(inlines)) blocks.Add(new ParagraphBlock { Inlines = inlines });
            para.Clear();
        }
        void FlushQuote()
        {
            if (quote.Count == 0) return;
            blocks.Add(new QuoteBlock { Inlines = ParseInline(string.Join(" ", quote)) });
            quote.Clear();
        }
        void FlushList()
        {
            if (list is { Items.Count: > 0 }) blocks.Add(list);
            list = null;
        }
        void FlushAll() { FlushPara(); FlushQuote(); FlushList(); }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            // Fenced code: everything between two fences is literal.
            if (FenceRx.IsMatch(line))
            {
                if (inFence)
                {
                    blocks.Add(new CodeBlock { Text = string.Join("\n", fence) });
                    fence.Clear();
                    inFence = false;
                }
                else
                {
                    FlushAll();
                    inFence = true;
                }
                continue;
            }
            if (inFence) { fence.Add(raw); continue; }

            // Blank line terminates the current paragraph / quote / list run.
            if (string.IsNullOrWhiteSpace(line)) { FlushAll(); continue; }

            var h = HeadingRx.Match(line);
            if (h.Success)
            {
                FlushAll();
                blocks.Add(new HeadingBlock { Level = h.Groups[1].Value.Length, Inlines = ParseInline(h.Groups[2].Value) });
                continue;
            }

            if (RuleRx.IsMatch(line)) { FlushAll(); blocks.Add(new RuleBlock()); continue; }

            var q = QuoteRx.Match(line);
            if (q.Success)
            {
                FlushPara(); FlushList();
                quote.Add(q.Groups[1].Value);
                continue;
            }

            var li = ListRx.Match(line);
            if (li.Success)
            {
                FlushPara(); FlushQuote();
                bool ordered = li.Groups[2].Success;
                // A change of list type starts a fresh list block.
                if (list is not null && list.Ordered != ordered) FlushList();
                list ??= new ListBlock { Ordered = ordered };
                list.Items.Add(ParseInline(li.Groups[3].Value));
                continue;
            }

            // Default: a paragraph continuation line.
            FlushQuote(); FlushList();
            para.Add(line.Trim());
        }

        if (inFence && fence.Count > 0) blocks.Add(new CodeBlock { Text = string.Join("\n", fence) });
        FlushAll();
        return blocks;
    }

    // ---- Inline-level ----

    // Code first so its contents are literal; then bold (two markers) before italic
    // (one marker). Underscore emphasis is intentionally NOT supported: release notes
    // mention identifiers like coreassets_assets_actors and intraword '_' would corrupt them.
    private static readonly Regex InlineRx = new(
        @"`(?<code>[^`]+)`"
        + @"|\*\*(?<bold>.+?)\*\*"
        + @"|\*(?<ital>.+?)\*"
        + @"|~~(?<strike>.+?)~~"
        + @"|\[(?<linkText>[^\]]*?)\]\((?<linkUrl>[^)\s]+)(?:\s+""[^""]*""|\s+'[^']*')?\)",
        RegexOptions.Compiled);

    public static List<Inline> ParseInline(string text)
    {
        var nodes = new List<Inline>();
        if (string.IsNullOrEmpty(text)) return nodes;

        int pos = 0;
        foreach (Match m in InlineRx.Matches(text))
        {
            if (m.Index > pos) AddText(nodes, text.Substring(pos, m.Index - pos));
            pos = m.Index + m.Length;

            if (m.Groups["code"].Success)
                nodes.Add(new CodeInline { Text = m.Groups["code"].Value.Trim() });
            else if (m.Groups["bold"].Success)
                nodes.Add(new BoldInline { Children = ParseInline(m.Groups["bold"].Value) });
            else if (m.Groups["ital"].Success)
                nodes.Add(new ItalicInline { Children = ParseInline(m.Groups["ital"].Value) });
            else if (m.Groups["strike"].Success)
                nodes.Add(new StrikeInline { Children = ParseInline(m.Groups["strike"].Value) });
            else if (m.Groups["linkUrl"].Success)
                nodes.Add(new LinkInline { Children = ParseInline(m.Groups["linkText"].Value), Url = m.Groups["linkUrl"].Value });
        }
        if (pos < text.Length) AddText(nodes, text.Substring(pos));

        return nodes;
    }

    private static void AddText(List<Inline> nodes, string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        // Merge with a trailing text node so runs stay compact.
        if (nodes.Count > 0 && nodes[^1] is TextInline t) t.Text += s;
        else nodes.Add(new TextInline { Text = s });
    }

    private static bool HasContent(List<Inline> inlines)
        => inlines.Any(n => n is not TextInline tt || !string.IsNullOrWhiteSpace(tt.Text));
}
