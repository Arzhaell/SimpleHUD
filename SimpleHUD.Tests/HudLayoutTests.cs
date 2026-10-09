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

    private static readonly Vector2 Screen = new(2560, 1440);
    private static readonly Vector2 Label = new(120, 20);

    [Fact]
    public void PutsTheLabelJustAboveTheFrameCorner()
    {
        var label = HudLayout.LabelMin(new Vector2(964, 1227), new Vector2(542, 54), Label, Vector2.Zero, Screen);

        Assert.Equal(new Vector2(964, 1207), label);
    }

    [Fact]
    public void PutsTheLabelBelowAFrameAtTheTopOfTheScreen()
    {
        var label = HudLayout.LabelMin(new Vector2(731, 1), new Vector2(125, 29), Label, Vector2.Zero, Screen);

        Assert.Equal(new Vector2(731, 30), label);
    }

    [Fact]
    public void KeepsTheLabelOnScreen()
    {
        // Cadre qui dépasse à gauche, puis tout à droite.
        Assert.Equal(new Vector2(0, 180), HudLayout.LabelMin(new Vector2(-30, 200), new Vector2(100, 50), Label, Vector2.Zero, Screen));
        Assert.Equal(new Vector2(2440, 1380), HudLayout.LabelMin(new Vector2(2500, 1400), new Vector2(60, 40), Label, Vector2.Zero, Screen));
    }

    [Fact]
    public void FollowsTheGameWindowOnTheDesktop()
    {
        // Fenêtre du jeu décalée sur le bureau : le haut de l'écran est celui de la fenêtre.
        var origin = new Vector2(100, 50);

        var label = HudLayout.LabelMin(origin, new Vector2(200, 40), Label, origin, new Vector2(1920, 1080));

        Assert.Equal(new Vector2(100, 90), label);
    }

    [Fact]
    public void FindsTheFrameUnderTheMouse()
    {
        HudFrame[] frames =
        [
            new("Équipe", 21, 738, 380, 420, false),
            new("Barre de raccourcis 1", 964, 1227, 542, 54, false),
        ];

        Assert.Equal(1, HudLayout.FrameAt(frames, new Vector2(1000, 1250)));
        Assert.Equal(-1, HudLayout.FrameAt(frames, new Vector2(700, 300)));

        // Le bord droit et le bas ne font plus partie du cadre.
        Assert.Equal(-1, HudLayout.FrameAt(frames, new Vector2(1506, 1250)));
    }

    [Fact]
    public void PrefersTheSmallestOfOverlappingFrames()
    {
        // Liste d'alliance posée sur la liste d'équipe : la souris dessus vise la petite.
        HudFrame[] frames =
        [
            new("Équipe", 21, 738, 380, 420, false),
            new("Liste d'alliance 1", 30, 760, 194, 52, false),
        ];

        Assert.Equal(1, HudLayout.FrameAt(frames, new Vector2(100, 780)));
    }

    [Fact]
    public void PutsThePanelUnderTheSelectedFrame()
    {
        var panel = HudLayout.PanelMin(new Vector2(964, 1227), new Vector2(542, 54), new Vector2(300, 90), Vector2.Zero, Screen, 6);

        Assert.Equal(new Vector2(964, 1287), panel);
    }

    [Fact]
    public void PutsThePanelAboveAFrameAtTheBottomOfTheScreen()
    {
        // Menu principal tout en bas : pas la place dessous.
        var panel = HudLayout.PanelMin(new Vector2(1699, 1409), new Vector2(171, 27), new Vector2(300, 90), Vector2.Zero, Screen, 6);

        Assert.Equal(new Vector2(1699, 1313), panel);
    }

    [Fact]
    public void KeepsThePanelOnScreen()
    {
        // Infos du serveur, collées au bord droit : le panneau recule pour rester entier.
        var panel = HudLayout.PanelMin(new Vector2(2310, -1), new Vector2(248, 28), new Vector2(300, 90), Vector2.Zero, Screen, 6);

        Assert.Equal(new Vector2(2260, 33), panel);
    }

    [Fact]
    public void MovesByWholePixels()
    {
        Assert.Equal(new Vector2(36, -10), HudLayout.MoveDelta(new Vector2(964, 1227), new Vector2(1000, 1217)));

        // Cadre posé entre deux pixels par le jeu : le déplacement le ramène sur un pixel entier.
        Assert.Equal(new Vector2(1, 0), HudLayout.MoveDelta(new Vector2(963.6f, 1227), new Vector2(965, 1227)));
        Assert.Equal(Vector2.Zero, HudLayout.MoveDelta(new Vector2(964.2f, 1226.8f), new Vector2(964, 1227)));
    }

    [Fact]
    public void LetsAFrameGoPastTheEdge()
    {
        // Comme le jeu (infos du serveur à Y -1), un cadre peut dépasser du bord.
        var target = HudLayout.ClampFrame(new Vector2(2310, -1), new Vector2(248, 28), Screen);

        Assert.Equal(new Vector2(2310, -1), target);
    }

    [Fact]
    public void KeepsAPieceOfTheFrameOnScreen()
    {
        var size = new Vector2(542, 54);

        Assert.Equal(new Vector2(-541, -53), HudLayout.ClampFrame(new Vector2(-5000, -5000), size, Screen));
        Assert.Equal(new Vector2(2559, 1439), HudLayout.ClampFrame(new Vector2(9000, 9000), size, Screen));
    }

    [Fact]
    public void KeepsOneFramePerElement()
    {
        // Le cadre de la sélection est dessiné par-dessus celui de l'élément : un seul reste, marqué sélectionné.
        var frames = HudLayout.Merge(
        [
            new HudFrame("Barre de raccourcis 1", 964, 1227, 542, 54, false),
            new HudFrame("Équipe", 21, 716, 380, 408, false),
            new HudFrame("Barre de raccourcis 1", 964, 1227, 542, 54, true),
        ]);

        Assert.Equal(2, frames.Length);
        Assert.Equal(new HudFrame("Barre de raccourcis 1", 964, 1227, 542, 54, true), frames[0]);
        Assert.Equal("Équipe", frames[1].Name);
    }

    [Fact]
    public void KeepsTwoElementsWithTheSameName()
    {
        // Le jeu a deux « Liste des objectifs » : deux cadres à des places différentes restent séparés.
        var frames = HudLayout.Merge(
        [
            new HudFrame("Liste des objectifs", 0, 300, 400, 120, false),
            new HudFrame("Liste des objectifs", 0, 46, 400, 120, false),
        ]);

        Assert.Equal([300, 46], frames.Select(frame => frame.Y));
    }
}
