using PrimeDictate.Core.Dictation;
using PrimeDictate.Platforms.Input;

namespace PrimeDictate.Core.Tests;

/// <summary>
/// The WPF delivery decision order: guard, then (focus moved) direct insertion into the original target's edit
/// control, else restore and type, then Enter only after a successful injection.
/// </summary>
public sealed class TextDeliveryParityTests
{
    [Fact]
    public void Still_foreground_types_and_never_tries_direct_insertion()
    {
        var target = new Target { Foreground = true, CanInject = true };
        var injector = new Injector();

        var r = TranscriptDelivery.Deliver("hi", target, new Guard(), injector, new DictationOptions { ReturnToStartTarget = true });

        Assert.Equal(DictationDeliveryStatus.Injected, r.Status);
        Assert.Equal(0, target.InjectCalls);
        Assert.Equal(["hi"], injector.Typed);
    }

    [Fact]
    public void Focus_moved_with_return_enabled_inserts_directly_without_restoring_or_typing()
    {
        var target = new Target { Foreground = false, CanInject = true, CanRestore = true };
        var injector = new Injector();

        var r = TranscriptDelivery.Deliver("hi there", target, new Guard(), injector, new DictationOptions { ReturnToStartTarget = true });

        Assert.Equal(DictationDeliveryStatus.Injected, r.Status);
        Assert.False(r.EnterSent);
        Assert.Equal(["hi there"], target.Injected);
        Assert.Equal(0, target.RestoreCalls);
        Assert.Empty(injector.Typed);
        Assert.Equal(0, injector.Enters);
    }

    [Fact]
    public void Focus_moved_without_return_enabled_skips_and_touches_nothing()
    {
        var target = new Target { Foreground = false, CanInject = true, CanRestore = true };
        var injector = new Injector();

        var r = TranscriptDelivery.Deliver("hi", target, new Guard(), injector, new DictationOptions());

        Assert.Equal(DictationDeliveryStatus.SkippedFocusChanged, r.Status);
        Assert.Equal(0, target.InjectCalls);
        Assert.Equal(0, target.RestoreCalls);
        Assert.Empty(injector.Typed);
    }

    [Fact]
    public void Focus_moved_when_direct_insertion_fails_restores_then_types()
    {
        var target = new Target { Foreground = false, CanInject = false, CanRestore = true };
        var injector = new Injector();

        var r = TranscriptDelivery.Deliver("hi", target, new Guard(), injector, new DictationOptions { ReturnToStartTarget = true });

        Assert.Equal(DictationDeliveryStatus.Injected, r.Status);
        Assert.Equal(1, target.InjectCalls);
        Assert.Equal(1, target.RestoreCalls);
        Assert.Equal(["hi"], injector.Typed);
    }

    [Fact]
    public void Focus_moved_in_coding_mode_skips_direct_insertion_restores_types_and_sends_enter()
    {
        var target = new Target { Foreground = false, CanInject = true, CanRestore = true };
        var injector = new Injector();

        var r = TranscriptDelivery.Deliver(
            "ls", target, new Guard(), injector, new DictationOptions { ReturnToStartTarget = true, SendEnterAfterCommit = true });

        Assert.Equal(DictationDeliveryStatus.Injected, r.Status);
        Assert.True(r.EnterSent);
        Assert.Equal(0, target.InjectCalls);
        Assert.Equal(1, target.RestoreCalls);
        Assert.Equal(["ls"], injector.Typed);
        Assert.Equal(1, injector.Enters);
    }

    [Fact]
    public void Focus_moved_and_nothing_works_reports_skipped_without_typing_or_enter()
    {
        var target = new Target { Foreground = false, CanInject = false, CanRestore = false };
        var injector = new Injector();

        var r = TranscriptDelivery.Deliver("hi", target, new Guard(), injector, new DictationOptions { ReturnToStartTarget = true });

        Assert.Equal(DictationDeliveryStatus.SkippedFocusChanged, r.Status);
        Assert.Empty(injector.Typed);
        Assert.Equal(0, injector.Enters);
    }

    [Fact]
    public void Enter_is_not_sent_when_typing_fails()
    {
        var injector = new Injector { Fail = true };

        var r = TranscriptDelivery.Deliver("ls", new Target { Foreground = true }, new Guard(), injector, new DictationOptions { SendEnterAfterCommit = true });

        Assert.Equal(DictationDeliveryStatus.FailedToInject, r.Status);
        Assert.False(r.EnterSent);
        Assert.Equal(0, injector.Enters);
    }

    [Fact]
    public void Enter_waits_for_the_target_to_take_the_text_in_then_is_sent()
    {
        var injector = new Injector { EnterDelay = TimeSpan.FromMilliseconds(80) };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var r = TranscriptDelivery.Deliver("send it", new Target { Foreground = true }, new Guard(), injector, new DictationOptions { SendEnterAfterCommit = true });

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(70));
        Assert.Equal(DictationDeliveryStatus.Injected, r.Status);
        Assert.True(r.EnterSent);
        Assert.Equal(1, injector.Enters);
    }

    [Fact]
    public void Enter_is_not_sent_when_another_window_came_to_the_front_after_typing()
    {
        // In front for the check before typing, then the user switched windows.
        var target = new Target { Foreground = true, ForegroundChecksBeforeSwitch = 1 };
        var injector = new Injector { EnterDelay = TimeSpan.FromMilliseconds(10) };

        var r = TranscriptDelivery.Deliver("send it", target, new Guard(), injector, new DictationOptions { SendEnterAfterCommit = true });

        Assert.Equal(DictationDeliveryStatus.Injected, r.Status);
        Assert.False(r.EnterSent);
        Assert.Contains("Enter was not sent", r.Error);
        Assert.Equal(["send it"], injector.Typed);
        Assert.Equal(0, injector.Enters);
    }

    [Fact]
    public void Enter_delay_grows_with_the_typed_text_and_is_capped()
    {
        Assert.Equal(TimeSpan.Zero, EnterTiming.AfterKeystrokes(0));
        Assert.Equal(TimeSpan.FromMilliseconds(270), EnterTiming.AfterKeystrokes(100));
        Assert.True(EnterTiming.AfterKeystrokes(555) > EnterTiming.AfterKeystrokes(100));
        Assert.Equal(EnterTiming.Max, EnterTiming.AfterKeystrokes(100_000));
    }

    [Fact]
    public void Windows_entry_prefers_the_focused_control_and_does_not_type_keys()
    {
        var keys = new List<string>();

        var route = WindowsTextEntry.Enter("hello", _ => true, keys.Add);

        Assert.Equal(WindowsTextEntry.FocusedControlRoute, route);
        Assert.Empty(keys);
    }

    [Fact]
    public void Windows_entry_falls_back_to_keys_when_the_focused_control_declines()
    {
        var keys = new List<string>();

        var route = WindowsTextEntry.Enter("hello", _ => false, keys.Add);

        Assert.Equal(WindowsTextEntry.KeyboardRoute, route);
        Assert.Equal(["hello"], keys);
    }

    [Fact]
    public void Windows_entry_ignores_empty_text()
    {
        var keys = new List<string>();

        WindowsTextEntry.Enter("", _ => throw new InvalidOperationException(), keys.Add);

        Assert.Empty(keys);
    }

    private sealed class Target : IForegroundTarget
    {
        private int foregroundChecks;

        public bool Foreground { get; set; } = true;

        /// <summary>When set, another window comes to the front after this many foreground checks.</summary>
        public int? ForegroundChecksBeforeSwitch { get; init; }

        public bool CanRestore { get; init; }

        public bool CanInject { get; init; }

        public int RestoreCalls { get; private set; }

        public int InjectCalls { get; private set; }

        public List<string> Injected { get; } = [];

        public string DisplayName => "Editor";

        public string? AppName => "editor";

        public string? WindowTitle => "notes.txt";

        public bool IsStillForeground()
        {
            if (this.ForegroundChecksBeforeSwitch is { } checks && ++this.foregroundChecks > checks)
            {
                this.Foreground = false;
            }

            return this.Foreground;
        }

        public bool TryRestore()
        {
            this.RestoreCalls++;
            if (this.CanRestore)
            {
                // A restored window is the one in front, as WindowsForegroundGuard checks before reporting success.
                this.Foreground = true;
            }

            return this.CanRestore;
        }

        public bool TryInjectDirectly(string text)
        {
            this.InjectCalls++;
            if (this.CanInject)
            {
                this.Injected.Add(text);
            }

            return this.CanInject;
        }
    }

    private sealed class Guard : IForegroundTargetGuard
    {
        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public IForegroundTarget? Capture() => new Target();
    }

    private sealed class Injector : ITextInjector
    {
        public List<string> Typed { get; } = [];

        public int Enters { get; private set; }

        public bool Fail { get; init; }

        public TimeSpan EnterDelay { get; init; }

        public void TypeText(string text)
        {
            if (this.Fail)
            {
                throw new InvalidOperationException("boom");
            }

            this.Typed.Add(text);
        }

        public void SendEnter() => this.Enters++;
    }
}
