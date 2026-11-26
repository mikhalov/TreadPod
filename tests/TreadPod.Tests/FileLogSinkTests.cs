using TreadPod.App.Platform;
using Xunit;

namespace TreadPod.Tests;

public sealed class FileLogSinkTests
{
    [Fact]
    public void WriteAndDispose_CreatesRollingLogFileWithEntries()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"TreadPod.LogTest.{Guid.NewGuid():N}");
        try
        {
            using (var sink = new FileLogSink(tempDir))
            {
                sink.WriteSystem("started {test}");
                sink.Write("22:35:10 RSC 0x2A53 notify 04 15 02 51 (7.5 km/h, 162 spm)");
            }

            var files = Directory.GetFiles(tempDir, "treadpod-*.log");
            Assert.Single(files);

            var content = File.ReadAllText(files[0]);
            Assert.Contains("started {test}", content);
            Assert.Contains("22:35:10 RSC 0x2A53 notify 04 15 02 51 (7.5 km/h, 162 spm)", content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
