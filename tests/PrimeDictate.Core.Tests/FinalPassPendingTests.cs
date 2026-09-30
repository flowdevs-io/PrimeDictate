using PrimeDictate.Platforms.Nemotron;

namespace PrimeDictate.Core.Tests;

public sealed class FinalPassPendingTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pd-fpp-" + Guid.NewGuid().ToString("N"), "media");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(this.dir)!, true);

    [Fact]
    public void Mark_survives_until_cleared_and_clearing_twice_is_fine()
    {
        Assert.False(FinalPassPending.IsPending(this.dir));
        FinalPassPending.Mark(this.dir);
        Assert.True(FinalPassPending.IsPending(this.dir));
        FinalPassPending.Clear(this.dir);
        Assert.False(FinalPassPending.IsPending(this.dir));
        FinalPassPending.Clear(this.dir);
    }
}
