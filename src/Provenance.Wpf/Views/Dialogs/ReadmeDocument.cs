using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using Provenance.Common.Game.ThirdParty;
using Serilog;

namespace Provenance.Wpf.Views.Dialogs;

internal static class ReadmeDocument
{
    private static readonly double[] HeadingSizes = [22, 18, 15.5, 14, 13, 13];
    private static readonly Thickness BlockGap = new(0, 0, 0, 10), ItemGap = new(0, 0, 0, 4);

    public static async Task<FlowDocument> BuildAsync(MarkdownDoc doc, Action<Uri> openLink, CancellationToken cancel)
    {
        var page = NewPage();
        bool whole = true;
        var clock = Stopwatch.StartNew();
        foreach (var block in doc.Blocks)
        {
            if (!TryBlock(block, openLink, out var built)) { whole = false; break; }
            page.Blocks.Add(built);
            if (clock.ElapsedMilliseconds < 15) continue;
            await Dispatcher.Yield(DispatcherPriority.Background);
            cancel.ThrowIfCancellationRequested();
            clock.Restart();
        }
        Finish(page, doc.Truncated || !whole);
        return page;
    }

    private static FlowDocument NewPage()
    {
        var page = new FlowDocument { PagePadding = new Thickness(18, 12, 18, 14), FontSize = 13, TextAlignment = TextAlignment.Left };
        page.SetResourceReference(FlowDocument.FontFamilyProperty, "Font.Body");
        page.SetResourceReference(FlowDocument.ForegroundProperty, "Text.1");
        return page;
    }

    private static bool TryBlock(MdBlock block, Action<Uri> openLink, out Block built)
    {
        try
        {
            built = ToBlock(block, BlockGap, openLink);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Log.Warning(e, "Could not lay out part of a readme, leaving the rest out");
            built = null!;
            return false;
        }
    }

    private static void Finish(FlowDocument page, bool leftOut)
    {
        if (!leftOut) return;
        var note = new Paragraph(new Run("The rest of this file was left out.")) { FontStyle = FontStyles.Italic, Margin = new Thickness(0, 4, 0, 0) };
        note.SetResourceReference(TextElement.ForegroundProperty, "Text.3");
        page.Blocks.Add(note);
    }

    private static Block ToBlock(MdBlock block, Thickness gap, Action<Uri> openLink)
    {
        switch (block)
        {
            case MdHeading heading:
                var title = new Paragraph { FontSize = HeadingSizes[heading.Level - 1], FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, heading.Level < 3 ? 8 : 4, 0, 8) };
                if (heading.Level < 3)
                {
                    title.Padding = new Thickness(0, 0, 0, 4);
                    title.BorderThickness = new Thickness(0, 0, 0, 1);
                    title.SetResourceReference(Block.BorderBrushProperty, "Border.Pane");
                }
                title.Inlines.AddRange(heading.Text.Select(i => ToInline(i, openLink)));
                return title;
            case MdParagraph paragraph:
                var text = new Paragraph { Margin = gap };
                text.Inlines.AddRange(paragraph.Text.Select(i => ToInline(i, openLink)));
                return text;
            case MdCode code:
                var box = new Paragraph { FontSize = 12.5, Padding = new Thickness(10, 8, 10, 8), Margin = BlockGap, BorderThickness = new Thickness(1) };
                foreach (var line in code.Text.Split('\n'))
                {
                    if (box.Inlines.Count > 0) box.Inlines.Add(new LineBreak());
                    box.Inlines.Add(new Run(line));
                }
                box.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
                box.SetResourceReference(TextElement.BackgroundProperty, "Surface.2");
                box.SetResourceReference(Block.BorderBrushProperty, "Border.Hair");
                return box;
            case MdList list:
                var items = new List
                {
                    MarkerStyle = list.Start is null ? TextMarkerStyle.Disc : TextMarkerStyle.Decimal, StartIndex = Math.Max(1, list.Start ?? 1),
                    Margin = gap == ItemGap ? new Thickness(0, 0, 0, 2) : BlockGap, Padding = new Thickness(24, 0, 0, 0), MarkerOffset = 7,
                };
                foreach (var entry in list.Items)
                {
                    var item = new ListItem();
                    item.Blocks.AddRange(entry.Select(b => ToBlock(b, ItemGap, openLink)));
                    if (item.Blocks.Count == 0) item.Blocks.Add(new Paragraph { Margin = ItemGap });
                    items.ListItems.Add(item);
                }
                return items;
            case MdQuote quote:
                var aside = new Section { Margin = BlockGap, Padding = new Thickness(12, 2, 0, 0), BorderThickness = new Thickness(3, 0, 0, 0) };
                aside.SetResourceReference(TextElement.ForegroundProperty, "Text.2");
                aside.SetResourceReference(Block.BorderBrushProperty, "Border.Pane");
                aside.Blocks.AddRange(quote.Blocks.Select(b => ToBlock(b, ItemGap, openLink)));
                return aside;
            case MdTable table:
                return ToTable(table, openLink);
            default:
                var rule = new Paragraph { FontSize = 1, Margin = new Thickness(0, 6, 0, 14), BorderThickness = new Thickness(0, 1, 0, 0) };
                rule.SetResourceReference(Block.BorderBrushProperty, "Border.Pane");
                return rule;
        }
    }

    private static Table ToTable(MdTable table, Action<Uri> openLink)
    {
        var grid = new Table { CellSpacing = 0, Margin = BlockGap, BorderThickness = new Thickness(1, 1, 0, 0) };
        grid.SetResourceReference(Block.BorderBrushProperty, "Border.Pane");
        // a flow table can't size a column to what's in it, so each gets a share by its longest cell
        for (int c = 0; c < table.Header.Count; c++)
        {
            int longest = table.Rows.Append(table.Header).Max(r => Length(r[c]));
            grid.Columns.Add(new TableColumn { Width = new GridLength(Math.Clamp(longest, 6, 48), GridUnitType.Star) });
        }
        var rows = new TableRowGroup();
        rows.Rows.Add(ToRow(table.Header, true, openLink));
        foreach (var row in table.Rows) rows.Rows.Add(ToRow(row, false, openLink));
        grid.RowGroups.Add(rows);
        return grid;
    }

    private static TableRow ToRow(IReadOnlyList<IReadOnlyList<MdInline>> cells, bool header, Action<Uri> openLink)
    {
        var row = new TableRow();
        if (header)
        {
            row.FontWeight = FontWeights.SemiBold;
            row.SetResourceReference(TextElement.BackgroundProperty, "Surface.2");
        }
        foreach (var cell in cells)
        {
            var text = new Paragraph { Margin = new Thickness(0) };
            text.Inlines.AddRange(cell.Select(i => ToInline(i, openLink)));
            var box = new TableCell(text) { Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(0, 0, 1, 1) };
            box.SetResourceReference(TableCell.BorderBrushProperty, "Border.Pane");
            row.Cells.Add(box);
        }
        return row;
    }

    private static int Length(IReadOnlyList<MdInline> inlines) => inlines.Sum(i => i switch
    {
        MdText t => t.Text.Length,
        MdCodeSpan c => c.Text.Length,
        MdStyle s => Length(s.Inner),
        MdLink l => Length(l.Inner),
        _ => 0,
    });

    private static Inline ToInline(MdInline inline, Action<Uri> openLink)
    {
        switch (inline)
        {
            case MdText text:
                return new Run(text.Text);
            case MdCodeSpan code:
                var mono = new Run(code.Text);
                mono.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
                mono.SetResourceReference(TextElement.BackgroundProperty, "Surface.2");
                return mono;
            case MdStyle style:
                var span = style.Bold ? new Span { FontWeight = FontWeights.Bold } : new Span { FontStyle = FontStyles.Italic };
                span.Inlines.AddRange(style.Inner.Select(i => ToInline(i, openLink)));
                return span;
            case MdLink link:
                // no NavigateUri, so the only thing a press ever does is hand the address over
                var hyperlink = new Hyperlink();
                if (link.Inner is not [MdText shown] || !Uri.TryCreate(shown.Text, UriKind.Absolute, out var typed) || typed != link.Web) hyperlink.ToolTip = link.Web.AbsoluteUri;
                hyperlink.SetResourceReference(TextElement.ForegroundProperty, "Primary");
                hyperlink.Click += (_, _) => openLink(link.Web);
                hyperlink.Inlines.AddRange(link.Inner.Select(i => ToInline(i, openLink)));
                return hyperlink;
            default:
                return new LineBreak();
        }
    }
}
