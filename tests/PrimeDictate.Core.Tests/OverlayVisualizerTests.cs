using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Core.Tests;

public sealed class OverlayVisualizerTests
{
    [Fact]
    public void Silence_keeps_bars_at_the_floor_and_particles_inside_the_field()
    {
        var v = new OverlayVisualizer(new Random(1));
        v.Resize(300, 120);
        for (var t = 0; t < 200; t++)
        {
            v.Tick(t / 30.0);
        }

        Assert.All(v.Bars, b => Assert.InRange(b, 3.0, 3.0 + 1e-6));
        Assert.All(v.Particles, p =>
        {
            Assert.InRange(p.X, -25, 325);
            Assert.InRange(p.Y, -25, 145);
            Assert.InRange(p.Brightness, 0, 1);
        });
    }

    [Fact]
    public void Loudness_raises_the_center_bars_and_scrolls_outward()
    {
        var v = new OverlayVisualizer(new Random(2));
        v.Resize(300, 120);
        v.SetLevel(0.5);
        v.Tick(0);
        var first = v.Bars[0];
        Assert.True(first > 20);
        for (var t = 1; t < 10; t++)
        {
            v.Tick(t / 30.0);
        }

        Assert.True(v.Bars[5] > 3.0, $"bar5={v.Bars[5]}");
        Assert.True(v.Bars[0] >= v.Bars[80]);
    }
}
