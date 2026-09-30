namespace PrimeDictate.Core.Dictation;

/// <summary>
/// Dictation behaviour that can change between recordings. Normalization matches the WPF app:
/// silence auto-commit is off at zero and otherwise clamped to 1 to 30 seconds, input gain to 0.5 to 4.
/// </summary>
public sealed record DictationOptions
{
    public string? InputDeviceId { get; init; }

    public double InputGain { get; init; } = 1.0;

    /// <summary>Commit after this much silence once speech was heard. Zero means hotkey stop only.</summary>
    public TimeSpan AutoCommitSilence { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Coding mode: press Enter after a successful, guarded final injection.</summary>
    public bool SendEnterAfterCommit { get; init; }

    /// <summary>
    /// When the platform has no foreground guard, type anyway. Off by default: typing into an
    /// unverified target is the failure the guard exists to prevent.
    /// </summary>
    public bool TypeWithoutFocusGuard { get; init; }

    /// <summary>If focus moved, try to bring the window dictation started in back before typing.</summary>
    public bool ReturnToStartTarget { get; init; }

    public string? Language { get; init; }

    public IReadOnlyList<ReplacementRule> Replacements { get; init; } = [];

    public DictationOptions Normalized() => this with
    {
        InputDeviceId = string.IsNullOrWhiteSpace(this.InputDeviceId) ? null : this.InputDeviceId,
        InputGain = NormalizeGain(this.InputGain),
        AutoCommitSilence = NormalizeSilence(this.AutoCommitSilence),
        Replacements = this.Replacements ?? []
    };

    public static double NormalizeGain(double gain) =>
        double.IsNaN(gain) || double.IsInfinity(gain) ? 1.0 : Math.Clamp(gain, 0.5, 4.0);

    public static TimeSpan NormalizeSilence(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return delay < TimeSpan.FromSeconds(1)
            ? TimeSpan.FromSeconds(1)
            : delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay;
    }
}
