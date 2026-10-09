using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;

namespace SimpleHUD;

/// <summary>Groupes de textes défilants affichés sur le personnage. Le jeu décide quel texte va dans quel groupe.</summary>
public enum FlyTextGroup
{
    /// <summary>Soins reçus, y compris les soins sur la durée.</summary>
    Healing = 0,

    /// <summary>Effets de statut gagnés ou perdus, et dégâts subis.</summary>
    StatusDamage = 1,
}

/// <summary>Disposition des textes affichés sur le personnage.</summary>
public enum PersonalLayout
{
    /// <summary>Un seul bloc : soins et statuts/dégâts bougent ensemble, côte à côte comme dans le jeu.</summary>
    Grouped,

    /// <summary>Soins d'un côté, statuts et dégâts subis de l'autre (les deux blocs du jeu).</summary>
    HealingSeparate,

    /// <summary>Statuts d'un côté, soins et dégâts subis ensemble de l'autre.</summary>
    StatusSeparate,

    /// <summary>Soins, statuts et dégâts subis : trois blocs.</summary>
    AllSeparate,
}

/// <summary>Cadre à déplacer dans l'éditeur d'ATH pour les textes sur le personnage.</summary>
public enum PersonalBlock
{
    All,
    Healing,
    StatusDamage,
    Status,
    HealingDamage,
    Damage,

    /// <summary>Autres textes (EXP, PM, objets obtenus…), quand ils sont à part.</summary>
    Other,
}

/// <summary>Familles de textes dont on peut régler la taille et l'affichage.</summary>
public enum FlyTextCategory
{
    /// <summary>Tout le reste (expérience, PM, objets obtenus, artisanat…).</summary>
    Other,

    /// <summary>Effets de statut gagnés, perdus, résistés…</summary>
    Status,

    /// <summary>Soins (et PV absorbés).</summary>
    Healing,

    /// <summary>Dégâts sur toi, avec ratés et esquives.</summary>
    DamageTaken,

    /// <summary>Dégâts sur tes cibles et les autres personnages, avec ratés et esquives.</summary>
    DamageDealt,
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

    /// <summary>
    /// En dessous, l'écart entre le mouvement d'un texte et son défilement habituel vient de la durée variable des
    /// images, pas d'une poussée du jeu (en pixels).
    /// </summary>
    public const float PushThreshold = 1.5f;

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

    /// <summary>Cadre qui englobe les deux blocs du jeu quand ils bougent ensemble : renvoie son coin haut-gauche et sa taille.</summary>
    public static (Vector2 Min, Vector2 Size) LinkedFrame(Vector2 healingAnchor, Vector2 statusDamageAnchor, Vector2 size)
    {
        var healingMin = FrameMin(FlyTextGroup.Healing, healingAnchor, size);
        var statusMin = FrameMin(FlyTextGroup.StatusDamage, statusDamageAnchor, size);
        var min = Vector2.Min(healingMin, statusMin);
        var max = Vector2.Max(healingMin + size, statusMin + size);
        return (min, max - min);
    }

    /// <summary>Le cadre des textes sur la cible est centré sur son point d'ancrage.</summary>
    public static Vector2 CenteredFrameMin(Vector2 anchor, Vector2 size) => anchor - (size / 2);

    /// <summary>Cadres affichés pour une disposition.</summary>
    public static PersonalBlock[] Blocks(PersonalLayout layout) => layout switch
    {
        PersonalLayout.Grouped => [PersonalBlock.All],
        PersonalLayout.HealingSeparate => [PersonalBlock.Healing, PersonalBlock.StatusDamage],
        PersonalLayout.StatusSeparate => [PersonalBlock.Status, PersonalBlock.HealingDamage],
        _ => [PersonalBlock.Healing, PersonalBlock.Status, PersonalBlock.Damage],
    };

    /// <summary>Cadres affichés pour une disposition, plus celui des autres textes s'ils sont à part.</summary>
    public static PersonalBlock[] Blocks(PersonalLayout layout, bool separateOther) =>
        separateOther ? [.. Blocks(layout), PersonalBlock.Other] : Blocks(layout);

    /// <summary>
    /// Bloc du jeu d'un texte affiché sur le personnage, d'après le numéro d'acteur que le jeu lui donne
    /// (0 : soins reçus, 1 : statuts et dégâts subis), ou null pour un texte sur un autre personnage.
    /// </summary>
    public static FlyTextGroup? PlayerGroup(uint? actor) => actor switch
    {
        0 => FlyTextGroup.Healing,
        1 => FlyTextGroup.StatusDamage,
        _ => null,
    };

    /// <summary>
    /// Cadre à part où le plugin déplace un texte du personnage, ou null s'il reste dans le bloc où le jeu le range.
    /// Les statuts (rangés par le jeu avec les dégâts subis) ont leur cadre dans certaines dispositions ; les autres
    /// textes (EXP et objets obtenus, rangés avec les dégâts subis ; PM, avec les soins) quand l'option est cochée.
    /// </summary>
    public static PersonalBlock? SeparateBlock(FlyTextCategory category, FlyTextGroup group, PersonalLayout layout, bool separateOther) => category switch
    {
        FlyTextCategory.Status when group == FlyTextGroup.StatusDamage && SeparatesStatuses(layout) => PersonalBlock.Status,
        FlyTextCategory.Other when separateOther => PersonalBlock.Other,
        _ => null,
    };

    /// <summary>Vrai si les statuts ont leur propre bloc (le jeu les range avec les dégâts : le plugin les sort un par un).</summary>
    public static bool SeparatesStatuses(PersonalLayout layout) => layout is PersonalLayout.StatusSeparate or PersonalLayout.AllSeparate;

    /// <summary>Vrai si les deux blocs du jeu bougent ensemble.</summary>
    public static bool LinksGroups(PersonalLayout layout) => layout is PersonalLayout.Grouped or PersonalLayout.StatusSeparate;

    /// <summary>Blocs du jeu déplacés par un cadre (aucun pour les statuts, qui ont leur propre point d'ancrage).</summary>
    public static FlyTextGroup[] GroupsOf(PersonalBlock block) => block switch
    {
        PersonalBlock.All or PersonalBlock.HealingDamage => [FlyTextGroup.Healing, FlyTextGroup.StatusDamage],
        PersonalBlock.Healing => [FlyTextGroup.Healing],
        PersonalBlock.StatusDamage or PersonalBlock.Damage => [FlyTextGroup.StatusDamage],
        _ => [],
    };

    /// <summary>Écart entre les deux blocs du jeu quand ils sont regroupés : celui d'origine (soins à gauche).</summary>
    public static Vector2 RegroupedHealing(Vector2 statusDamage, Vector2 defaultHealing, Vector2 defaultStatusDamage) =>
        Clamp(statusDamage + (defaultHealing - defaultStatusDamage));

    /// <summary>Première place du bloc des statuts quand on le sépare : juste au-dessus des dégâts.</summary>
    public static Vector2 DefaultStatusPosition(Vector2 statusDamage) => Clamp(statusDamage + new Vector2(0, -0.1f));

    /// <summary>Première place du cadre des autres textes : juste au-dessous des dégâts.</summary>
    public static Vector2 DefaultOtherPosition(Vector2 statusDamage) => Clamp(statusDamage + new Vector2(0, 0.1f));

    /// <summary>
    /// Poussée du jeu pendant une image où un texte est arrivé dans le bloc : mouvement de l'image moins le défilement
    /// habituel (celui d'une image sans arrivée). Constaté en jeu : quand un texte arrive, le jeu pousse vers le bas les
    /// plus anciens de son bloc pour lui faire de la place. Zéro sous le seuil.
    /// </summary>
    public static float GamePush(float moved, float usualStep)
    {
        var push = moved - usualStep;
        return push > PushThreshold ? push : 0f;
    }

    /// <summary>
    /// Écart que chaque texte arrivé pendant une image laisse sous lui, mesuré sur l'empilement du jeu. Constaté en jeu :
    /// les textes arrivés ensemble sont empilés du plus récent (en haut) au plus ancien, chacun juste sous le précédent ;
    /// les plus anciens textes du bloc, s'ils ont été poussés, viennent juste sous le dernier. <paramref name="arrivals"/>
    /// : hauteur des textes arrivés, de haut en bas. Null là où l'écart ne se voit pas (dernier texte, rien poussé).
    /// </summary>
    public static float?[] MeasuredGaps(IReadOnlyList<float> arrivals, float? pushedBelow)
    {
        var gaps = new float?[arrivals.Count];
        for (var i = 0; i < arrivals.Count; i++)
        {
            var next = i + 1 < arrivals.Count ? arrivals[i + 1] : pushedBelow;
            if (next is { } below && below > arrivals[i])
                gaps[i] = below - arrivals[i];
        }

        return gaps;
    }

    /// <summary>
    /// Empile les textes d'un cadre arrivés pendant une image, comme le ferait le jeu si le cadre était seul : le plus
    /// récent à son point de départ, chacun des suivants sous le précédent. Renvoie la poussée à donner aux plus anciens
    /// textes du cadre pour que le plus récent d'entre eux (<paramref name="newestOlder"/>) reste sous le dernier arrivé.
    /// </summary>
    public static float StackArrivals(float start, IReadOnlyList<float> gaps, float? newestOlder, float[] places)
    {
        var cursor = start;
        for (var i = 0; i < gaps.Count; i++)
        {
            places[i] = cursor;
            cursor += gaps[i];
        }

        return newestOlder is { } older ? MathF.Max(0f, cursor - older) : 0f;
    }

    /// <summary>
    /// Famille d'un texte d'après son type (numéros des types : FlyTextKind de Dalamud) et l'acteur qui le reçoit :
    /// les dégâts sur le personnage (acteurs 0 et 1) sont subis, les autres infligés.
    /// </summary>
    public static FlyTextCategory Categorize(int kind, uint? actor) => kind switch
    {
        // Buff, Debuff, DebuffNoEffect, BuffFading, DebuffFading, DebuffResisted, DebuffInvulnerable.
        12 or 13 or 37 or 38 or 39 or 41 or 48 => FlyTextCategory.Status,

        // Healing, HealingCrit, HpDrain.
        21 or 34 or 45 => FlyTextCategory.Healing,

        // Auto-attaques et dégâts sur la durée (0-3), dégâts (4-7), raté/esquive (8-11), Invulnerable,
        // AutoAttackNoText3, coups critiques nommés, FullyResisted, HasNoEffect, Resist, Reflect, Reflected, CriticalHit4.
        >= 0 and <= 11 or 28 or 33 or 35 or 36 or 43 or 44 or 49 or 53 or 54 or 56 =>
            PlayerGroup(actor) == null ? FlyTextCategory.DamageDealt : FlyTextCategory.DamageTaken,

        _ => FlyTextCategory.Other,
    };

    /// <summary>Taille réglable : de 50 % à 200 % de celle du jeu.</summary>
    public static float ClampScale(float scale) => float.IsFinite(scale) ? Math.Clamp(scale, 0.5f, 2f) : 1f;

    private static bool IsPlausible(float value) => float.IsFinite(value) && value > -MaxCoordinate && value < MaxCoordinate;
}
