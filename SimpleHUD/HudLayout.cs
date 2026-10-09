using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace FlyingTextModifier;

/// <summary>
/// Rangement des éléments de l'ATH dans les dispositions du jeu, et placement des étiquettes dans l'éditeur.
/// Chaque disposition (4 en tout) a le même nombre d'emplacements (112), dans un ordre propre au jeu (constaté :
/// ce n'est pas celui de la feuille « Hud »), les dispositions l'une après l'autre.
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

    /// <summary>Texte de l'étiquette d'un cadre : la position de son coin haut-gauche à l'écran, en pixels.</summary>
    public static string Coordinates(int x, int y) => $"X {x}  ·  Y {y}";

    /// <summary>
    /// Coin haut-gauche de l'étiquette d'un cadre : juste au-dessus de son coin haut-gauche, pour ne pas cacher le nom
    /// que le jeu écrit dans le cadre ; juste en dessous si le cadre touche le haut de l'écran. Toujours entière à l'écran.
    /// </summary>
    public static Vector2 LabelMin(Vector2 frameMin, Vector2 frameSize, Vector2 labelSize, Vector2 screenMin, Vector2 screenSize)
    {
        var label = new Vector2(frameMin.X, frameMin.Y - labelSize.Y);
        if (label.Y < screenMin.Y)
            label.Y = frameMin.Y + frameSize.Y;

        var max = Vector2.Max(screenMin, screenMin + screenSize - labelSize);
        return Vector2.Clamp(label, screenMin, max);
    }

    /// <summary>
    /// Cadre sous la souris (en pixels du jeu), ou -1. Quand des cadres se chevauchent, le plus petit : c'est le plus
    /// précis (un élément posé sur un plus grand).
    /// </summary>
    public static int FrameAt(IReadOnlyList<HudFrame> frames, Vector2 point)
    {
        var found = -1;
        var foundArea = float.MaxValue;
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (point.X < frame.X || point.Y < frame.Y || point.X >= frame.X + frame.Width || point.Y >= frame.Y + frame.Height)
                continue;

            var area = (float)frame.Width * frame.Height;
            if (area < foundArea)
            {
                found = i;
                foundArea = area;
            }
        }

        return found;
    }

    /// <summary>
    /// Coin haut-gauche du panneau de réglage de l'élément sélectionné : juste sous son cadre, ou juste au-dessus s'il
    /// n'y a pas la place en dessous. Toujours entier à l'écran.
    /// </summary>
    public static Vector2 PanelMin(Vector2 frameMin, Vector2 frameSize, Vector2 panelSize, Vector2 screenMin, Vector2 screenSize, float gap)
    {
        var panel = new Vector2(frameMin.X, frameMin.Y + frameSize.Y + gap);
        if (panel.Y + panelSize.Y > screenMin.Y + screenSize.Y)
            panel.Y = frameMin.Y - gap - panelSize.Y;

        var max = Vector2.Max(screenMin, screenMin + screenSize - panelSize);
        return Vector2.Clamp(panel, screenMin, max);
    }

    /// <summary>
    /// Place demandée pour le coin haut-gauche d'un cadre, gardée à l'écran : le cadre peut dépasser du bord (le jeu
    /// le permet, ex. infos du serveur à Y -1), mais il en reste toujours un morceau visible.
    /// </summary>
    public static Vector2 ClampFrame(Vector2 target, Vector2 frameSize, Vector2 screenSize)
    {
        var min = Vector2.One - Vector2.Max(frameSize, Vector2.One);
        var max = Vector2.Max(min, screenSize - Vector2.One);
        return Vector2.Clamp(target, min, max);
    }

    /// <summary>Déplacement en pixels entiers qui amène le coin d'un cadre de sa place actuelle à la place demandée.</summary>
    public static Vector2 MoveDelta(Vector2 current, Vector2 target) =>
        new(MathF.Round(target.X) - MathF.Round(current.X), MathF.Round(target.Y) - MathF.Round(current.Y));

    /// <summary>
    /// Un cadre par élément : le jeu peut dessiner deux fois le même (cadre de la sélection par-dessus celui de
    /// l'élément).
    /// </summary>
    public static HudFrame[] Merge(IEnumerable<HudFrame> frames) => frames
        .GroupBy(frame => (frame.Name, frame.X, frame.Y, frame.Width, frame.Height))
        .Select(same => same.First() with { Selected = same.Any(frame => frame.Selected) })
        .ToArray();
}
