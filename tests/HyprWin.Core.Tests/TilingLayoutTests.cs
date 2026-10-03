using HyprWin.Core.Interop;
using Xunit;

namespace HyprWin.Core.Tests;

public class TilingLayoutTests
{
    private static IntPtr Id(int n) => (IntPtr)n;

    private static Workspace NewWorkspace() => new()
    {
        Id = 0,
        MonitorIndex = 0,
        MasterRatio = 0.55,
        LayoutMode = "dwindle",
    };

    [Fact]
    public void Insert_preserves_existing_split_ratios()
    {
        var ws = NewWorkspace();
        TilingLayout.Insert(ws, Id(1));
        TilingLayout.Insert(ws, Id(2));

        Assert.NotNull(ws.LayoutRoot);
        Assert.True(ws.LayoutRoot!.IsSplit);
        ws.LayoutRoot.Ratio = 0.7;

        TilingLayout.Insert(ws, Id(3));

        Assert.NotNull(ws.LayoutRoot);
        Assert.True(ws.LayoutRoot!.IsSplit);
        Assert.Equal(0.7, ws.LayoutRoot.Ratio, 3);
        Assert.Equal(3, ws.LayoutRoot.LeafCount());
        Assert.NotNull(ws.LayoutRoot.FindLeaf(Id(1)));
        Assert.NotNull(ws.LayoutRoot.FindLeaf(Id(2)));
        Assert.NotNull(ws.LayoutRoot.FindLeaf(Id(3)));
    }

    [Fact]
    public void Close_promotes_sibling_and_leaves_no_ghost_node()
    {
        var ws = NewWorkspace();
        TilingLayout.Insert(ws, Id(1));
        TilingLayout.Insert(ws, Id(2));
        TilingLayout.Insert(ws, Id(3));

        TilingLayout.Close(ws, Id(2));

        Assert.NotNull(ws.LayoutRoot);
        Assert.Null(ws.LayoutRoot!.FindLeaf(Id(2)));
        Assert.NotNull(ws.LayoutRoot.FindLeaf(Id(1)));
        Assert.NotNull(ws.LayoutRoot.FindLeaf(Id(3)));
        Assert.Equal(2, ws.LayoutRoot.LeafCount());
        Assert.Equal(0, CountGhostSplits(ws.LayoutRoot));
    }

    [Fact]
    public void Swap_exchanges_two_leaves()
    {
        var ws = NewWorkspace();
        TilingLayout.Insert(ws, Id(1));
        TilingLayout.Insert(ws, Id(2));

        var before = ws.LayoutRoot!.GetLeaves().Select(l => l.WindowHandle).ToList();
        TilingLayout.Swap(ws, Id(1), Id(2));
        var after = ws.LayoutRoot!.GetLeaves().Select(l => l.WindowHandle).ToList();

        Assert.Equal(2, after.Count);
        Assert.Equal(before[1], after[0]);
        Assert.Equal(before[0], after[1]);
        Assert.NotNull(ws.LayoutRoot.FindLeaf(Id(1)));
        Assert.NotNull(ws.LayoutRoot.FindLeaf(Id(2)));
    }

    [Fact]
    public void Master_stack_respects_master_ratio()
    {
        var ws = NewWorkspace();
        ws.MasterRatio = 0.6;
        ws.LayoutMode = "master";
        TilingLayout.Insert(ws, Id(1));
        TilingLayout.Insert(ws, Id(2));
        TilingLayout.Insert(ws, Id(3));

        var area = new NativeMethods.RECT(0, 0, 1000, 1000);
        TilingLayout.CalculateMasterLayout(ws, area, gapsInner: 0);

        var leaves = ws.LayoutRoot!.GetLeaves().ToList();
        Assert.Equal(3, leaves.Count);
        Assert.Equal(600, leaves[0].ComputedRect.Width);
        Assert.Equal(400, leaves[1].ComputedRect.Width);
        Assert.Equal(400, leaves[2].ComputedRect.Width);
        Assert.Equal(area.Left, leaves[0].ComputedRect.Left);
        Assert.Equal(leaves[0].ComputedRect.Right, leaves[1].ComputedRect.Left);
    }

    [Fact]
    public void Scratchpad_move_does_not_delete_the_node()
    {
        var source = NewWorkspace();
        var scratch = new Workspace { Id = -1, MonitorIndex = 0, IsSpecial = true };

        TilingLayout.Insert(source, Id(1));
        TilingLayout.Insert(source, Id(2));

        TilingLayout.MoveToScratchpad(source, scratch, Id(1));

        Assert.Null(source.LayoutRoot!.FindLeaf(Id(1)));
        Assert.NotNull(source.LayoutRoot.FindLeaf(Id(2)));
        Assert.Equal(1, source.LayoutRoot.LeafCount());
        Assert.Equal(0, CountGhostSplits(source.LayoutRoot));

        Assert.NotNull(scratch.LayoutRoot);
        Assert.NotNull(scratch.LayoutRoot!.FindLeaf(Id(1)));
        Assert.Equal(1, scratch.LayoutRoot.LeafCount());
        Assert.Null(scratch.LayoutRoot.FindLeaf(Id(2)));
    }

    [Fact]
    public void Toggle_layout_switches_dwindle_and_master()
    {
        var ws = NewWorkspace();
        Assert.Equal("dwindle", ws.LayoutMode);
        Assert.Equal("master", TilingLayout.ToggleLayout(ws));
        Assert.Equal("dwindle", TilingLayout.ToggleLayout(ws));
    }

    private static int CountGhostSplits(BspNode node)
    {
        if (!node.IsSplit)
            return node.WindowHandle == IntPtr.Zero ? 1 : 0;

        int ghosts = 0;
        if (node.First == null || node.Second == null)
            ghosts++;
        if (node.First != null) ghosts += CountGhostSplits(node.First);
        if (node.Second != null) ghosts += CountGhostSplits(node.Second);
        return ghosts;
    }
}
