using HyprWin.Core.Interop;

namespace HyprWin.Core;

/// <summary>
/// HWND-free BSP and master/stack tree math. Operates on <see cref="BspNode"/> / <see cref="Workspace"/>
/// using integer window ids (IntPtr) and rectangles — no Win32 window-station calls.
/// </summary>
public static class TilingLayout
{
    /// <summary>
    /// Insert <paramref name="hwnd"/> into the workspace tree. Existing split ratios are left unchanged.
    /// </summary>
    public static void Insert(Workspace workspace, IntPtr hwnd)
    {
        if (workspace.LayoutRoot == null)
        {
            workspace.LayoutRoot = BspNode.Leaf(hwnd);
            return;
        }

        var focusedHwnd = workspace.FocusedWindow?.Handle ?? IntPtr.Zero;
        BspNode? targetLeaf = focusedHwnd != IntPtr.Zero
            ? workspace.LayoutRoot.FindLeaf(focusedHwnd)
            : null;

        if (targetLeaf == null)
        {
            long bestArea = -1;
            foreach (var leaf in workspace.LayoutRoot.GetLeaves())
            {
                long area = (long)leaf.ComputedRect.Width * leaf.ComputedRect.Height;
                if (area > bestArea)
                {
                    bestArea = area;
                    targetLeaf = leaf;
                }
            }
        }

        if (targetLeaf == null)
        {
            workspace.LayoutRoot = BspNode.Leaf(hwnd);
            return;
        }

        BspNode.SplitDirection direction;
        var cr = targetLeaf.ComputedRect;
        if (cr.Width > 0 && cr.Height > 0)
            direction = cr.Width >= cr.Height
                ? BspNode.SplitDirection.Horizontal
                : BspNode.SplitDirection.Vertical;
        else
        {
            int depth = GetNodeDepth(targetLeaf);
            direction = depth % 2 == 0
                ? BspNode.SplitDirection.Horizontal
                : BspNode.SplitDirection.Vertical;
        }

        var existingLeaf = BspNode.Leaf(targetLeaf.WindowHandle);
        var newLeaf = BspNode.Leaf(hwnd);
        var splitNode = BspNode.Split(direction, existingLeaf, newLeaf);

        if (targetLeaf.Parent == null)
        {
            workspace.LayoutRoot = splitNode;
        }
        else
        {
            var parent = targetLeaf.Parent;
            if (parent.First == targetLeaf)
                parent.First = splitNode;
            else
                parent.Second = splitNode;
            splitNode.Parent = parent;
        }
    }

    /// <summary>
    /// Remove a leaf and promote its sibling so the parent split is not left as a ghost node.
    /// </summary>
    public static void Close(Workspace workspace, IntPtr hwnd)
    {
        if (workspace.LayoutRoot == null) return;

        var leaf = workspace.LayoutRoot.FindLeaf(hwnd);
        if (leaf == null) return;

        if (leaf.Parent == null)
        {
            workspace.LayoutRoot = null;
            return;
        }

        var parent = leaf.Parent;
        var sibling = parent.First == leaf ? parent.Second : parent.First;
        if (sibling == null) return;

        if (parent.Parent == null)
        {
            workspace.LayoutRoot = sibling;
            sibling.Parent = null;
        }
        else
        {
            var grandparent = parent.Parent;
            if (grandparent.First == parent)
                grandparent.First = sibling;
            else
                grandparent.Second = sibling;
            sibling.Parent = grandparent;
        }
    }

    /// <summary>
    /// Exchange the window ids of two leaves. Tree structure and ratios are unchanged.
    /// </summary>
    public static void Swap(Workspace workspace, IntPtr hwnd1, IntPtr hwnd2)
    {
        if (workspace.LayoutRoot == null) return;

        var leaf1 = workspace.LayoutRoot.FindLeaf(hwnd1);
        var leaf2 = workspace.LayoutRoot.FindLeaf(hwnd2);
        if (leaf1 != null && leaf2 != null)
            (leaf1.WindowHandle, leaf2.WindowHandle) = (leaf2.WindowHandle, leaf1.WindowHandle);
    }

    /// <summary>
    /// Toggle dwindle (BSP) vs master/stack. Returns the new mode.
    /// </summary>
    public static string ToggleLayout(Workspace workspace)
    {
        workspace.LayoutMode = workspace.LayoutMode == "master" ? "dwindle" : "master";
        return workspace.LayoutMode;
    }

    /// <summary>
    /// Move a leaf from <paramref name="source"/> onto <paramref name="scratchpad"/> without
    /// destroying the window id. Source sibling is promoted; dest tree receives a new leaf.
    /// </summary>
    public static void MoveToScratchpad(Workspace source, Workspace scratchpad, IntPtr hwnd)
    {
        Close(source, hwnd);
        Insert(scratchpad, hwnd);
    }

    /// <summary>
    /// Recursively compute BSP rectangles. Split ratios on existing nodes are honored.
    /// </summary>
    public static void CalculateBspLayout(BspNode node, NativeMethods.RECT area, int gapsInner)
    {
        node.ComputedRect = area;
        if (!node.IsSplit) return;
        if (node.First == null || node.Second == null) return;

        int halfGap = gapsInner / 2;

        if (node.Direction == BspNode.SplitDirection.Horizontal)
        {
            int splitX = area.Left + (int)(area.Width * node.Ratio);
            var firstRect = new NativeMethods.RECT(area.Left, area.Top, splitX - halfGap, area.Bottom);
            var secondRect = new NativeMethods.RECT(splitX + halfGap, area.Top, area.Right, area.Bottom);
            CalculateBspLayout(node.First, firstRect, gapsInner);
            CalculateBspLayout(node.Second, secondRect, gapsInner);
        }
        else
        {
            int splitY = area.Top + (int)(area.Height * node.Ratio);
            var firstRect = new NativeMethods.RECT(area.Left, area.Top, area.Right, splitY - halfGap);
            var secondRect = new NativeMethods.RECT(area.Left, splitY + halfGap, area.Right, area.Bottom);
            CalculateBspLayout(node.First, firstRect, gapsInner);
            CalculateBspLayout(node.Second, secondRect, gapsInner);
        }
    }

    /// <summary>
    /// Master on the left using <see cref="Workspace.MasterRatio"/>; remaining leaves stacked on the right.
    /// </summary>
    public static void CalculateMasterLayout(Workspace workspace, NativeMethods.RECT area, int gapsInner)
    {
        if (workspace.LayoutRoot == null) return;
        var leaves = workspace.LayoutRoot.GetLeaves().ToList();
        if (leaves.Count == 0) return;

        if (leaves.Count == 1)
        {
            leaves[0].ComputedRect = area;
            return;
        }

        double ratio = Math.Clamp(workspace.MasterRatio, 0.2, 0.8);

        int masterWidth = (int)((area.Width - gapsInner) * ratio);
        int stackWidth = area.Width - masterWidth - gapsInner;

        leaves[0].ComputedRect = new NativeMethods.RECT(area.Left, area.Top, area.Left + masterWidth, area.Bottom);

        int stackCount = leaves.Count - 1;
        int totalStackGaps = (stackCount - 1) * gapsInner;
        int availableStackHeight = Math.Max(0, area.Height - totalStackGaps);
        int slotHeight = availableStackHeight / stackCount;

        int currentY = area.Top;
        int stackX = area.Left + masterWidth + gapsInner;

        for (int i = 1; i < leaves.Count; i++)
        {
            int h = (i == leaves.Count - 1) ? (area.Bottom - currentY) : slotHeight;
            leaves[i].ComputedRect = new NativeMethods.RECT(stackX, currentY, stackX + stackWidth, currentY + h);
            currentY += h + gapsInner;
        }
    }

    private static int GetNodeDepth(BspNode node)
    {
        int depth = 0;
        var current = node;
        while (current.Parent != null)
        {
            depth++;
            current = current.Parent;
        }
        return depth;
    }
}
