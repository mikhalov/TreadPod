using Avalonia;
using TreadPod.App.Platform;
using Xunit;

namespace TreadPod.Tests;

public sealed class PopoverPositioningTests
{
    [Fact]
    public void DockedBottomRight_GrowsUpwardWithoutLeavingScreen()
    {
        var area = new PixelRect(0, 0, 1920, 1032); // 1080p minus 48px taskbar

        var compact = PopoverPositioning.ComputePosition(
            area, new PixelPoint(0, 0), widthPx: 348, heightPx: 320, marginPx: 12, anchorBottomRight: true);
        var expanded = PopoverPositioning.ComputePosition(
            area, compact, widthPx: 348, heightPx: 580, marginPx: 12, anchorBottomRight: true);

        Assert.Equal(1920 - 348 - 12, compact.X);
        Assert.Equal(1032 - 320 - 12, compact.Y);
        Assert.Equal(compact.X, expanded.X);
        Assert.Equal(1032 - 580 - 12, expanded.Y);
        Assert.Equal(1032 - 12, expanded.Y + 580);
    }

    [Fact]
    public void DraggedNearBottomEdge_ClampsUpwardWhenHeightExpands()
    {
        var area = new PixelRect(0, 0, 1920, 1032);
        var draggedPos = new PixelPoint(1100, 680);

        var expanded = PopoverPositioning.ComputePosition(
            area, draggedPos, widthPx: 348, heightPx: 560, marginPx: 12, anchorBottomRight: false);

        Assert.Equal(1100, expanded.X);
        Assert.Equal(1032 - 560 - 12, expanded.Y);
    }

    [Fact]
    public void SecondaryMonitorWithNegativeCoordinates_ClampsInsideMonitorWorkingArea()
    {
        var leftMonitor = new PixelRect(-1920, 40, 1920, 1000);

        var docked = PopoverPositioning.ComputePosition(
            leftMonitor, new PixelPoint(0, 0), widthPx: 348, heightPx: 420, marginPx: 12, anchorBottomRight: true);

        Assert.Equal(-348 - 12, docked.X);
        Assert.Equal(40 + 1000 - 420 - 12, docked.Y);

        var oversized = PopoverPositioning.ComputePosition(
            leftMonitor, docked, widthPx: 348, heightPx: 1100, marginPx: 12, anchorBottomRight: true);

        Assert.Equal(leftMonitor.Y + 12, oversized.Y);
    }
}
