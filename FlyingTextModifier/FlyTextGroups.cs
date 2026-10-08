using System;
using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FlyingTextModifier;

/// <summary>Lit et écrit dans le jeu la position des groupes de textes défilants du personnage.</summary>
internal sealed unsafe class FlyTextGroups : IDisposable
{
    private const string AddonName = "_FlyText";

    private readonly Configuration configuration;
    private readonly object sync = new();

    // Par groupe, en fraction de l'écran : position actuelle et position d'origine du jeu.
    private readonly Vector2?[] current = new Vector2?[FlyTextLayout.Groups.Length];
    private readonly Vector2?[] gameDefaults = new Vector2?[FlyTextLayout.Groups.Length];

    // Groupes réinitialisés dont la position d'origine reste à réécrire dans le jeu.
    private readonly bool[] restorePending = new bool[FlyTextLayout.Groups.Length];

    private int arrayOffset = -1;

    public FlyTextGroups(Configuration configuration)
    {
        this.configuration = configuration;
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnAddonSetup);
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    /// <summary>Vrai si l'addon existe mais que le tableau des groupes n'y est pas (le jeu a sans doute changé).</summary>
    public bool NotFound { get; private set; }

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
            restorePending[(int)group] = false;
        }
    }

    /// <summary>Remet un groupe à la position d'origine du jeu.</summary>
    public void Reset(FlyTextGroup group)
    {
        lock (sync)
        {
            if (configuration.Positions.Remove(group))
                restorePending[(int)group] = true;
            if (gameDefaults[(int)group] is { } position)
                current[(int)group] = position;
        }
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, AddonName, OnAddonSetup);

        // Plugin désactivé ou désinstallé : les textes reprennent leur place d'origine.
        Apply(restoreAll: true);
    }

    // Addon recréé (connexion, changement de résolution…) : le jeu vient d'y remettre ses positions d'origine.
    private void OnAddonSetup(AddonEvent type, AddonArgs args)
    {
        lock (sync)
            Array.Clear(gameDefaults);
    }

    private void OnFrameworkUpdate(IFramework framework) => Apply(restoreAll: false);

    private void Apply(bool restoreAll)
    {
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(AddonName).Address;
        var screen = ScreenSize();
        if (addon == null || screen.X <= 0 || screen.Y <= 0)
            return;

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

                Vector2? target;
                if (restoreAll)
                    target = configuration.Positions.ContainsKey(group) || restorePending[index] ? gameDefaults[index] : null;
                else if (configuration.Positions.TryGetValue(group, out var chosen))
                    target = chosen;
                else
                    target = restorePending[index] ? gameDefaults[index] : null;
                restorePending[index] = false;

                // Réécrite à chaque image : le jeu peut remettre ses valeurs (changement de résolution, recréation de l'addon).
                if (target is { } ratio)
                {
                    var pixels = FlyTextLayout.ToPixels(ratio, screen);
                    if (*position != pixels)
                        *position = pixels;
                }

                current[index] = FlyTextLayout.ToRatio(*position, screen);
            }
        }
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
