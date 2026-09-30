using System.IO.Pipes;

namespace PrimeDictate.Platforms.Startup;

/// <summary>
/// One running PrimeDictate per user. The first process owns a named mutex and listens on a named pipe for two commands,
/// <c>show</c> (bring the window forward) and <c>quit</c> (shut down gracefully); anything else is ignored. The pipe is
/// restricted to the current user. A later launch sends its command instead of starting a second copy, which would
/// otherwise register the global hotkey twice.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string ShowCommand = "show";
    public const string QuitCommand = "quit";

    private readonly string mutexName;
    private readonly string pipeName;
    private readonly CancellationTokenSource stop = new();
    private Mutex? mutex;
    private Task? listener;

    public SingleInstance(string? name = null)
    {
        var suffix = name ?? $"PrimeDictate.Desktop.{Environment.UserName}";
        this.mutexName = (OperatingSystem.IsWindows() ? @"Local\" : string.Empty) + suffix;
        this.pipeName = suffix + ".control";
    }

    /// <summary>True when this process is now the only instance. The mutex is held until the process exits.</summary>
    public bool TryBecomePrimary()
    {
        this.mutex = new Mutex(initiallyOwned: true, this.mutexName, out var createdNew);
        if (!createdNew)
        {
            this.mutex.Dispose();
            this.mutex = null;
        }

        return createdNew;
    }

    /// <summary>Runs <paramref name="onCommand"/> for each valid command until disposed. Call only on the primary instance.</summary>
    public void StartListening(Func<string, Task> onCommand) => this.listener = Task.Run(async () =>
    {
        while (!this.stop.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(this.pipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(this.stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                server?.Dispose();
                await Task.Delay(200).ConfigureAwait(false);
                continue;
            }

            // Read on another task and go straight back to listening, so a second client never meets a moment with no listener.
            _ = Task.Run(() => this.HandleAsync(server, onCommand));
        }
    });

    private async Task HandleAsync(NamedPipeServerStream server, Func<string, Task> onCommand)
    {
        try
        {
            await using (server.ConfigureAwait(false))
            {
                using var reader = new StreamReader(server);
                var line = (await reader.ReadLineAsync(this.stop.Token).ConfigureAwait(false))?.Trim().ToLowerInvariant();
                if (line is ShowCommand or QuitCommand)
                {
                    await onCommand(line).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // A client that vanished mid-command is ignored.
        }
    }

    /// <summary>Sends a command to the running instance. False when none answered in time.</summary>
    public async Task<bool> SendAsync(string command, TimeSpan timeout)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", this.pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            await client.ConnectAsync((int)timeout.TotalMilliseconds).ConfigureAwait(false);
            await using var writer = new StreamWriter(client) { AutoFlush = true };
            await writer.WriteLineAsync(command).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True when no instance owns the mutex (already gone, or gone within the timeout).</summary>
    public bool WaitForPrimaryToExit(TimeSpan timeout)
    {
        using var probe = new Mutex(initiallyOwned: false, this.mutexName);
        try
        {
            if (probe.WaitOne(timeout))
            {
                probe.ReleaseMutex();
            }
            else
            {
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // The owner ended without releasing it: it is gone.
            probe.ReleaseMutex();
        }

        return true;
    }

    public void Dispose()
    {
        this.stop.Cancel();
        try
        {
            this.listener?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        this.stop.Dispose();
    }
}
