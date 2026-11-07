using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace LeafItAlone.Launcher.Services;

/// <summary>
/// 2b: turns <see cref="Markdown"/> blocks into a themed WPF <see cref="System.Windows.Documents.FlowDocument"/>
/// for the "What's new" panel. Colours and the UI font are wired by DynamicResource key so
/// the notes follow the active Light/Dark theme and accent exactly like the rest of the app.
/// Links open in the default browser via shell-execute (never in-app). All WPF types are
/// fully qualified to avoid clashing with the pure <see cref="Markdown"/> model names.
/// Lives apart from <see cref="Markdown"/> so the parser stays WPF-free (and headless-testable).
/// </summary>
public static class MarkdownRenderer
{
    private static readonly System.Windows.Media.FontFamily Mono =
        new("Consolas, Cascadia Mono, Courier New");

    public static System.Windows.Documents.FlowDocument Render(string markdown)
    {
        var doc = new System.Windows.Documents.FlowDocument
        {
            PagePadding = new System.Windows.Thickness(0),
            FontSize = 13,
            IsOptimalParagraphEnabled = false,
        };
        doc.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextPrimaryBrush");
        doc.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "AppFont");

        foreach (var b in Markdown.Parse(markdown))
            doc.Blocks.Add(BuildBlock(b));

        // Guarantee at least one block so an empty body does not render a blank viewer.
        if (doc.Blocks.Count == 0)
            doc.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run("")));
        return doc;
    }

    private static System.Windows.Documents.Block BuildBlock(Markdown.Block b)
    {
        switch (b)
        {
            case Markdown.HeadingBlock h:
            {
                var p = new System.Windows.Documents.Paragraph
                {
                    FontWeight = System.Windows.FontWeights.SemiBold,
                    FontSize = h.Level switch { 1 => 20d, 2 => 17d, 3 => 15d, _ => 13.5d },
                    Margin = new System.Windows.Thickness(0, h.Level <= 1 ? 2 : 10, 0, 4),
                };
                AddInlines(p.Inlines, h.Inlines);
                return p;
            }
            case Markdown.ParagraphBlock pb:
            {
                var p = new System.Windows.Documents.Paragraph { Margin = new System.Windows.Thickness(0, 0, 0, 8) };
                AddInlines(p.Inlines, pb.Inlines);
                return p;
            }
            case Markdown.QuoteBlock q:
            {
                var p = new System.Windows.Documents.Paragraph
                {
                    Margin = new System.Windows.Thickness(0, 0, 0, 8),
                    Padding = new System.Windows.Thickness(10, 2, 0, 2),
                    BorderThickness = new System.Windows.Thickness(3, 0, 0, 0),
                    FontStyle = System.Windows.FontStyles.Italic,
                };
                p.SetResourceReference(System.Windows.Documents.Block.BorderBrushProperty, "DividerBrush");
                p.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextSecondaryBrush");
                AddInlines(p.Inlines, q.Inlines);
                return p;
            }
            case Markdown.CodeBlock c:
                return BuildCode(c);
            case Markdown.RuleBlock:
            {
                var line = new System.Windows.Controls.Border
                {
                    Height = 1,
                    Margin = new System.Windows.Thickness(0, 6, 0, 8),
                };
                line.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "DividerBrush");
                return new System.Windows.Documents.BlockUIContainer(line);
            }
            case Markdown.ListBlock lb:
            {
                var list = new System.Windows.Documents.List
                {
                    Margin = new System.Windows.Thickness(0, 0, 0, 8),
                };
                list.MarkerStyle = lb.Ordered
                    ? System.Windows.TextMarkerStyle.Decimal
                    : System.Windows.TextMarkerStyle.Disc;
                foreach (var item in lb.Items)
                {
                    var p = new System.Windows.Documents.Paragraph { Margin = new System.Windows.Thickness(0, 0, 0, 2) };
                    AddInlines(p.Inlines, item);
                    var li = new System.Windows.Documents.ListItem();
                    li.Blocks.Add(p);
                    list.ListItems.Add(li);
                }
                return list;
            }
            default:
            {
                var p = new System.Windows.Documents.Paragraph { Margin = new System.Windows.Thickness(0, 0, 0, 8) };
                return p;
            }
        }
    }

    private static System.Windows.Documents.Block BuildCode(Markdown.CodeBlock c)
    {
        var p = new System.Windows.Documents.Paragraph
        {
            FontFamily = Mono,
            FontSize = 12.5,
            Padding = new System.Windows.Thickness(10, 8, 10, 8),
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        };
        p.SetResourceReference(System.Windows.Documents.Block.BackgroundProperty, "SurfaceAltBrush");

        var lines = (c.Text ?? "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            // Non-breaking spaces so indentation inside a code fence survives.
            p.Inlines.Add(new System.Windows.Documents.Run(lines[i].Replace(" ", "\u00A0")));
            if (i < lines.Length - 1) p.Inlines.Add(new System.Windows.Documents.LineBreak());
        }
        return p;
    }

    private static void AddInlines(System.Windows.Documents.InlineCollection target, List<Markdown.Inline> nodes)
    {
        foreach (var n in nodes)
        {
            switch (n)
            {
                case Markdown.TextInline t:
                    target.Add(new System.Windows.Documents.Run(t.Text));
                    break;
                case Markdown.CodeInline code:
                {
                    var r = new System.Windows.Documents.Run(code.Text) { FontFamily = Mono };
                    r.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextSecondaryBrush");
                    target.Add(r);
                    break;
                }
                case Markdown.BoldInline b:
                {
                    var bold = new System.Windows.Documents.Bold();
                    target.Add(bold);
                    AddInlines(bold.Inlines, b.Children);
                    break;
                }
                case Markdown.ItalicInline it:
                {
                    var em = new System.Windows.Documents.Italic();
                    target.Add(em);
                    AddInlines(em.Inlines, it.Children);
                    break;
                }
                case Markdown.StrikeInline st:
                {
                    var span = new System.Windows.Documents.Span { TextDecorations = System.Windows.TextDecorations.Strikethrough };
                    target.Add(span);
                    AddInlines(span.Inlines, st.Children);
                    break;
                }
                case Markdown.LinkInline lk:
                    target.Add(BuildLink(lk));
                    break;
            }
        }
    }

    private static System.Windows.Documents.Hyperlink BuildLink(Markdown.LinkInline lk)
    {
        var link = new System.Windows.Documents.Hyperlink();
        link.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "AccentBrush");
        if (Uri.TryCreate(lk.Url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            link.NavigateUri = uri;
        AddInlines(link.Inlines, lk.Children.Count > 0 ? lk.Children : new List<Markdown.Inline> { new Markdown.TextInline { Text = lk.Url } });
        link.RequestNavigate += OpenExternal;
        return link;
    }

    private static void OpenExternal(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
            e.Handled = true;
        }
        catch { /* a blocked link should never crash the dialog */ }
    }
}
