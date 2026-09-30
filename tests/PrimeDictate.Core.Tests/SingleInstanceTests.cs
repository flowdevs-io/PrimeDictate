using PrimeDictate.Platforms.Startup;

namespace PrimeDictate.Core.Tests;

public sealed class SingleInstanceTests
{
    private static string Name() => "pd-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Second_instance_is_refused_and_can_send_commands_to_the_first()
    {
        var name = Name();
        using var first = new SingleInstance(name);
        Assert.True(first.TryBecomePrimary());
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.StartListening(c =>
        {
            received.TrySetResult(c);
            return Task.CompletedTask;
        });

        // The mutex is owned by this thread, so probe from another one as a second process would.
        var second = await Task.Run(async () =>
        {
            using var other = new SingleInstance(name);
            return (Primary: other.TryBecomePrimary(), Sent: await other.SendAsync(SingleInstance.QuitCommand, TimeSpan.FromSeconds(5)));
        });
        Assert.False(second.Primary);
        Assert.True(second.Sent);
        Assert.Equal("quit", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Unknown_commands_are_ignored()
    {
        var name = Name();
        using var first = new SingleInstance(name);
        Assert.True(first.TryBecomePrimary());
        var seen = new List<string>();
        first.StartListening(c =>
        {
            lock (seen)
            {
                seen.Add(c);
            }

            return Task.CompletedTask;
        });
        using var other = new SingleInstance(name);
        Assert.True(await other.SendAsync("rm -rf /", TimeSpan.FromSeconds(5)));
        Assert.True(await other.SendAsync("show", TimeSpan.FromSeconds(5)));
        for (var i = 0; i < 100; i++)
        {
            lock (seen)
            {
                if (seen.Count > 0)
                {
                    break;
                }
            }

            await Task.Delay(50);
        }

        lock (seen)
        {
            Assert.Equal(["show"], seen);
        }
    }

    [Fact]
    public async Task Sending_with_nothing_running_fails_quickly_and_waiting_succeeds()
    {
        using var none = new SingleInstance(Name());
        Assert.False(await none.SendAsync("quit", TimeSpan.FromMilliseconds(300)));
        Assert.True(none.WaitForPrimaryToExit(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Waiting_for_a_running_primary_times_out_then_succeeds_once_it_ends()
    {
        var name = Name();
        var owner = new SingleInstance(name);
        var release = new ManualResetEventSlim();
        var ready = new ManualResetEventSlim();
        // A dedicated thread: a mutex is only abandoned when its owning thread ends, which a pool thread never does.
        var ownerThread = new Thread(() =>
        {
            Assert.True(owner.TryBecomePrimary());
            ready.Set();
            release.Wait();
        });
        ownerThread.Start();
        ready.Wait();
        using var waiter = new SingleInstance(name);
        Assert.False(await Task.Run(() => waiter.WaitForPrimaryToExit(TimeSpan.FromMilliseconds(300))));
        release.Set();
        ownerThread.Join();
        Assert.True(await Task.Run(() => waiter.WaitForPrimaryToExit(TimeSpan.FromSeconds(3))));
        owner.Dispose();
    }
}
