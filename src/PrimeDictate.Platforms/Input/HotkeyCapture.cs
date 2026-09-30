using PrimeDictate.Core.Dictation;
using SharpHook;
using SharpHook.Data;

namespace PrimeDictate.Platforms.Input;

/// <summary>Records the next modifier+key chord with a short-lived hook, so the key names match what the hotkey source compares.</summary>
public static class HotkeyCapture
{
    public static async Task<HotkeyGesture?> CaptureAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<HotkeyGesture?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hook = new SimpleGlobalHook(GlobalHookType.Keyboard);
        hook.KeyPressed += (sender, e) =>
        {
            var mask = e.RawEvent.Mask;
            var gesture = new HotkeyGesture(e.Data.KeyCode.ToString(), mask.HasCtrl(), mask.HasShift(), mask.HasAlt());
            if (e.Data.KeyCode == KeyCode.VcEscape && !gesture.Ctrl && !gesture.Shift && !gesture.Alt)
            {
                completion.TrySetResult(null);
            }
            else if (gesture.IsValid(out _))
            {
                e.SuppressEvent = true;
                completion.TrySetResult(gesture);
            }
        };

        var run = hook.RunAsync();
        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, cancellationToken);
        await using var registration = linked.Token.Register(() => completion.TrySetResult(null));
        var result = await completion.Task.ConfigureAwait(false);
        hook.Dispose();
        try
        {
            await run.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The hook stopped because we disposed it.
        }

        return result;
    }
}
