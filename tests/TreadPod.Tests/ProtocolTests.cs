using TreadPod.Core.Protocol;
using Xunit;

namespace TreadPod.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void R2TryParseStatus_ParsesValidSamplePacket()
    {
        var pkt = Convert.FromHexString("f8a2015001000064000032000096500000000afd");

        var ok = R2Protocol.TryParseStatus(pkt, out var status);

        Assert.True(ok);
        Assert.Equal((byte)1, status.BeltState);
        Assert.True(status.IsRunning);
        Assert.Equal(8.0, status.SpeedKmh, 1);
        Assert.Equal(100, status.TimeSeconds);
        Assert.Equal(50, status.DistanceDecameters);
        Assert.Equal(0.50, status.DistanceKm, 2);
        Assert.Equal(150, status.Steps);
        Assert.Equal(8.0, status.TargetSpeedKmh, 1);
    }

    [Theory]
    [InlineData("f8a20150")] // too short
    [InlineData("a8a2015001000064000032000096500000000afd")] // bad header
    [InlineData("f8a2015001000064000032000096500000000aff")] // bad footer
    public void R2TryParseStatus_RejectsInvalidPackets(string hex)
    {
        var pkt = Convert.FromHexString(hex);
        Assert.False(R2Protocol.TryParseStatus(pkt, out _));
    }

    [Fact]
    public void RscEncodeMeasurement_MatchesVerifiedBytes()
    {
        // 8 km/h = 569 (0x0239), 170 spm -> rsc 85 (0x55)
        Assert.Equal(new byte[] { 0x04, 0x39, 0x02, 0x55 }, RscProtocol.EncodeMeasurement(8.0, 170));
        // 10 km/h = 711 (0x02C7), 170 spm -> rsc 85 (0x55)
        Assert.Equal(new byte[] { 0x04, 0xC7, 0x02, 0x55 }, RscProtocol.EncodeMeasurement(10.0, 170));
    }

    [Fact]
    public void RscToRscCadence_HalvesAndCapsAt127()
    {
        Assert.Equal(95, RscProtocol.ToRscCadence(190));
        Assert.Equal(127, RscProtocol.ToRscCadence(300));
        Assert.Equal(0, RscProtocol.ToRscCadence(0));
    }

    [Theory]
    [InlineData(0.0, "—")]
    [InlineData(2.4, "—")]
    [InlineData(2.5, "24:00 /km")]
    [InlineData(7.5, "8:00 /km")]
    [InlineData(10.0, "6:00 /km")]
    public void RscFormatPace_RespectsPace4Threshold(double speedKmh, string expected)
    {
        Assert.Equal(expected, RscProtocol.FormatPace(speedKmh));
        if (speedKmh >= 2.5)
        {
            Assert.Equal(expected.Replace("/km", "/км"), RscProtocol.FormatPace(speedKmh, "/км"));
        }
    }

    [Fact]
    public void CadenceCalculator_HandlesWindowGapAndReset()
    {
        var calc = new CadenceCalculator(windowSeconds: 10.0);

        Assert.Equal(0, calc.Update(0, 0));
        Assert.Equal(120, calc.Update(5, 10));
        Assert.Equal(120, calc.Update(10, 20));
        // 15 s gap: falls back to the last point before the gap
        Assert.Equal(80, calc.Update(25, 40));
        // Counter reset (new R2 session) -> 0, not negative
        Assert.Equal(0, calc.Update(26, 30));
    }
}
