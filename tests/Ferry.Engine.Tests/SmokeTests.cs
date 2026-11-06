namespace Ferry.Engine.Tests;

public class SmokeTests
{
    [Fact]
    public void Smoke_AssemblyLoads()
    {
        Assert.NotNull(typeof(AssemblyMarker).Assembly);
    }
}
