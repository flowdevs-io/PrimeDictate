using PrimeDictate.Core.Diagnostics;
using PrimeDictate.Core.Providers;

namespace PrimeDictate.Core.Dictation;

/// <summary>
/// Dictation and the wake word record from the microphone chosen in Settings, or from the default one when that is not
/// connected (as the WPF app does), saying so once instead of not listening at all. A choice that no longer exists is
/// common: the WPF settings keep a Windows device id after that device is gone.
/// </summary>
public sealed class DefaultMicrophoneFallback(IAudioSource inner, Action<string> notice) : IAudioSource
{
    private int reported;

    public ValueTask<IReadOnlyList<AudioInputDevice>> ListDevicesAsync(CancellationToken cancellationToken) =>
        inner.ListDevicesAsync(cancellationToken);

    public ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, CancellationToken cancellationToken) =>
        this.OpenAsync(deviceId, MicAccessMode.Shared, cancellationToken);

    public async ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, MicAccessMode access, CancellationToken cancellationToken)
    {
        if (deviceId is null)
        {
            return await inner.OpenAsync(null, access, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var lease = await inner.OpenAsync(deviceId, access, cancellationToken).ConfigureAwait(false);
            // It is back, so losing it again is worth saying again.
            Volatile.Write(ref this.reported, 0);
            return lease;
        }
        catch (AudioSourceException ex) when (ex.Kind == AudioSourceErrorKind.DeviceRemoved)
        {
            var lease = await inner.OpenAsync(null, access, cancellationToken).ConfigureAwait(false);
            if (Interlocked.Exchange(ref this.reported, 1) == 0)
            {
                AppLog.Event("microphone", "The microphone chosen in Settings is not connected; dictation uses the default microphone.", ActivityLevel.Warning);
                notice("The microphone chosen in Settings is not connected, so the default microphone is used. Pick one in Settings to change that.");
            }

            return lease;
        }
    }
}
