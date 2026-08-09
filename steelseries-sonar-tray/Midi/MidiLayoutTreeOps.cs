namespace SonarQuickMixer.Midi;

/// <summary>Pure layout-tree mutations for the constructor (regions + controls).</summary>
public static class MidiLayoutTreeOps
{
    /// <summary>
    /// Set <see cref="MidiDeviceLayout.Rows"/> / <see cref="MidiDeviceLayout.Columns"/>
    /// from the bounding box of root-level areas and controls so JSON matches the visual tree.
    /// </summary>
    public static void SyncRootGridExtent(MidiDeviceLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var maxRow = 1;
        var maxCol = 1;
        var any = false;

        foreach (var region in layout.Regions)
        {
            if (!string.IsNullOrWhiteSpace(region.ParentRegionId))
            {
                continue;
            }

            any = true;
            maxRow = Math.Max(maxRow, region.Row + Math.Max(1, region.RowSpan));
            maxCol = Math.Max(maxCol, region.Col + Math.Max(1, region.ColSpan));
        }

        foreach (var control in layout.Controls)
        {
            if (!string.IsNullOrWhiteSpace(control.RegionId))
            {
                continue;
            }

            any = true;
            maxRow = Math.Max(maxRow, control.Row + Math.Max(1, control.RowSpan));
            maxCol = Math.Max(maxCol, control.Col + Math.Max(1, control.ColSpan));
        }

        if (!any)
        {
            layout.Rows = Math.Max(1, layout.Rows);
            layout.Columns = Math.Max(1, layout.Columns);
            return;
        }

        layout.Rows = Math.Clamp(maxRow, 1, 64);
        layout.Columns = Math.Clamp(maxCol, 1, 64);
    }

    public static MidiLayoutDropZone ResolveDropZone(double x, double y, double width, double height)
    {
        if (width <= 1 || height <= 1)
        {
            return MidiLayoutDropZone.Inside;
        }

        var nx = Math.Clamp(x / width, 0, 1);
        var ny = Math.Clamp(y / height, 0, 1);
        const double edge = 0.22;

        if (nx < edge)
        {
            return MidiLayoutDropZone.Left;
        }

        if (nx > 1 - edge)
        {
            return MidiLayoutDropZone.Right;
        }

        if (ny < edge)
        {
            return MidiLayoutDropZone.Top;
        }

        if (ny > 1 - edge)
        {
            return MidiLayoutDropZone.Bottom;
        }

        return MidiLayoutDropZone.Inside;
    }

    /// <summary>
    /// Drop zones for leaf controls: edges only. Center maps to the nearest edge
    /// (controls cannot host nested children — use areas for <see cref="MidiLayoutDropZone.Inside"/>).
    /// </summary>
    public static MidiLayoutDropZone ResolveDropZoneBesideOnly(double x, double y, double width, double height)
    {
        if (width <= 1 || height <= 1)
        {
            return MidiLayoutDropZone.Right;
        }

        var zone = ResolveDropZone(x, y, width, height);
        if (zone != MidiLayoutDropZone.Inside)
        {
            return zone;
        }

        var nx = Math.Clamp(x / width, 0, 1);
        var ny = Math.Clamp(y / height, 0, 1);
        var distLeft = nx;
        var distRight = 1 - nx;
        var distTop = ny;
        var distBottom = 1 - ny;

        if (distLeft <= distRight && distLeft <= distTop && distLeft <= distBottom)
        {
            return MidiLayoutDropZone.Left;
        }

        if (distRight <= distTop && distRight <= distBottom)
        {
            return MidiLayoutDropZone.Right;
        }

        if (distTop <= distBottom)
        {
            return MidiLayoutDropZone.Top;
        }

        return MidiLayoutDropZone.Bottom;
    }

    public static bool PlaceNewRegion(
        MidiDeviceLayout layout,
        string? targetRegionId,
        MidiLayoutDropZone zone,
        string label,
        out string regionId)
    {
        regionId = GenerateUniqueRegionId(layout);
        var region = new MidiLayoutRegion
        {
            Id = regionId,
            Label = string.IsNullOrWhiteSpace(label) ? string.Empty : label.Trim()
        };

        if (string.IsNullOrWhiteSpace(targetRegionId))
        {
            region.ParentRegionId = null;
            PlaceAsSibling(layout, region, parentId: null, before: null, zone);
            layout.Regions.Add(region);
            return true;
        }

        var target = layout.Regions.FirstOrDefault(r =>
            string.Equals(r.Id, targetRegionId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return false;
        }

        if (zone == MidiLayoutDropZone.Inside)
        {
            region.ParentRegionId = target.Id;
            var (row, col) = FindFreeCellAmongSiblings(layout, target.Id, isRegion: true);
            region.Row = row;
            region.Col = col;
            layout.Regions.Add(region);
            return true;
        }

        region.ParentRegionId = target.ParentRegionId;
        PlaceBeside(layout, region, target, zone);
        layout.Regions.Add(region);
        return true;
    }

    public static bool PlaceNewControl(
        MidiDeviceLayout layout,
        MidiLayoutControl control,
        string? targetRegionId,
        string? targetControlId,
        MidiLayoutDropZone zone)
    {
        ArgumentNullException.ThrowIfNull(control);

        if (!string.IsNullOrWhiteSpace(targetControlId))
        {
            var targetControl = layout.Controls.FirstOrDefault(c =>
                string.Equals(c.Id, targetControlId, StringComparison.OrdinalIgnoreCase));
            if (targetControl is null)
            {
                return false;
            }

            if (zone == MidiLayoutDropZone.Inside)
            {
                // Controls are leaves — nesting is only allowed into areas.
                return false;
            }

            control.RegionId = targetControl.RegionId;
            PlaceControlBeside(layout, control, targetControl, zone);
            layout.Controls.Add(control);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(targetRegionId))
        {
            var target = layout.Regions.FirstOrDefault(r =>
                string.Equals(r.Id, targetRegionId, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                return false;
            }

            if (zone == MidiLayoutDropZone.Inside)
            {
                control.RegionId = target.Id;
                var (row, col) = FindFreeCellAmongSiblings(layout, target.Id, isRegion: false);
                control.Row = row;
                control.Col = col;
                layout.Controls.Add(control);
                return true;
            }

            // Beside a region → sibling control under the region's parent.
            control.RegionId = target.ParentRegionId;
            PlaceBesideRegionAsControl(layout, control, target, zone);
            layout.Controls.Add(control);
            return true;
        }

        control.RegionId = null;
        var free = FindFreeCellAmongSiblings(layout, null, isRegion: false);
        control.Row = free.Row;
        control.Col = free.Col;
        layout.Controls.Add(control);
        return true;
    }

    public static bool MoveControl(
        MidiDeviceLayout layout,
        string controlId,
        string? targetRegionId,
        string? targetControlId,
        MidiLayoutDropZone zone)
    {
        var control = layout.Controls.FirstOrDefault(c =>
            string.Equals(c.Id, controlId, StringComparison.OrdinalIgnoreCase));
        if (control is null)
        {
            return false;
        }

        // Cannot place a control relative to itself (and would lose it after Remove).
        if (!string.IsNullOrWhiteSpace(targetControlId)
            && string.Equals(controlId, targetControlId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        layout.Controls.Remove(control);
        if (PlaceNewControl(layout, control, targetRegionId, targetControlId, zone))
        {
            return true;
        }

        layout.Controls.Add(control);
        return false;
    }

    public static bool MoveRegion(
        MidiDeviceLayout layout,
        string regionId,
        string? targetRegionId,
        MidiLayoutDropZone zone)
    {
        var region = layout.Regions.FirstOrDefault(r =>
            string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));
        if (region is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(targetRegionId)
            && string.Equals(regionId, targetRegionId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Prevent cycles: cannot move into own descendant.
        if (!string.IsNullOrWhiteSpace(targetRegionId)
            && zone == MidiLayoutDropZone.Inside
            && IsDescendantRegion(layout, ancestorId: regionId, candidateId: targetRegionId!))
        {
            return false;
        }

        layout.Regions.Remove(region);

        if (string.IsNullOrWhiteSpace(targetRegionId))
        {
            region.ParentRegionId = null;
            var free = FindFreeCellAmongSiblings(layout, null, isRegion: true);
            region.Row = free.Row;
            region.Col = free.Col;
            layout.Regions.Add(region);
            return true;
        }

        var target = layout.Regions.FirstOrDefault(r =>
            string.Equals(r.Id, targetRegionId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            layout.Regions.Add(region);
            return false;
        }

        if (zone == MidiLayoutDropZone.Inside)
        {
            region.ParentRegionId = target.Id;
            var free = FindFreeCellAmongSiblings(layout, target.Id, isRegion: true);
            region.Row = free.Row;
            region.Col = free.Col;
            layout.Regions.Add(region);
            return true;
        }

        region.ParentRegionId = target.ParentRegionId;
        PlaceBeside(layout, region, target, zone);
        layout.Regions.Add(region);
        return true;
    }

    public static bool DeleteRegion(MidiDeviceLayout layout, string regionId, bool deleteContents)
    {
        var region = layout.Regions.FirstOrDefault(r =>
            string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));
        if (region is null)
        {
            return false;
        }

        var descendants = CollectDescendantRegionIds(layout, regionId);
        descendants.Add(regionId);

        if (deleteContents)
        {
            layout.Controls.RemoveAll(c =>
                !string.IsNullOrWhiteSpace(c.RegionId) && descendants.Contains(c.RegionId));
            layout.Regions.RemoveAll(r => descendants.Contains(r.Id));
            return true;
        }

        // Re-parent children to this region's parent.
        foreach (var child in layout.Regions.Where(r =>
                     string.Equals(r.ParentRegionId, regionId, StringComparison.OrdinalIgnoreCase)))
        {
            child.ParentRegionId = region.ParentRegionId;
        }

        foreach (var control in layout.Controls.Where(c =>
                     string.Equals(c.RegionId, regionId, StringComparison.OrdinalIgnoreCase)))
        {
            control.RegionId = region.ParentRegionId;
        }

        layout.Regions.RemoveAll(r => string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    private static void PlaceAsSibling(
        MidiDeviceLayout layout,
        MidiLayoutRegion region,
        string? parentId,
        MidiLayoutRegion? before,
        MidiLayoutDropZone zone)
    {
        if (before is null)
        {
            var free = FindFreeCellAmongSiblings(layout, parentId, isRegion: true);
            region.Row = free.Row;
            region.Col = free.Col;
            return;
        }

        PlaceBeside(layout, region, before, zone);
    }

    /// <summary>
    /// Insert <paramref name="control"/> at <paramref name="slot"/>, shifting sibling controls on the slot axis.
    /// Caller must ensure the control is not already in <paramref name="layout"/>.Controls.
    /// </summary>
    public static bool InsertControl(MidiDeviceLayout layout, MidiLayoutControl control, MidiDropSlot slot)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(control);

        control.RegionId = slot.ParentRegionId;
        if (slot.Axis == MidiLayoutShiftAxis.Horizontal)
        {
            ShiftControlCols(layout, slot.ParentRegionId, fromCol: slot.Col);
        }
        else
        {
            ShiftControlRows(layout, slot.ParentRegionId, fromRow: slot.Row);
        }

        control.Row = Math.Max(0, slot.Row);
        control.Col = Math.Max(0, slot.Col);
        layout.Controls.Add(control);
        SyncRootGridExtent(layout);
        return true;
    }

    /// <summary>
    /// Insert <paramref name="region"/> at <paramref name="slot"/>, shifting sibling regions on the slot axis.
    /// Caller must ensure the region is not already in <paramref name="layout"/>.Regions.
    /// </summary>
    public static bool InsertRegion(MidiDeviceLayout layout, MidiLayoutRegion region, MidiDropSlot slot)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(region);

        if (!string.IsNullOrWhiteSpace(slot.ParentRegionId)
            && string.Equals(region.Id, slot.ParentRegionId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(slot.ParentRegionId)
            && IsDescendantRegion(layout, ancestorId: region.Id, candidateId: slot.ParentRegionId!))
        {
            return false;
        }

        region.ParentRegionId = slot.ParentRegionId;
        if (slot.Axis == MidiLayoutShiftAxis.Horizontal)
        {
            ShiftSiblings(layout, slot.ParentRegionId, fromCol: slot.Col, isRegion: true);
        }
        else
        {
            ShiftSiblingRows(layout, slot.ParentRegionId, fromRow: slot.Row, isRegion: true);
        }

        region.Row = Math.Max(0, slot.Row);
        region.Col = Math.Max(0, slot.Col);
        layout.Regions.Add(region);
        SyncRootGridExtent(layout);
        return true;
    }

    public static bool MoveControlToSlot(MidiDeviceLayout layout, string controlId, MidiDropSlot slot)
    {
        var control = layout.Controls.FirstOrDefault(c =>
            string.Equals(c.Id, controlId, StringComparison.OrdinalIgnoreCase));
        if (control is null)
        {
            return false;
        }

        // No-op if already exactly there (same parent + cell).
        if (SameParent(control.RegionId, slot.ParentRegionId)
            && control.Row == slot.Row
            && control.Col == slot.Col)
        {
            return false;
        }

        layout.Controls.Remove(control);
        if (InsertControl(layout, control, slot))
        {
            return true;
        }

        layout.Controls.Add(control);
        return false;
    }

    public static bool MoveRegionToSlot(MidiDeviceLayout layout, string regionId, MidiDropSlot slot)
    {
        var region = layout.Regions.FirstOrDefault(r =>
            string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));
        if (region is null)
        {
            return false;
        }

        if (SameParent(region.ParentRegionId, slot.ParentRegionId)
            && region.Row == slot.Row
            && region.Col == slot.Col)
        {
            return false;
        }

        layout.Regions.Remove(region);
        if (InsertRegion(layout, region, slot))
        {
            return true;
        }

        layout.Regions.Add(region);
        return false;
    }

    /// <summary>Build an insert slot beside an existing control (Left/Right/Top/Bottom).</summary>
    public static MidiDropSlot SlotBesideControl(
        MidiLayoutControl target,
        MidiLayoutDropZone zone,
        int rowSpan = 1,
        int colSpan = 1)
    {
        rowSpan = Math.Max(1, rowSpan);
        colSpan = Math.Max(1, colSpan);
        return zone switch
        {
            MidiLayoutDropZone.Left => new MidiDropSlot(
                target.RegionId, target.Row, Math.Max(0, target.Col), MidiLayoutShiftAxis.Horizontal, rowSpan, colSpan),
            MidiLayoutDropZone.Right => new MidiDropSlot(
                target.RegionId, target.Row, target.Col + Math.Max(1, target.ColSpan), MidiLayoutShiftAxis.Horizontal, rowSpan, colSpan),
            MidiLayoutDropZone.Top => new MidiDropSlot(
                target.RegionId, Math.Max(0, target.Row), target.Col, MidiLayoutShiftAxis.Vertical, rowSpan, colSpan),
            MidiLayoutDropZone.Bottom => new MidiDropSlot(
                target.RegionId, target.Row + Math.Max(1, target.RowSpan), target.Col, MidiLayoutShiftAxis.Vertical, rowSpan, colSpan),
            _ => new MidiDropSlot(
                target.RegionId, target.Row, target.Col + Math.Max(1, target.ColSpan), MidiLayoutShiftAxis.Horizontal, rowSpan, colSpan)
        };
    }

    /// <summary>Build an insert slot beside an existing region among its siblings.</summary>
    public static MidiDropSlot SlotBesideRegion(
        MidiLayoutRegion target,
        MidiLayoutDropZone zone,
        int rowSpan = 1,
        int colSpan = 1)
    {
        rowSpan = Math.Max(1, rowSpan);
        colSpan = Math.Max(1, colSpan);
        return zone switch
        {
            MidiLayoutDropZone.Left => new MidiDropSlot(
                target.ParentRegionId, target.Row, Math.Max(0, target.Col), MidiLayoutShiftAxis.Horizontal, rowSpan, colSpan),
            MidiLayoutDropZone.Right => new MidiDropSlot(
                target.ParentRegionId, target.Row, target.Col + Math.Max(1, target.ColSpan), MidiLayoutShiftAxis.Horizontal, rowSpan, colSpan),
            MidiLayoutDropZone.Top => new MidiDropSlot(
                target.ParentRegionId, Math.Max(0, target.Row), target.Col, MidiLayoutShiftAxis.Vertical, rowSpan, colSpan),
            MidiLayoutDropZone.Bottom => new MidiDropSlot(
                target.ParentRegionId, target.Row + Math.Max(1, target.RowSpan), target.Col, MidiLayoutShiftAxis.Vertical, rowSpan, colSpan),
            _ => new MidiDropSlot(
                target.ParentRegionId, target.Row, target.Col + Math.Max(1, target.ColSpan), MidiLayoutShiftAxis.Horizontal, rowSpan, colSpan)
        };
    }

    private static void PlaceControlBeside(
        MidiDeviceLayout layout,
        MidiLayoutControl control,
        MidiLayoutControl target,
        MidiLayoutDropZone zone)
    {
        var slot = SlotBesideControl(target, zone, control.RowSpan, control.ColSpan);
        // InsertControl adds to Controls — PlaceNewControl also adds. Use shift+assign only.
        control.RegionId = slot.ParentRegionId;
        if (slot.Axis == MidiLayoutShiftAxis.Horizontal)
        {
            ShiftControlCols(layout, slot.ParentRegionId, fromCol: slot.Col);
        }
        else
        {
            ShiftControlRows(layout, slot.ParentRegionId, fromRow: slot.Row);
        }

        control.Row = slot.Row;
        control.Col = slot.Col;
    }

    private static void PlaceBeside(
        MidiDeviceLayout layout,
        MidiLayoutRegion region,
        MidiLayoutRegion target,
        MidiLayoutDropZone zone)
    {
        var slot = SlotBesideRegion(target, zone, region.RowSpan, region.ColSpan);
        region.ParentRegionId = slot.ParentRegionId;
        if (slot.Axis == MidiLayoutShiftAxis.Horizontal)
        {
            ShiftSiblings(layout, slot.ParentRegionId, fromCol: slot.Col, isRegion: true);
        }
        else
        {
            ShiftSiblingRows(layout, slot.ParentRegionId, fromRow: slot.Row, isRegion: true);
        }

        region.Row = slot.Row;
        region.Col = slot.Col;
    }

    /// <summary>
    /// Resolve an insert slot from a pointer over sibling cells (edges + gaps between).
    /// <paramref name="children"/> are in parent-local coordinates.
    /// When <paramref name="allowEmptyFreeCell"/> is false, returns false if the pointer
    /// is not on a child edge/body and not in a gap (so callers can treat that as Inside-nest).
    /// </summary>
    public static bool TryResolveInsertSlot(
        IReadOnlyList<MidiDropHitChild> children,
        double x,
        double y,
        string? parentRegionId,
        int rowSpan,
        int colSpan,
        string? excludeId,
        out MidiDropSlot slot,
        bool allowEmptyFreeCell = false)
    {
        slot = default;
        rowSpan = Math.Max(1, rowSpan);
        colSpan = Math.Max(1, colSpan);

        var usable = children
            .Where(c => excludeId is null
                        || !string.Equals(c.Id, excludeId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (usable.Count == 0)
        {
            if (!allowEmptyFreeCell)
            {
                return false;
            }

            slot = new MidiDropSlot(parentRegionId, 0, 0, MidiLayoutShiftAxis.Horizontal, rowSpan, colSpan);
            return true;
        }

        // 1) Pointer inside a child → edge band relative to that child.
        foreach (var child in usable)
        {
            if (!child.Bounds.Contains(x, y))
            {
                continue;
            }

            var zone = ResolveDropZoneBesideOnly(
                x - child.Bounds.X,
                y - child.Bounds.Y,
                child.Bounds.Width,
                child.Bounds.Height);
            slot = SlotFromHitChild(child, parentRegionId, zone, rowSpan, colSpan);
            return true;
        }

        // 2) Gap between two children.
        MidiDropHitChild? bestGap = null;
        var bestGapDist = double.MaxValue;
        var bestIsHorizontal = true;

        for (var i = 0; i < usable.Count; i++)
        {
            for (var j = i + 1; j < usable.Count; j++)
            {
                var a = usable[i];
                var b = usable[j];

                var left = a.Bounds.Right <= b.Bounds.Left ? a : b.Bounds.Right <= a.Bounds.Left ? b : null;
                var right = left is null ? null : ReferenceEquals(left, a) ? b : a;
                if (left is not null && right is not null
                    && RangesOverlap(left.Bounds.Top, left.Bounds.Bottom, right.Bounds.Top, right.Bounds.Bottom)
                    && x >= left.Bounds.Right && x <= right.Bounds.Left
                    && y >= Math.Max(left.Bounds.Top, right.Bounds.Top)
                    && y <= Math.Min(left.Bounds.Bottom, right.Bounds.Bottom))
                {
                    var mid = (left.Bounds.Right + right.Bounds.Left) / 2;
                    var dist = Math.Abs(x - mid);
                    if (dist < bestGapDist)
                    {
                        bestGapDist = dist;
                        bestGap = right;
                        bestIsHorizontal = true;
                    }
                }

                var top = a.Bounds.Bottom <= b.Bounds.Top ? a : b.Bounds.Bottom <= a.Bounds.Top ? b : null;
                var bottom = top is null ? null : ReferenceEquals(top, a) ? b : a;
                if (top is not null && bottom is not null
                    && RangesOverlap(top.Bounds.Left, top.Bounds.Right, bottom.Bounds.Left, bottom.Bounds.Right)
                    && y >= top.Bounds.Bottom && y <= bottom.Bounds.Top
                    && x >= Math.Max(top.Bounds.Left, bottom.Bounds.Left)
                    && x <= Math.Min(top.Bounds.Right, bottom.Bounds.Right))
                {
                    var mid = (top.Bounds.Bottom + bottom.Bounds.Top) / 2;
                    var dist = Math.Abs(y - mid);
                    if (dist < bestGapDist)
                    {
                        bestGapDist = dist;
                        bestGap = bottom;
                        bestIsHorizontal = false;
                    }
                }
            }
        }

        if (bestGap is not null)
        {
            slot = bestIsHorizontal
                ? new MidiDropSlot(
                    parentRegionId, bestGap.Row, bestGap.Col, MidiLayoutShiftAxis.Horizontal, rowSpan, colSpan)
                : new MidiDropSlot(
                    parentRegionId, bestGap.Row, bestGap.Col, MidiLayoutShiftAxis.Vertical, rowSpan, colSpan);
            return true;
        }

        if (!allowEmptyFreeCell)
        {
            return false;
        }

        // Empty body fallback — first free cell (only when caller opts in).
        var occupied = usable.Select(c => (c.Row, c.Col)).ToHashSet();
        for (var row = 0; row < 32; row++)
        {
            for (var col = 0; col < 24; col++)
            {
                if (occupied.Contains((row, col)))
                {
                    continue;
                }

                slot = new MidiDropSlot(parentRegionId, row, col, MidiLayoutShiftAxis.Horizontal, rowSpan, colSpan);
                return true;
            }
        }

        slot = new MidiDropSlot(parentRegionId, 0, 0, MidiLayoutShiftAxis.Horizontal, rowSpan, colSpan);
        return true;
    }

    private static MidiDropSlot SlotFromHitChild(
        MidiDropHitChild child,
        string? parentRegionId,
        MidiLayoutDropZone zone,
        int rowSpan,
        int colSpan) =>
        zone switch
        {
            MidiLayoutDropZone.Left => new MidiDropSlot(
                parentRegionId, child.Row, Math.Max(0, child.Col), MidiLayoutShiftAxis.Horizontal, rowSpan, colSpan),
            MidiLayoutDropZone.Right => new MidiDropSlot(
                parentRegionId, child.Row, child.Col + Math.Max(1, child.ColSpan), MidiLayoutShiftAxis.Horizontal, rowSpan, colSpan),
            MidiLayoutDropZone.Top => new MidiDropSlot(
                parentRegionId, Math.Max(0, child.Row), child.Col, MidiLayoutShiftAxis.Vertical, rowSpan, colSpan),
            MidiLayoutDropZone.Bottom => new MidiDropSlot(
                parentRegionId, child.Row + Math.Max(1, child.RowSpan), child.Col, MidiLayoutShiftAxis.Vertical, rowSpan, colSpan),
            _ => new MidiDropSlot(
                parentRegionId, child.Row, child.Col + Math.Max(1, child.ColSpan), MidiLayoutShiftAxis.Horizontal, rowSpan, colSpan)
        };

    private static bool RangesOverlap(double a0, double a1, double b0, double b1) =>
        a0 < b1 && b0 < a1;

    private static void PlaceBesideRegionAsControl(
        MidiDeviceLayout layout,
        MidiLayoutControl control,
        MidiLayoutRegion target,
        MidiLayoutDropZone zone)
    {
        control.Row = target.Row;
        control.Col = target.Col;
        switch (zone)
        {
            case MidiLayoutDropZone.Left:
                control.Col = Math.Max(0, target.Col - 1);
                break;
            case MidiLayoutDropZone.Right:
                control.Col = target.Col + target.ColSpan;
                break;
            case MidiLayoutDropZone.Top:
                control.Row = Math.Max(0, target.Row - 1);
                break;
            case MidiLayoutDropZone.Bottom:
                control.Row = target.Row + target.RowSpan;
                break;
        }
    }

    private static void ShiftSiblings(MidiDeviceLayout layout, string? parentId, int fromCol, bool isRegion)
    {
        foreach (var r in layout.Regions.Where(r => SameParent(r.ParentRegionId, parentId) && r.Col >= fromCol))
        {
            r.Col++;
        }
    }

    private static void ShiftSiblingRows(MidiDeviceLayout layout, string? parentId, int fromRow, bool isRegion)
    {
        foreach (var r in layout.Regions.Where(r => SameParent(r.ParentRegionId, parentId) && r.Row >= fromRow))
        {
            r.Row++;
        }
    }

    private static void ShiftControlCols(MidiDeviceLayout layout, string? regionId, int fromCol)
    {
        foreach (var c in layout.Controls.Where(c => SameParent(c.RegionId, regionId) && c.Col >= fromCol))
        {
            c.Col++;
        }
    }

    private static void ShiftControlRows(MidiDeviceLayout layout, string? regionId, int fromRow)
    {
        foreach (var c in layout.Controls.Where(c => SameParent(c.RegionId, regionId) && c.Row >= fromRow))
        {
            c.Row++;
        }
    }

    private static (int Row, int Col) FindFreeCellAmongSiblings(
        MidiDeviceLayout layout,
        string? parentId,
        bool isRegion)
    {
        var occupied = new HashSet<(int, int)>();
        if (isRegion)
        {
            foreach (var r in layout.Regions.Where(r => SameParent(r.ParentRegionId, parentId)))
            {
                occupied.Add((r.Row, r.Col));
            }
        }
        else
        {
            foreach (var c in layout.Controls.Where(c => SameParent(c.RegionId, parentId)))
            {
                occupied.Add((c.Row, c.Col));
            }
        }

        for (var row = 0; row < 32; row++)
        {
            for (var col = 0; col < 24; col++)
            {
                if (occupied.Add((row, col)))
                {
                    return (row, col);
                }
            }
        }

        return (0, 0);
    }

    private static bool IsDescendantRegion(MidiDeviceLayout layout, string ancestorId, string candidateId)
    {
        var current = candidateId;
        var guard = 0;
        while (!string.IsNullOrWhiteSpace(current) && guard++ < 64)
        {
            if (string.Equals(current, ancestorId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            current = layout.Regions.FirstOrDefault(r =>
                string.Equals(r.Id, current, StringComparison.OrdinalIgnoreCase))?.ParentRegionId;
        }

        return false;
    }

    private static HashSet<string> CollectDescendantRegionIds(MidiDeviceLayout layout, string rootId)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var child in layout.Regions.Where(r =>
                         string.Equals(r.ParentRegionId, id, StringComparison.OrdinalIgnoreCase)))
            {
                if (set.Add(child.Id))
                {
                    queue.Enqueue(child.Id);
                }
            }
        }

        return set;
    }

    private static string GenerateUniqueRegionId(MidiDeviceLayout layout)
    {
        var existing = layout.Regions.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < 1000; i++)
        {
            var id = $"area_{i}";
            if (!existing.Contains(id))
            {
                return id;
            }
        }

        return $"area_{Guid.NewGuid():N}"[..12];
    }

    private static bool SameParent(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right)
        || string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
