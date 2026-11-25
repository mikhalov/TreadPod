namespace TreadPod.Core.Protocol;

public sealed record R2Status(
    byte BeltState,
    double SpeedKmh,
    int TimeSeconds,
    int DistanceDecameters,
    int Steps,
    double TargetSpeedKmh)
{
    public bool IsRunning => BeltState == 1;
    public double DistanceKm => DistanceDecameters / 100.0;
}

public static class R2Protocol
{
    public static readonly Guid ServiceUuid = new("0000fe00-0000-1000-8000-00805f9b34fb");
    public static readonly Guid NotifyUuid = new("0000fe01-0000-1000-8000-00805f9b34fb");
    public static readonly Guid WriteUuid = new("0000fe02-0000-1000-8000-00805f9b34fb");

    // Strictly read-only status poll (noOp / ask_stats). Never send start, stop, or speed control commands.
    private static readonly byte[] AskStatsBytes = [0xF7, 0xA2, 0x00, 0x00, 0xA2, 0xFD];
    public static ReadOnlyMemory<byte> AskStatsCommand => AskStatsBytes;

    public static bool IsKnownBeltState(byte state) =>
        state is 0 or 1 or 3 or 6 or 7 or 8 or 9;

    public static bool TryParseStatus(ReadOnlySpan<byte> packet, out R2Status status)
    {
        if (packet.Length != 20 || packet[0] != 0xF8 || packet[1] != 0xA2 || packet[19] != 0xFD)
        {
            status = default!;
            return false;
        }

        status = new R2Status(
            BeltState: packet[2],
            SpeedKmh: packet[3] / 10.0,
            TimeSeconds: ReadUInt24BigEndian(packet.Slice(5, 3)),
            DistanceDecameters: ReadUInt24BigEndian(packet.Slice(8, 3)),
            Steps: ReadUInt24BigEndian(packet.Slice(11, 3)),
            TargetSpeedKmh: packet[14] / 10.0);
        return true;
    }

    private static int ReadUInt24BigEndian(ReadOnlySpan<byte> bytes) =>
        (bytes[0] << 16) | (bytes[1] << 8) | bytes[2];
}
