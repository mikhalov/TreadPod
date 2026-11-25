using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using TreadPod.Core.Bridge;

namespace TreadPod.App.Platform;

public static class TrayIconBitmapFactory
{
    public static WindowIcon Create(BridgePhase phase)
    {
        const int size = 32;
        var pixels = new byte[size * size * 4]; // BGRA8888

        var (dotR, dotG, dotB) = phase switch
        {
            BridgePhase.Running => ((byte)16, (byte)185, (byte)129),         // Emerald
            BridgePhase.ScanningR2 or BridgePhase.ConnectedIdle => ((byte)245, (byte)158, (byte)11), // Amber
            BridgePhase.ReleasedForKsFit => ((byte)56, (byte)189, (byte)248), // Sky
            BridgePhase.BluetoothOff or BridgePhase.Error => ((byte)244, (byte)63, (byte)94), // Rose
            _ => ((byte)148, (byte)163, (byte)184)                            // Slate
        };

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var idx = (y * size + x) * 4;
                // Rounded squircle background (2px inset, radius 7)
                if (IsInsideRoundedRect(x, y, 2, 2, 28, 28, 7))
                {
                    pixels[idx + 0] = 30;  // B
                    pixels[idx + 1] = 24;  // G
                    pixels[idx + 2] = 20;  // R
                    pixels[idx + 3] = 255; // A
                }

                // Minimalist white "T" / pod glyph
                var isTopBar = x is >= 9 and <= 20 && y is >= 9 and <= 12;
                var isStem = x is >= 13 and <= 16 && y is >= 12 and <= 23;
                if (isTopBar || isStem)
                {
                    pixels[idx + 0] = 245;
                    pixels[idx + 1] = 245;
                    pixels[idx + 2] = 245;
                    pixels[idx + 3] = 255;
                }

                // Live status dot in bottom-right corner
                var dx = x - 24.5;
                var dy = y - 24.5;
                var distSq = dx * dx + dy * dy;
                if (distSq <= 25.0) // outer dark ring
                {
                    pixels[idx + 0] = 18;
                    pixels[idx + 1] = 16;
                    pixels[idx + 2] = 14;
                    pixels[idx + 3] = 255;
                }
                if (distSq <= 14.5) // inner colored dot
                {
                    pixels[idx + 0] = dotB;
                    pixels[idx + 1] = dotG;
                    pixels[idx + 2] = dotR;
                    pixels[idx + 3] = 255;
                }
            }
        }

        using var wb = new WriteableBitmap(
            new PixelSize(size, size),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using (var fb = wb.Lock())
        {
            Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
        }

        using var ms = new MemoryStream();
        wb.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }

    private static bool IsInsideRoundedRect(int x, int y, int left, int top, int width, int height, int radius)
    {
        var right = left + width - 1;
        var bottom = top + height - 1;
        if (x < left || x > right || y < top || y > bottom)
        {
            return false;
        }

        var cx = x < left + radius ? left + radius : (x > right - radius ? right - radius : x);
        var cy = y < top + radius ? top + radius : (y > bottom - radius ? bottom - radius : y);
        var dx = x - cx;
        var dy = y - cy;
        return dx * dx + dy * dy <= radius * radius;
    }
}
