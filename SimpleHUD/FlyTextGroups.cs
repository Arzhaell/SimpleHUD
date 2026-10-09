using System;
using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FlyingTextModifier;

/// <summary>
/// Applique dans le jeu les positions choisies :
/// - les deux groupes du personnage ont une position fixe à l'écran, écrite directement ;
/// - les textes sur la cible suivent la cible (le jeu recalcule leur position en continu) : on décale tout le calque
///   des textes défilants, et on retire ce décalage aux deux groupes du personnage pour qu'ils restent en place.
/// </summary>
internal sealed unsafe class FlyTextGroups : IDisposable
{
    private const string AddonName = "_FlyText";

    private readonly Configuration configuration;
    private readonly object sync = new();

    // Par groupe, en fraction de l'écran : position affichée et position d'origine du jeu.
    private readonly Vector2?[] current = new Vector2?[FlyTextLayout.Groups.Length];
    private readonly Vector2?[] gameDefaults = new Vector2?[FlyTextLayout.Groups.Length];

    // Groupes dont on a écrit la position à l'image précédente (à remettre d'origine quand on n'y touche plus).
    private readonly bool[] managed = new bool[FlyTextLayout.Groups.Length];

    private int arrayOffset = -1;
    private bool layerShifted;

    public FlyTextGroups(Configuration configuration)
    {
        this.configuration = configuration;
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnAddonSetup);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, AddonName, OnAddonPreDraw);
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    /// <summary>Vrai si l'addon existe mais que le tableau des groupes n'y est pas (le jeu a sans doute changé).</summary>
    public bool NotFound { get; private set; }

    /// <summary>Taille de l'écran du jeu en pixels (zéro tant qu'elle est inconnue).</summary>
    public Vector2 Screen { get; private set; }

    /// <summary>Position actuelle du groupe en fraction de l'écran, ou null tant qu'elle est inconnue.</summary>
    public Vector2? GetPosition(FlyTextGroup group)
    {
        lock (sync)
            return current[(int)group];
    }

    /// <summary>Déplace un groupe (en fraction de l'écran). Le jeu en tient compte à l'image suivante.</summary>
    public void SetPosition(FlyTextGroup group, Vector2 ratio)
    {
        lock (sync)
        {
            ratio = FlyTextLayout.Clamp(ratio);
            configuration.Positions[group] = ratio;
            current[(int)group] = ratio;
        }
    }

    /// <summary>Remet un groupe à la position d'origine du jeu.</summary>
    public void Reset(FlyTextGroup group)
    {
        lock (sync)
        {
            configuration.Positions.Remove(group);
            if (gameDefaults[(int)group] is { } position)
                current[(int)group] = position;
        }
    }

    /// <summary>Position d'origine du groupe dans le jeu (fraction de l'écran), ou null tant qu'elle est inconnue.</summary>
    public Vector2? GetGameDefault(FlyTextGroup group)
    {
        lock (sync)
            return gameDefaults[(int)group];
    }

    /// <summary>Position du bloc des statuts quand il est séparé (fraction de l'écran).</summary>
    public Vector2? StatusPosition
    {
        get
        {
            lock (sync)
                return configuration.StatusPosition;
        }

        set
        {
            lock (sync)
                configuration.StatusPosition = value is { } ratio ? FlyTextLayout.Clamp(ratio) : null;
        }
    }

    /// <summary>
    /// Écart en pixels entre le bloc des statuts et le bloc où le jeu les range (statuts/dégâts) :
    /// c'est de cet écart que le plugin déplace chaque texte de statut. Zéro si les statuts ne sont pas séparés.
    /// </summary>
    public Vector2 StatusShift()
    {
        lock (sync)
        {
            if (!FlyTextLayout.SeparatesStatuses(configuration.Layout)
                || configuration.StatusPosition is not { } status
                || current[(int)FlyTextGroup.StatusDamage] is not { } statusDamage)
                return Vector2.Zero;

            return (status - statusDamage) * Screen;
        }
    }

    /// <summary>Décalage des textes sur la cible, en fraction de l'écran.</summary>
    public Vector2 TargetOffset
    {
        get
        {
            lock (sync)
                return configuration.TargetOffset;
        }

        set
        {
            lock (sync)
                configuration.TargetOffset = FlyTextLayout.ClampOffset(value);
        }
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, AddonName, OnAddonSetup);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, AddonName, OnAddonPreDraw);

        // Plugin désactivé ou désinstallé : les textes reprennent leur place d'origine.
        Apply(restoreAll: true);
    }

    // Addon recréé (connexion, changement de résolution…) : le jeu vient d'y remettre ses positions d'origine.
    private void OnAddonSetup(AddonEvent type, AddonArgs args)
    {
        lock (sync)
            Array.Clear(gameDefaults);
    }

    // Juste avant l'affichage, au cas où le jeu aurait remis le calque à sa place pendant l'image.
    private void OnAddonPreDraw(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon != null)
            ShiftLayer(addon, LayerShift());
    }

    private void OnFrameworkUpdate(IFramework framework) => Apply(restoreAll: false);

    private void Apply(bool restoreAll)
    {
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(AddonName).Address;
        var screen = ScreenSize();
        if (addon == null || screen.X <= 0 || screen.Y <= 0)
            return;
        Screen = screen;

        var shift = restoreAll ? Vector2.Zero : LayerShift();
        ShiftLayer(addon, shift);

        var memory = new ReadOnlySpan<byte>(addon, sizeof(AddonFlyText));
        if (!FlyTextLayout.IsGroupArray(memory, arrayOffset) && !FindGroupArray(memory))
            return;

        lock (sync)
        {
            foreach (var group in FlyTextLayout.Groups)
            {
                var index = (int)group;
                var position = (Vector2*)((byte*)addon + FlyTextLayout.PositionAddress(arrayOffset, group));

                // Lue avant toute écriture de notre part, la position est celle choisie par le jeu.
                gameDefaults[index] ??= FlyTextLayout.ToRatio(*position, screen);

                var chosen = Vector2.Zero;
                var custom = !restoreAll && configuration.Positions.TryGetValue(group, out chosen);
                Vector2? wanted = custom ? chosen : shift != Vector2.Zero || managed[index] ? gameDefaults[index] : null;

                // Réécrite à chaque image : le jeu peut remettre ses valeurs (changement de résolution, recréation de l'addon).
                if (wanted is { } ratio)
                {
                    var pixels = FlyTextLayout.ToPixels(ratio, screen) - shift;
                    if (*position != pixels)
                        *position = pixels;
                }

                managed[index] = custom || shift != Vector2.Zero;
                current[index] = FlyTextLayout.ToRatio(*position + shift, screen);
            }
        }
    }

    // Décalage du calque en pixels (celui des textes sur la cible).
    private Vector2 LayerShift()
    {
        var screen = Screen;
        lock (sync)
            return FlyTextLayout.OffsetToPixels(configuration.TargetOffset, screen);
    }

    // Sans décalage demandé, on ne touche au calque que pour le remettre en place.
    private void ShiftLayer(AtkUnitBase* addon, Vector2 shift)
    {
        var root = addon->RootNode;
        if (root == null || (shift == Vector2.Zero && !layerShifted))
            return;

        var x = addon->X + shift.X;
        var y = addon->Y + shift.Y;
        if (root->X != x || root->Y != y)
            root->SetPositionFloat(x, y);
        layerShifted = shift != Vector2.Zero;
    }

    private bool FindGroupArray(ReadOnlySpan<byte> memory)
    {
        var offset = FlyTextLayout.FindGroupArray(memory, sizeof(AtkUnitBase));
        if (offset < 0)
        {
            if (!NotFound)
                Plugin.Log.Warning("Flying text groups not found in {Addon}: the game layout may have changed.", AddonName);
            NotFound = true;
            return false;
        }

        if (offset != arrayOffset)
            Plugin.Log.Debug("Flying text groups found at {Offset:X} in {Addon}.", offset, AddonName);
        arrayOffset = offset;
        NotFound = false;
        return true;
    }

    private static Vector2 ScreenSize()
    {
        var device = Device.Instance();
        return device == null ? Vector2.Zero : new Vector2(device->Width, device->Height);
    }
}
