using Xunit;

public sealed class MemoryTelemetryTests
{
    [Fact]
    public void ParsesLinuxRam()
    {
        var value = MemoryTelemetry.ParseMemInfo("MemTotal: 16384 kB\nMemAvailable: 8192 kB\n");
        Assert.Equal(16_777_216, value.Total);
        Assert.Equal(8_388_608, value.Available);
    }

    [Fact]
    public void ParsesNvidiaMiB()
    {
        var value = MemoryTelemetry.ParseNvidiaMemory("8192, 4096\n");
        Assert.Equal(8_589_934_592, value.Total);
        Assert.Equal(4_294_967_296, value.Free);
    }

    [Fact]
    public void GracefullyHandlesMissingGpu()
    {
        var result = MemoryTelemetry.ParseNvidiaMemory("NVIDIA device not found");
        Assert.Null(result.Total);
    }
}
