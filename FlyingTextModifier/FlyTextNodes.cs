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
/// Réglages texte par texte : taille par famille (statuts, soins, dégâts), et déplacement des statuts du personnage
/// quand ils ont leur propre bloc. Chaque texte est repéré au moment où le jeu le crée (son type et le personnage
/// qui le reçoit sont alors connus), puis ajusté juste avant chaque affichage. Les animations du jeu (rebond des
/// critiques, défilement) sont gardées : on part de ce que le jeu donne au lieu de le remplacer.
/// </summary>
internal sealed unsafe class FlyTextNodes : IDisposable
{
    private const string AddonName = "_FlyText";

    // Numéro d'acteur que le jeu donne au personnage du joueur pour ses textes défilants.
    private const uint LocalPlayerActor = 1;

    private readonly Configuration configuration;
    private readonly FlyTextGroups groups;
    private readonly Hook<AddonFlyText.Delegates.AddFlyText> addHook;
    private readonly Hook<AddonFlyText.Delegates.CreateFlyText> createHook;

    // Textes suivis, par fiche : la fiche est rangée à un endroit fixe de l'addon, mais le jeu détruit et recrée
    // le nœud qu'elle désigne. On ne touche donc à un nœud qu'après avoir vérifié, à chaque image, que la fiche
    // désigne toujours ce nœud et qu'il fait toujours partie de l'addon (écrire dans un nœud détruit fait planter le jeu).
    private readonly Dictionary<int, Tracked> tracked = [];
    private readonly HashSet<nint> liveNodes = [];
    private readonly List<int> stale = [];
    private bool reportedUnknownResult;

    // Acteur dont le jeu est en train de créer les textes (null en dehors).
    private uint? currentActor;

    private sealed class Tracked
    {
        public FlyTextCategory Category;
        public uint? Actor;
        public nint Node;

        // Taille donnée par le jeu, et dernière taille écrite par le plugin.
        public Vector2 GameScale;
        public Vector2? WrittenScale;

        // Position donnée par le jeu, dernière position écrite et décalage qu'elle contenait.
        public Vector2 GamePosition;
        public Vector2? WrittenPosition;
        public Vector2 AppliedShift;
    }

    public FlyTextNodes(Configuration configuration, FlyTextGroups groups)
    {
        this.configuration = configuration;
        this.groups = groups;
        addHook = Plugin.GameInterop.HookFromAddress<AddonFlyText.Delegates.AddFlyText>(
            (nint)AddonFlyText.Addresses.AddFlyText.Value, OnAddFlyText);
        createHook = Plugin.GameInterop.HookFromAddress<AddonFlyText.Delegates.CreateFlyText>(
            (nint)AddonFlyText.Addresses.CreateFlyText.Value, OnCreateFlyText);
        addHook.Enable();
        createHook.Enable();
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, AddonName, OnAddonPreDraw);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnAddonFinalize);
    }

    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, AddonName, OnAddonPreDraw);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, AddonName, OnAddonFinalize);
        createHook.Dispose();
        addHook.Dispose();

        // Plugin désactivé : les textes encore à l'écran reprennent la taille et la place du jeu.
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(AddonName).Address;
        if (addon != null)
        {
            CollectLiveNodes(addon);
            foreach (var (offset, entry) in tracked)
            {
                var node = LiveNode(addon, offset, entry);
                if (node == null)
                    continue;
                if (entry.WrittenScale is { } scale && node->ScaleX == scale.X && node->ScaleY == scale.Y)
                    node->SetScale(entry.GameScale.X, entry.GameScale.Y);
                if (entry.WrittenPosition is { } position && node->X == position.X && node->Y == position.Y)
                    node->SetPositionFloat(entry.GamePosition.X, entry.GamePosition.Y);
            }
        }

        tracked.Clear();
    }

    // Le jeu crée les textes d'un acteur à la fois : on retient lequel pour les textes créés pendant ce temps.
    private void OnAddFlyText(
        AddonFlyText* addon, uint actorIndex, uint messageMax, NumberArrayData* numberArrayData, uint offsetNum, uint offsetNumMax,
        StringArrayData* stringArrayData, uint offsetStr, uint offsetStrMax, int unknown)
    {
        currentActor = actorIndex;
        try
        {
            addHook.Original(addon, actorIndex, messageMax, numberArrayData, offsetNum, offsetNumMax, stringArrayData, offsetStr, offsetStrMax, unknown);
        }
        finally
        {
            currentActor = null;
        }
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
        Plugin.Log.Information("[diag] create kind={Kind} actor={Actor} result={Result} node={Node:X}",
            kind, currentActor?.ToString() ?? "?", isEntry ? $"addon+{offset:X}" : $"{result:X}", node);
#endif
        if (!isEntry || !IsAddonNode(addon, node))
        {
            if (result != 0 && !reportedUnknownResult)
                Plugin.Log.Warning("Unexpected value returned when creating a flying text: it cannot be adjusted.");
            reportedUnknownResult |= result != 0;
            return;
        }

        var textNode = (AtkResNode*)node;
        var scale = new Vector2(textNode->ScaleX, textNode->ScaleY);
        var position = new Vector2(textNode->X, textNode->Y);

        // Même nœud réutilisé sans que le jeu ait remis sa taille ou sa place : on repart des valeurs du jeu, pas des nôtres.
        tracked.TryGetValue((int)offset, out var previous);
        var reused = previous?.Node == node;
        tracked[(int)offset] = new Tracked
        {
            Category = FlyTextLayout.Categorize(kind),
            Actor = currentActor,
            Node = node,
            GameScale = reused && previous!.WrittenScale == scale ? previous.GameScale : scale,
            WrittenScale = reused ? previous!.WrittenScale : null,
            GamePosition = reused && previous!.WrittenPosition == position ? previous.GamePosition : position,
            WrittenPosition = reused ? previous!.WrittenPosition : null,
            AppliedShift = reused ? previous!.AppliedShift : Vector2.Zero,
        };
    }

    // Juste avant l'affichage : taille du jeu × taille de la famille, et place du jeu + écart du bloc des statuts.
    private void OnAddonPreDraw(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || tracked.Count == 0)
            return;

        var statusShift = groups.StatusShift();
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

            ApplyScale(node, entry);

            // Seuls les statuts du personnage changent de bloc ; ceux des autres restent sur eux.
            var shift = entry.Category == FlyTextCategory.Status && entry.Actor == LocalPlayerActor ? statusShift : Vector2.Zero;
            ApplyPosition(node, entry, shift);
        }

        // Texte terminé ou nœud détruit : on l'oublie.
        foreach (var offset in stale)
            tracked.Remove(offset);
    }

    private void ApplyScale(AtkResNode* node, Tracked entry)
    {
        var current = new Vector2(node->ScaleX, node->ScaleY);

        // Taille différente de celle qu'on a écrite : le jeu vient de la changer (création, rebond).
        if (entry.WrittenScale != current)
            entry.GameScale = current;

        var wanted = entry.GameScale * configuration.GetScale(entry.Category);
        if (current != wanted)
            node->SetScale(wanted.X, wanted.Y);
        entry.WrittenScale = wanted;
    }

    private static void ApplyPosition(AtkResNode* node, Tracked entry, Vector2 shift)
    {
        // Rien à décaler, ni maintenant ni avant : on laisse le jeu faire.
        if (shift == Vector2.Zero && entry.AppliedShift == Vector2.Zero)
        {
            entry.WrittenPosition = null;
            return;
        }

        var current = new Vector2(node->X, node->Y);
        entry.GamePosition = entry.WrittenPosition is { } written
            ? FlyTextLayout.GamePosition(current, entry.GamePosition, written, entry.AppliedShift)
            : current;

        var wanted = entry.GamePosition + shift;
        if (current != wanted)
            node->SetPositionFloat(wanted.X, wanted.Y);
        entry.WrittenPosition = wanted;
        entry.AppliedShift = shift;
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
