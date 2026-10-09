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

    // Textes suivis, par fiche : la fiche est rangée à un endroit fixe de l'addon, mais le jeu détruit et recrée
    // le nœud qu'elle désigne. On ne touche donc à un nœud qu'après avoir vérifié, à chaque image, que la fiche
    // désigne toujours ce nœud et qu'il fait toujours partie de l'addon (écrire dans un nœud détruit fait planter le jeu).
    private readonly Dictionary<int, Tracked> tracked = [];
    private readonly HashSet<nint> liveNodes = [];
    private readonly List<int> stale = [];
    private bool reportedUnknownResult;

    private sealed class Tracked
    {
        public FlyTextCategory Category;
        public nint Node;

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
            CollectLiveNodes(addon);
            foreach (var (offset, entry) in tracked)
            {
                var node = LiveNode(addon, offset, entry);
                if (node != null && entry.Written is { } written && node->ScaleX == written.X && node->ScaleY == written.Y)
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

    // Le jeu renvoie la fiche du texte créé, rangée dans l'addon ; son premier champ est le nœud du texte.
    // On ne lit jamais une adresse inconnue : seulement la mémoire de l'addon, et seulement des nœuds de l'addon.
    private void Track(AtkUnitBase* addon, nint result, int kind)
    {
        var offset = (long)(result - (nint)addon);
        var isEntry = result != 0 && offset >= 0 && offset <= sizeof(AddonFlyText) - sizeof(nint);
        var node = isEntry ? *(nint*)result : 0;
#if DEBUG
        Plugin.Log.Information("[diag] create kind={Kind} result={Result} node={Node:X}", kind, isEntry ? $"addon+{offset:X}" : $"{result:X}", node);
#endif
        if (!isEntry || !IsAddonNode(addon, node))
        {
            if (result != 0 && !reportedUnknownResult)
                Plugin.Log.Warning("Unexpected value returned when creating a flying text: sizes cannot be changed.");
            reportedUnknownResult |= result != 0;
            return;
        }

        var scale = new Vector2(((AtkResNode*)node)->ScaleX, ((AtkResNode*)node)->ScaleY);

        // Même nœud réutilisé sans que le jeu ait remis sa taille : on repart de la taille du jeu, pas de la nôtre.
        var gameScale = tracked.TryGetValue((int)offset, out var previous) && previous.Node == node && previous.Written == scale
            ? previous.GameScale
            : scale;

        tracked[(int)offset] = new Tracked
        {
            Category = FlyTextLayout.Categorize(kind),
            Node = node,
            GameScale = gameScale,
            Written = previous?.Node == node ? previous.Written : null,
        };
    }

    // Juste avant l'affichage : taille du jeu × taille choisie pour la famille du texte.
    private void OnAddonPreDraw(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || tracked.Count == 0)
            return;

        CollectLiveNodes(addon);
        stale.Clear();
        foreach (var (offset, entry) in tracked)
        {
            var node = LiveNode(addon, offset, entry);
            if (node == null)
            {
                stale.Add(offset);
                continue;
            }

            var current = new Vector2(node->ScaleX, node->ScaleY);

            // Taille différente de celle qu'on a écrite : le jeu vient de la changer (création, rebond).
            if (entry.Written != current)
                entry.GameScale = current;

            var wanted = entry.GameScale * configuration.GetScale(entry.Category);
            if (current != wanted)
                node->SetScale(wanted.X, wanted.Y);
            entry.Written = wanted;
        }

        // Texte terminé ou nœud détruit : on l'oublie.
        foreach (var offset in stale)
            tracked.Remove(offset);
    }

    // Addon détruit (déconnexion…) : ses nœuds n'existent plus.
    private void OnAddonFinalize(AddonEvent type, AddonArgs args) => tracked.Clear();

    // Nœud du texte s'il existe toujours : la fiche désigne encore le nœud suivi, et ce nœud fait partie de l'addon.
    private AtkResNode* LiveNode(AtkUnitBase* addon, int offset, Tracked entry)
    {
        var node = *(nint*)((byte*)addon + offset);
        return node == entry.Node && liveNodes.Contains(node) ? (AtkResNode*)node : null;
    }

    private void CollectLiveNodes(AtkUnitBase* addon)
    {
        liveNodes.Clear();
        var manager = &addon->UldManager;
        for (var i = 0; i < manager->NodeListCount; i++)
            liveNodes.Add((nint)manager->NodeList[i]);
    }

    private static bool IsAddonNode(AtkUnitBase* addon, nint pointer)
    {
        if (pointer == 0)
            return false;

        var manager = &addon->UldManager;
        for (var i = 0; i < manager->NodeListCount; i++)
        {
            if ((nint)manager->NodeList[i] == pointer)
                return true;
        }

        return false;
    }
}
