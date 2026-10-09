using System.Numerics;

namespace FlyingTextModifier;

/// <summary>
/// Rangement des éléments de l'ATH dans les dispositions du jeu. Chaque disposition (4 en tout) garde un emplacement
/// par élément de la feuille « Hud » du jeu (112 éléments), dans le même ordre, les dispositions l'une après l'autre.
/// Ce fichier ne contient que de la logique pure, testable sans le jeu.
/// </summary>
internal static class HudLayout
{
    /// <summary>Emplacement d'un élément dans le tableau de toutes les dispositions, ou -1 s'il n'existe pas.</summary>
    public static int EntryIndex(int layout, int element, int perLayout, int entryCount)
    {
        if (perLayout <= 0 || layout < 0 || element < 0 || element >= perLayout)
            return -1;

        var index = (layout * perLayout) + element;
        return index < entryCount ? index : -1;
    }

    /// <summary>Texte de l'étiquette d'un élément : sa position à l'écran, en pixels.</summary>
    public static string Coordinates(int x, int y) => $"X {x}  ·  Y {y}";

    /// <summary>
    /// Coin haut-gauche de l'étiquette d'un élément : dans son coin haut-gauche, mais toujours entière à l'écran
    /// (un élément peut dépasser du bord).
    /// </summary>
    public static Vector2 LabelMin(Vector2 elementMin, Vector2 labelSize, Vector2 screenMin, Vector2 screenSize)
    {
        var max = Vector2.Max(screenMin, screenMin + screenSize - labelSize);
        return Vector2.Clamp(elementMin, screenMin, max);
    }
}
