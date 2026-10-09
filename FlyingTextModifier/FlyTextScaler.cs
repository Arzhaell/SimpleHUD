using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;

namespace FlyingTextModifier;

/// <summary>
/// Taille des textes par famille (statuts, soins, dégâts). Chaque texte est repéré au moment où le jeu le crée
/// (son type est alors connu), puis sa taille est ajustée juste avant chaque affichage. Les animations du jeu
/// (rebond des critiques) sont gardées : on multiplie la taille que le jeu donne au lieu de la remplacer.
/// </summary>
internal sealed unsafe class FlyTextScaler : IDisposable
{
    private const string AddonName = "_FlyText";

    private readonly Configuration configuration;
    private readonly Hook<AddonFlyText.Delegates.CreateFlyText> createHook;

    // Textes suivis, par nœud. Le jeu réutilise ses nœuds : un nœud recréé change simplement de famille.
    private readonly Dictionary<nint, Tracked> tracked = [];
    private bool reportedUnknownResult;

    private sealed class Tracked
    {
        public FlyTextCategory Category;

        // Taille donnée par le jeu, et dernière taille écrite par le plugin.
        public Vector2 GameScale;
        public Vector2? Written;
    }

    public FlyTextScaler(Configuration configuration)
    {
        this.configuration = configuration;
        createHook = Plugin.GameInterop.HookFromAddress<AddonFlyText.Delegates.CreateFlyText>(
            (nint)AddonFlyText.Addresses.CreateFlyText.Value, OnCreateFlyText);
        createHook.Enable();
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, AddonName, OnAddonPreDraw);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnAddonFinalize);
    }

    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, AddonName, OnAddonPreDraw);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, AddonName, OnAddonFinalize);
        createHook.Dispose();

        // Plugin désactivé : les textes encore à l'écran reprennent la taille du jeu.
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(AddonName).Address;
        if (addon != null)
        {
            foreach (var (address, entry) in tracked)
            {
                var node = (AtkResNode*)address;
                if (entry.Written is { } written && IsAddonNode(addon, address) && node->ScaleX == written.X && node->ScaleY == written.Y)
                    node->SetScale(entry.GameScale.X, entry.GameScale.Y);
            }
        }

        tracked.Clear();
    }

    private nint OnCreateFlyText(
        AddonFlyText* addon, int kind, int val1, int val2, CStringPointer text2, uint color, uint icon, uint damageTypeIcon, CStringPointer text1, float yOffset)
    {
        var result = createHook.Original(addon, kind, val1, val2, text2, color, icon, damageTypeIcon, text1, yOffset);
        try
        {
            Track((AtkUnitBase*)addon, result, kind);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Could not follow a new flying text.");
        }

        return result;
    }

    private void Track(AtkUnitBase* addon, nint result, int kind)
    {
        var node = FindNode(addon, result);
#if DEBUG
        var where = result >= (nint)addon && result < (nint)addon + sizeof(AddonFlyText) ? $"addon+{result - (nint)addon:X}" : $"{result:X}";
        Plugin.Log.Information("[diag] create kind={Kind} result={Result} node={Node}", kind, where, node == null ? "?" : $"{(nint)node:X}");
#endif
        if (node == null)
        {
            if (result != 0 && !reportedUnknownResult)
                Plugin.Log.Warning("Unexpected value returned when creating a flying text: sizes cannot be changed.");
            reportedUnknownResult |= result != 0;
            return;
        }

        if (!tracked.TryGetValue((nint)node, out var entry))
        {
            entry = new Tracked { GameScale = new Vector2(node->ScaleX, node->ScaleY) };
            tracked[(nint)node] = entry;
        }

        entry.Category = FlyTextLayout.Categorize(kind);
    }

    // Juste avant l'affichage : taille du jeu × taille choisie pour la famille du texte.
    private void OnAddonPreDraw(AddonEvent type, AddonArgs args)
    {
        foreach (var (address, entry) in tracked)
        {
            var node = (AtkResNode*)address;
            var current = new Vector2(node->ScaleX, node->ScaleY);

            // Taille différente de celle qu'on a écrite : le jeu vient de la changer (création, rebond).
            if (entry.Written != current)
                entry.GameScale = current;

            var wanted = entry.GameScale * configuration.GetScale(entry.Category);
            if (current != wanted)
                node->SetScale(wanted.X, wanted.Y);
            entry.Written = wanted;
        }
    }

    // Addon détruit (déconnexion…) : ses nœuds n'existent plus.
    private void OnAddonFinalize(AddonEvent type, AddonArgs args) => tracked.Clear();

    // Le jeu renvoie le texte créé : soit son nœud, soit une fiche rangée dans l'addon qui commence par l'adresse du nœud.
    // On ne lit jamais une adresse inconnue : seulement la mémoire de l'addon, et seulement des nœuds de l'addon.
    private static AtkResNode* FindNode(AtkUnitBase* addon, nint result)
    {
        if (result == 0)
            return null;
        if (IsAddonNode(addon, result))
            return (AtkResNode*)result;

        var start = (nint)addon;
        if (result >= start && result <= start + sizeof(AddonFlyText) - sizeof(nint))
        {
            var first = *(nint*)result;
            if (IsAddonNode(addon, first))
                return (AtkResNode*)first;
        }

        return null;
    }

    private static bool IsAddonNode(AtkUnitBase* addon, nint pointer)
    {
        var manager = &addon->UldManager;
        for (var i = 0; i < manager->NodeListCount; i++)
        {
            if ((nint)manager->NodeList[i] == pointer)
                return true;
        }

        return false;
    }
}
