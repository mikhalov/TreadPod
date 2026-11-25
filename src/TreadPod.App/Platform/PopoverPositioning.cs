using Avalonia;

namespace TreadPod.App.Platform;

public static class PopoverPositioning
{
    public static PixelPoint ComputePosition(
        PixelRect workingArea,
        PixelPoint currentPosition,
        int widthPx,
        int heightPx,
        int marginPx,
        bool anchorBottomRight)
    {
        var minX = workingArea.X + marginPx;
        var maxX = Math.Max(minX, workingArea.Right - widthPx - marginPx);
        var minY = workingArea.Y + marginPx;
        var maxY = Math.Max(minY, workingArea.Bottom - heightPx - marginPx);

        var targetX = anchorBottomRight ? maxX : currentPosition.X;
        var targetY = anchorBottomRight ? maxY : currentPosition.Y;

        return new PixelPoint(
            Math.Clamp(targetX, minX, maxX),
            Math.Clamp(targetY, minY, maxY));
    }
}
