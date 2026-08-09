using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SonarQuickMixer.Midi;
using WpfPanel = System.Windows.Controls.Panel;
using WpfSize = System.Windows.Size;
using WpfRect = System.Windows.Rect;
using WpfVisibility = System.Windows.Visibility;
using WpfUIElement = System.Windows.UIElement;
using WpfFrameworkElement = System.Windows.FrameworkElement;

namespace SonarQuickMixer.Controls;

/// <summary>
/// Arranges ItemsControl children by <c>Row</c>/<c>Col</c>/<c>RowSpan</c>/<c>ColSpan</c>
/// from <see cref="BlueprintControlVm"/> or <see cref="BlueprintRegionVm"/> DataContext.
/// Track sizes follow content (device does not stretch to the window). When an area is given
/// extra room (ColSpan/RowSpan stretch), free space is distributed per
/// <see cref="MidiContentJustify"/> horizontally and <see cref="BlueprintRegionVm.ContentAlign"/> vertically.
/// Children fill their grid slots (so RowSpan=4 really occupies four rows).
/// </summary>
public sealed class MidiBlueprintCellPanel : WpfPanel
{
    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        var cells = CollectCells();
        if (cells.Count == 0)
        {
            return new WpfSize(0, 0);
        }

        foreach (var cell in cells)
        {
            cell.Element.Measure(availableSize);
        }

        var (colWidths, rowHeights) = ComputeTracks(cells);
        return new WpfSize(Sum(colWidths), Sum(rowHeights));
    }

    protected override WpfSize ArrangeOverride(WpfSize finalSize)
    {
        var cells = CollectCells();
        if (cells.Count == 0)
        {
            return finalSize;
        }

        var (colWidths, rowHeights) = ComputeTracks(cells);
        var totalW = Sum(colWidths);
        var totalH = Sum(rowHeights);
        var (justifyH, justifyV, insideArea) = ReadAreaJustify();

        double gapXBefore;
        double gapXBetween;
        double gapYBefore;
        double gapYBetween;
        if (insideArea)
        {
            (gapXBefore, gapXBetween) = ComputeMainGaps(justifyH, finalSize.Width, totalW, colWidths.Length);
            (gapYBefore, gapYBetween) = ComputeMainGaps(justifyV, finalSize.Height, totalH, rowHeights.Length);
        }
        else
        {
            // Device root: keep the packed block centered in leftover window space.
            gapXBefore = finalSize.Width > totalW ? (finalSize.Width - totalW) / 2 : 0;
            gapYBefore = finalSize.Height > totalH ? (finalSize.Height - totalH) / 2 : 0;
            gapXBetween = 0;
            gapYBetween = 0;
        }

        var colOffsets = PrefixSums(colWidths);
        var rowOffsets = PrefixSums(rowHeights);

        foreach (var cell in cells)
        {
            var x = gapXBefore + colOffsets[cell.Col] + gapXBetween * cell.Col;
            var y = gapYBefore + rowOffsets[cell.Row] + gapYBetween * cell.Row;
            var w = SpanSize(colWidths, cell.Col, cell.ColSpan) + gapXBetween * Math.Max(0, cell.ColSpan - 1);
            var h = SpanSize(rowHeights, cell.Row, cell.RowSpan) + gapYBetween * Math.Max(0, cell.RowSpan - 1);
            cell.Element.Arrange(new WpfRect(x, y, w, h));
        }

        return finalSize;
    }

    private (MidiContentJustify Horizontal, MidiContentJustify Vertical, bool InsideArea) ReadAreaJustify()
    {
        if (TryGetParentRegion(out var region))
        {
            return (region.ContentJustify, region.ContentAlign, true);
        }

        return (MidiContentJustify.Pack, MidiContentJustify.Pack, false);
    }

    private bool TryGetParentRegion(out BlueprintRegionVm region)
    {
        if (DataContext is BlueprintRegionVm direct)
        {
            region = direct;
            return true;
        }

        for (DependencyObject? current = this; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { DataContext: BlueprintRegionVm r })
            {
                region = r;
                return true;
            }
        }

        region = null!;
        return false;
    }

    /// <summary>
    /// Returns (leading edge gap, gap between tracks). Trailing edge matches leading for SpaceEvenly.
    /// </summary>
    internal static (double Before, double Between) ComputeMainGaps(
        MidiContentJustify justify,
        double available,
        double content,
        int trackCount)
    {
        var extra = available - content;
        if (extra <= 0 || trackCount <= 0)
        {
            return (0, 0);
        }

        return justify switch
        {
            MidiContentJustify.SpaceBetween when trackCount > 1
                => (0, extra / (trackCount - 1)),
            MidiContentJustify.SpaceBetween
                => (0, 0),
            MidiContentJustify.SpaceEvenly
                => (extra / (trackCount + 1), extra / (trackCount + 1)),
            _ => (0, 0) // Pack — leftover stays at the end
        };
    }

    private List<Cell> CollectCells()
    {
        var list = new List<Cell>(InternalChildren.Count);
        foreach (WpfUIElement child in InternalChildren)
        {
            if (child.Visibility == WpfVisibility.Collapsed)
            {
                continue;
            }

            var (row, col, rowSpan, colSpan) = ReadPlacement(child);
            list.Add(new Cell(child, row, col, Math.Max(1, rowSpan), Math.Max(1, colSpan)));
        }

        return list;
    }

    private static (int Row, int Col, int RowSpan, int ColSpan) ReadPlacement(WpfUIElement element)
    {
        var dc = (element as WpfFrameworkElement)?.DataContext;
        if (dc is IBlueprintFormCell cell)
        {
            return (cell.Row, cell.Col, cell.RowSpan, cell.ColSpan);
        }

        return (0, 0, 1, 1);
    }

    private static (double[] ColWidths, double[] RowHeights) ComputeTracks(List<Cell> cells)
    {
        var colCount = 0;
        var rowCount = 0;
        foreach (var cell in cells)
        {
            colCount = Math.Max(colCount, cell.Col + cell.ColSpan);
            rowCount = Math.Max(rowCount, cell.Row + cell.RowSpan);
        }

        var colWidths = new double[Math.Max(1, colCount)];
        var rowHeights = new double[Math.Max(1, rowCount)];

        foreach (var cell in cells)
        {
            var size = cell.Element.DesiredSize;
            var perCol = size.Width / cell.ColSpan;
            var perRow = size.Height / cell.RowSpan;
            for (var c = 0; c < cell.ColSpan; c++)
            {
                colWidths[cell.Col + c] = Math.Max(colWidths[cell.Col + c], perCol);
            }

            for (var r = 0; r < cell.RowSpan; r++)
            {
                rowHeights[cell.Row + r] = Math.Max(rowHeights[cell.Row + r], perRow);
            }
        }

        foreach (var cell in cells)
        {
            var size = cell.Element.DesiredSize;
            var spanW = SpanSize(colWidths, cell.Col, cell.ColSpan);
            if (size.Width > spanW && cell.ColSpan > 0)
            {
                var extra = (size.Width - spanW) / cell.ColSpan;
                for (var c = 0; c < cell.ColSpan; c++)
                {
                    colWidths[cell.Col + c] += extra;
                }
            }

            var spanH = SpanSize(rowHeights, cell.Row, cell.RowSpan);
            if (size.Height > spanH && cell.RowSpan > 0)
            {
                var extra = (size.Height - spanH) / cell.RowSpan;
                for (var r = 0; r < cell.RowSpan; r++)
                {
                    rowHeights[cell.Row + r] += extra;
                }
            }
        }

        return (colWidths, rowHeights);
    }

    private static double SpanSize(double[] tracks, int start, int span)
    {
        var sum = 0.0;
        for (var i = 0; i < span && start + i < tracks.Length; i++)
        {
            sum += tracks[start + i];
        }

        return sum;
    }

    private static double[] PrefixSums(double[] tracks)
    {
        var offsets = new double[tracks.Length + 1];
        for (var i = 0; i < tracks.Length; i++)
        {
            offsets[i + 1] = offsets[i] + tracks[i];
        }

        return offsets;
    }

    private static double Sum(double[] values)
    {
        var sum = 0.0;
        foreach (var v in values)
        {
            sum += v;
        }

        return sum;
    }

    private readonly record struct Cell(WpfUIElement Element, int Row, int Col, int RowSpan, int ColSpan);
}
