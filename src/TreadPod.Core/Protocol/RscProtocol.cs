using System.Globalization;

namespace TreadPod.Core.Protocol;

public static class RscProtocol
{
    public static readonly Guid ServiceUuid = new("00001814-0000-1000-8000-00805f9b34fb");
    public static readonly Guid MeasurementUuid = new("00002a53-0000-1000-8000-00805f9b34fb");
    public static readonly Guid FeatureUuid = new("00002a54-0000-1000-8000-00805f9b34fb");

    private static readonly byte[] FeatureStaticBytes = [0x00, 0x00];
    public static ReadOnlyMemory<byte> FeatureBytes => FeatureStaticBytes;

    // COROS PACE 4 doubles RSC cadence (treats it as strides/min) and wraps values >= 128.
    public static int ToRscCadence(int stepsPerMinute)
    {
        var half = (int)Math.Round(stepsPerMinute / 2.0, MidpointRounding.ToEven);
        return Math.Clamp(half, 0, 127);
    }

    public static byte[] EncodeMeasurement(double speedKmh, int stepsPerMinute)
    {
        var rawSpeed = (ushort)Math.Clamp(
            Math.Round(Math.Max(0.0, speedKmh) / 3.6 * 256.0, MidpointRounding.ToEven),
            0,
            ushort.MaxValue);

        return
        [
            0x04, // flags: running
            (byte)(rawSpeed & 0xFF),
            (byte)((rawSpeed >> 8) & 0xFF),
            (byte)ToRscCadence(stepsPerMinute)
        ];
    }

    public static string FormatPace(double speedKmh)
    {
        if (speedKmh < 2.5)
        {
            return "—";
        }

        var secPerKm = (int)Math.Round(3600.0 / speedKmh, MidpointRounding.AwayFromZero);
        var min = secPerKm / 60;
        var sec = secPerKm % 60;
        return string.Create(CultureInfo.InvariantCulture, $"{min}:{sec:00} /км");
    }

    public static string FormatHex(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        return string.Create(bytes.Length * 3 - 1, bytes.ToArray(), static (span, data) =>
        {
            const string hex = "0123456789abcdef";
            var pos = 0;
            for (var i = 0; i < data.Length; i++)
            {
                if (i > 0)
                {
                    span[pos++] = ' ';
                }

                span[pos++] = hex[data[i] >> 4];
                span[pos++] = hex[data[i] & 0x0F];
            }
        });
    }
}
