using Avalonia.Media;

namespace PrimeDictate.Desktop;

/// <summary>Stable speaker colors. Chosen to stay distinguishable on light and dark themes and for common color-vision differences.</summary>
public static class SpeakerPalette
{
    private static readonly Color[] Colors =
    [
        Color.Parse("#2F7BD9"), Color.Parse("#E07B1F"), Color.Parse("#2E9E6B"), Color.Parse("#B455C9"),
        Color.Parse("#D64B5F"), Color.Parse("#8A7A1E"), Color.Parse("#2AA1B8"), Color.Parse("#7A7F89")
    ];

    public static Color For(int index) => Colors[Math.Abs(index) % Colors.Length];

    public static IBrush BrushFor(int index) => new SolidColorBrush(For(index));
}
