using System.Runtime.CompilerServices;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Storage;
using PrimeDictate.Platforms.Audio;

namespace PrimeDictate.Core.Tests;

public sealed class MicrophoneFallbackTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-mic-" + Guid.NewGuid().ToString("N"));

    public MicrophoneFallbackTests() => Directory.CreateDirectory(this.root);

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    /// <summary>A microphone list: names it has open as themselves, null opens "default", anything else is not connected.</summary>
    private sealed class Devices(params string[] names) : IAudioSource
    {
        public HashSet<string> Connected { get; } = [.. names];

        public string? Broken { get; init; }

        public List<string?> Opened { get; } = [];

        public ValueTask<IReadOnlyList<AudioInputDevice>> ListDevicesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AudioInputDevice>>([.. this.Connected.Select(n => new AudioInputDevice(n, n, false))]);

        public ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, CancellationToken cancellationToken)
        {
            lock (this.Opened)
            {
                this.Opened.Add(deviceId);
            }

            if (deviceId is not null && deviceId == this.Broken)
            {
                throw new AudioSourceException(AudioSourceErrorKind.Unknown, "The microphone could not be opened.");
            }

            if (deviceId is not null && !this.Connected.Contains(deviceId))
            {
                throw new AudioSourceException(AudioSourceErrorKind.DeviceRemoved, "The selected microphone is not connected.");
            }

            return ValueTask.FromResult<IAudioCaptureLease>(new Lease(deviceId ?? "default"));
        }
    }

    private sealed class Lease(string device) : IAudioCaptureLease
    {
        public AudioFormat Format => AudioFormat.SpeechTimeline;

        public string DeviceId => device;

        public string DeviceName => device;

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            yield break;
        }

        public ValueTask PauseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private const string GoneMicrophone = "{0.0.1.00000000}.{60a9a03a-ffa2-49ff-b206-0a4dfb8632b8}";

    [Fact]
    public async Task A_chosen_microphone_that_is_gone_records_from_the_default_one_and_says_so_once()
    {
        var notices = new List<string>();
        var source = new DefaultMicrophoneFallback(new Devices("Microphone (Yeti Classic)"), notices.Add);

        await using var first = await source.OpenAsync(GoneMicrophone, default);
        await using var second = await source.OpenAsync(GoneMicrophone, default);

        Assert.Equal("default", first.DeviceId);
        Assert.Equal("default", second.DeviceId);
        Assert.Contains("default microphone", Assert.Single(notices));
    }

    [Fact]
    public async Task A_connected_choice_is_used_and_other_microphone_errors_are_not_hidden()
    {
        var notices = new List<string>();
        var source = new DefaultMicrophoneFallback(new Devices("Microphone (Yeti Classic)") { Broken = "Microphone (Arctis)" }, notices.Add);

        await using var yeti = await source.OpenAsync("Microphone (Yeti Classic)", default);
        await using var fallback = await source.OpenAsync(null, default);
        var broken = await Assert.ThrowsAsync<AudioSourceException>(async () => await source.OpenAsync("Microphone (Arctis)", default));

        Assert.Equal("Microphone (Yeti Classic)", yeti.DeviceId);
        Assert.Equal("default", fallback.DeviceId);
        Assert.Equal(AudioSourceErrorKind.Unknown, broken.Kind);
        Assert.Empty(notices);
    }

    [Fact]
    public async Task Losing_the_chosen_microphone_again_after_it_came_back_is_said_again()
    {
        var notices = new List<string>();
        var devices = new Devices();
        var source = new DefaultMicrophoneFallback(devices, notices.Add);

        await (await source.OpenAsync("Headset", default)).DisposeAsync();
        devices.Connected.Add("Headset");
        await using (var back = await source.OpenAsync("Headset", default))
        {
            Assert.Equal("Headset", back.DeviceId);
        }

        devices.Connected.Remove("Headset");
        await (await source.OpenAsync("Headset", default)).DisposeAsync();

        Assert.Equal(2, notices.Count);
    }

    [Fact]
    public void Windows_endpoint_ids_saved_by_the_WPF_app_are_told_apart_from_device_names()
    {
        Assert.True(WindowsAudioEndpoints.IsEndpointId(GoneMicrophone));
        Assert.True(WindowsAudioEndpoints.IsEndpointId("{0.0.0.00000000}.{8C01FF4F-7598-4041-9FEB-107609CEDBE9}"));
        Assert.False(WindowsAudioEndpoints.IsEndpointId("Microphone (Yeti Classic)"));
        Assert.False(WindowsAudioEndpoints.IsEndpointId("{0.0.1.00000000}"));
        Assert.False(WindowsAudioEndpoints.IsEndpointId(string.Empty));
        if (OperatingSystem.IsWindows())
        {
            // Windows no longer knows this id, as with Justin's saved microphone.
            Assert.Null(WindowsAudioEndpoints.FriendlyName("{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}"));
        }
    }

    [Fact]
    public async Task The_wake_word_listens_on_the_default_microphone_when_the_saved_one_is_gone()
    {
        File.WriteAllText(Path.Combine(this.root, "settings.json"), $$"""
            { "FirstRunCompleted": true, "EnableWakeWord": true, "WakeWordPhrase": "okay computer", "SelectedInputDeviceId": "{{GoneMicrophone}}" }
            """);
        // Wake listening only starts when a speech model is installed (checked once, as the WPF app did).
        var model = Path.Combine(this.root, "models", "whisper", "sherpa-onnx-whisper-tiny.en");
        Directory.CreateDirectory(model);
        foreach (var f in new[] { "tiny.en-decoder.int8.onnx", "tiny.en-encoder.int8.onnx", "tiny.en-tokens.txt" })
        {
            File.WriteAllText(Path.Combine(model, f), "x");
        }

        var devices = new Devices("Microphone (Yeti Classic)");
        await using var host = new PrimeDictate.Platforms.Dictation.DictationHost(new AppDataPaths(this.root), new PrimeDictate.Core.Coordination.MicrophoneCoordinator(), devices);
        var notice = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Notice += m => notice.TrySetResult(m);

        host.StartWakeWord();

        Assert.Contains("default microphone", await notice.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        lock (devices.Opened)
        {
            Assert.Equal([GoneMicrophone, null], devices.Opened);
        }
    }

    [Fact]
    public async Task A_selected_model_that_is_not_installed_is_reported_once_not_on_every_press()
    {
        File.WriteAllText(Path.Combine(this.root, "settings.json"), """
            { "FirstRunCompleted": true, "TranscriptionBackend": "WhisperNet", "SelectedModelId": "large-v3-turbo" }
            """);
        var notices = new List<string>();
        await using var host = new PrimeDictate.Platforms.Dictation.DictationHost(new AppDataPaths(this.root), new PrimeDictate.Core.Coordination.MicrophoneCoordinator(), new Devices());
        host.Notice += m =>
        {
            lock (notices)
            {
                notices.Add(m);
            }
        };

        for (var press = 0; press < 3; press++)
        {
            await host.Controller!.ToggleAsync();
        }

        lock (notices)
        {
            Assert.Single(notices, n => n.Contains("is not installed", StringComparison.Ordinal));
            Assert.Equal(3, notices.Count(n => n.Contains("No speech model", StringComparison.Ordinal)));
        }
    }
}
