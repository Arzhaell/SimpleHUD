using System.Numerics;

namespace FlyingTextModifier.Tests;

public class HudLayoutTests
{
    [Fact]
    public void FindsTheElementInTheCurrentLayout()
    {
        // 4 dispositions de 112 éléments, l'une après l'autre.
        Assert.Equal(5, HudLayout.EntryIndex(0, 5, 112, 448));
        Assert.Equal(117, HudLayout.EntryIndex(1, 5, 112, 448));
        Assert.Equal(447, HudLayout.EntryIndex(3, 111, 112, 448));
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 112)]
    [InlineData(0, -1)]
    public void RefusesAnElementOutsideTheLayouts(int layout, int element)
    {
        Assert.Equal(-1, HudLayout.EntryIndex(layout, element, 112, 448));
    }

    [Fact]
    public void RefusesAnEmptyLayout()
    {
        Assert.Equal(-1, HudLayout.EntryIndex(0, 0, 0, 0));
    }

    [Fact]
    public void WritesThePositionInPixels()
    {
        Assert.Equal("X 1280  ·  Y -4", HudLayout.Coordinates(1280, -4));
    }

    [Fact]
    public void PutsTheLabelInTheElementCorner()
    {
        var label = HudLayout.LabelMin(new Vector2(300, 200), new Vector2(120, 20), Vector2.Zero, new Vector2(2560, 1440));

        Assert.Equal(new Vector2(300, 200), label);
    }

    [Fact]
    public void KeepsTheLabelOnScreen()
    {
        var screen = new Vector2(2560, 1440);
        var size = new Vector2(120, 20);

        // Élément qui dépasse en haut à gauche, puis en bas à droite.
        Assert.Equal(Vector2.Zero, HudLayout.LabelMin(new Vector2(-30, -10), size, Vector2.Zero, screen));
        Assert.Equal(new Vector2(2440, 1420), HudLayout.LabelMin(new Vector2(2500, 1430), size, Vector2.Zero, screen));
    }

    [Fact]
    public void FollowsTheGameWindowOnTheDesktop()
    {
        // Fenêtre du jeu décalée sur le bureau : l'étiquette reste dans la fenêtre.
        var origin = new Vector2(100, 50);

        Assert.Equal(origin, HudLayout.LabelMin(new Vector2(0, 0), new Vector2(120, 20), origin, new Vector2(1920, 1080)));
    }
}
