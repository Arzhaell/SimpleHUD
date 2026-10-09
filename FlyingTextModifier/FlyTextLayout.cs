using System;
using System.Buffers.Binary;
using System.Numerics;

namespace FlyingTextModifier;

/// <summary>Groupes de textes défilants affichés sur le personnage. Le jeu décide quel texte va dans quel groupe.</summary>
public enum FlyTextGroup
{
    /// <summary>Soins reçus, y compris les soins sur la durée.</summary>
    Healing = 0,

    /// <summary>Effets de statut gagnés ou perdus, et dégâts subis.</summary>
    StatusDamage = 1,
}

/// <summary>
/// Rangement en mémoire des groupes de textes défilants dans l'addon « _FlyText ».
/// Le jeu ne documente rien : la disposition (10 groupes de 0x30 octets, position X/Y à 0x10, priorité 9 → 0 à 0x26)
/// a été relevée par le plugin FlyTextFilter. Ce fichier ne contient que de la logique pure, testable sans le jeu.
/// </summary>
internal static class FlyTextLayout
{
    public const int GroupCount = 10;
    public const int GroupSize = 0x30;
    public const int PositionOffset = 0x10;
    public const int PriorityOffset = 0x26;

    // Au-delà, une « position » n'est pas une coordonnée d'écran : on n'est pas sur le bon tableau.
    private const float MaxCoordinate = 20000f;

    public static readonly FlyTextGroup[] Groups = [FlyTextGroup.Healing, FlyTextGroup.StatusDamage];

    /// <summary>Cherche le tableau des groupes dans la mémoire de l'addon, à partir de <paramref name="start"/>. Renvoie -1 s'il est introuvable.</summary>
    public static int FindGroupArray(ReadOnlySpan<byte> memory, int start)
    {
        for (var offset = Math.Max(start, 0); offset + (GroupCount * GroupSize) <= memory.Length; offset += 2)
        {
            if (IsGroupArray(memory, offset))
                return offset;
        }

        return -1;
    }

    /// <summary>Vérifie que le tableau des groupes se trouve bien à <paramref name="offset"/> avant d'y lire ou d'y écrire.</summary>
    public static bool IsGroupArray(ReadOnlySpan<byte> memory, int offset)
    {
        if (offset < 0 || offset + (GroupCount * GroupSize) > memory.Length)
            return false;

        // Chaque groupe porte sa priorité : 9 pour le premier, puis 8, 7… jusqu'à 0.
        for (var i = 0; i < GroupCount; i++)
        {
            var priority = BinaryPrimitives.ReadInt16LittleEndian(memory[(offset + (i * GroupSize) + PriorityOffset)..]);
            if (priority != GroupCount - 1 - i)
                return false;
        }

        foreach (var group in Groups)
        {
            var position = ReadPosition(memory, offset, group);
            if (!IsPlausible(position.X) || !IsPlausible(position.Y))
                return false;
        }

        return true;
    }

    /// <summary>Emplacement (depuis le début de l'addon) de la position X/Y d'un groupe.</summary>
    public static int PositionAddress(int arrayOffset, FlyTextGroup group) => arrayOffset + ((int)group * GroupSize) + PositionOffset;

    public static Vector2 ReadPosition(ReadOnlySpan<byte> memory, int arrayOffset, FlyTextGroup group)
    {
        var address = PositionAddress(arrayOffset, group);
        return new Vector2(
            BinaryPrimitives.ReadSingleLittleEndian(memory[address..]),
            BinaryPrimitives.ReadSingleLittleEndian(memory[(address + 4)..]));
    }

    /// <summary>Pixels → fraction de l'écran, bornée pour que le texte reste visible.</summary>
    public static Vector2 ToRatio(Vector2 pixels, Vector2 screen)
    {
        if (screen.X <= 0 || screen.Y <= 0)
            return Vector2.Zero;

        return Clamp(pixels / screen);
    }

    /// <summary>Fraction de l'écran → pixels.</summary>
    public static Vector2 ToPixels(Vector2 ratio, Vector2 screen) => Clamp(ratio) * screen;

    public static Vector2 Clamp(Vector2 ratio) => Vector2.Clamp(ratio, Vector2.Zero, Vector2.One);

    /// <summary>Un décalage peut être négatif, mais jamais plus grand que l'écran.</summary>
    public static Vector2 ClampOffset(Vector2 ratio) => Vector2.Clamp(ratio, -Vector2.One, Vector2.One);

    /// <summary>Décalage en fraction de l'écran → pixels.</summary>
    public static Vector2 OffsetToPixels(Vector2 ratio, Vector2 screen) => ClampOffset(ratio) * screen;

    /// <summary>Valeur affichée et saisie dans la fenêtre : pixels entiers.</summary>
    public static (int X, int Y) ToWholePixels(Vector2 ratio, Vector2 screen) =>
        ((int)MathF.Round(ratio.X * screen.X), (int)MathF.Round(ratio.Y * screen.Y));

    /// <summary>Glissé fin (Maj enfoncée) : le cadre avance quatre fois moins vite que la souris.</summary>
    public static Vector2 DragDelta(Vector2 mouseDelta, bool fine) => fine ? mouseDelta * 0.25f : mouseDelta;

    /// <summary>
    /// Coin haut-gauche du cadre affiché dans l'éditeur, à partir du point d'ancrage des textes.
    /// Par défaut le jeu met les soins juste à gauche du personnage et les statuts/dégâts à droite :
    /// le cadre des soins s'étend donc vers la gauche du point d'ancrage, celui des statuts vers la droite.
    /// </summary>
    public static Vector2 FrameMin(FlyTextGroup group, Vector2 anchor, Vector2 size) => group == FlyTextGroup.Healing
        ? new Vector2(anchor.X - size.X, anchor.Y - (size.Y / 2))
        : new Vector2(anchor.X, anchor.Y - (size.Y / 2));

    /// <summary>Le cadre des textes sur la cible est centré sur son point d'ancrage.</summary>
    public static Vector2 CenteredFrameMin(Vector2 anchor, Vector2 size) => anchor - (size / 2);

    private static bool IsPlausible(float value) => float.IsFinite(value) && value > -MaxCoordinate && value < MaxCoordinate;
}
