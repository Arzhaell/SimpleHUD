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
/// qui le reçoit sont alors connus).
/// La taille et la place d'un texte sont aussi l'état de son animation pour le jeu (il le fait défiler à partir de
/// sa place actuelle). Le plugin ne les modifie donc que pour l'affichage : il les applique juste après la mise à
/// jour des textes par le jeu, et remet les valeurs du jeu juste avant la mise à jour suivante. Le dessin a lieu
/// entre les deux (constaté en jeu : remettre les valeurs dès le début de l'image annulait l'effet à l'écran).
/// </summary>
internal sealed unsafe class FlyTextNodes : IDisposable
{
    private const string AddonName = "_FlyText";

    // Numéro d'acteur que le jeu donne au personnage du joueur pour ses statuts et dégâts subis.
    private const uint LocalPlayerActor = 1;

    private readonly Configuration configuration;
    private readonly FlyTextGroups groups;
    private readonly Hook<AddonFlyText.Delegates.AddFlyText> addHook;
    private readonly Hook<AddonFlyText.Delegates.CreateFlyText> createHook;

    // Textes suivis, par fiche : la fiche est rangée à un endroit fixe de l'addon, mais le jeu détruit et recrée
    // le nœud qu'elle désigne. On ne touche donc à un nœud qu'après avoir vérifié que la fiche désigne toujours
    // ce nœud et qu'il fait toujours partie de l'addon (écrire dans un nœud détruit fait planter le jeu).
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

        // Vrai entre l'application de nos réglages et la remise des valeurs du jeu.
        public bool Applied;

        // Valeurs du jeu, et valeurs écrites par le plugin pour l'affichage.
        public Vector2 GameScale;
        public Vector2 GamePosition;
        public Vector2 WrittenScale;
        public Vector2 WrittenPosition;

#if DEBUG
        // Relevé : nombre de lignes déjà notées pour ce texte.
        public int Logged;
#endif
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
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreUpdate, AddonName, OnAddonPreUpdate);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, AddonName, OnAddonPostUpdate);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnAddonFinalize);
    }

    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreUpdate, AddonName, OnAddonPreUpdate);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, AddonName, OnAddonPostUpdate);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, AddonName, OnAddonFinalize);
        createHook.Dispose();
        addHook.Dispose();

        // Plugin désactivé : les textes encore à l'écran reprennent la taille et la place du jeu.
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(AddonName).Address;
        if (addon != null)
            RestoreAll(addon);
        tracked.Clear();
    }

    // Juste avant que le jeu ne fasse avancer ses textes : il retrouve ses propres valeurs.
    private void OnAddonPreUpdate(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon != null && tracked.Count != 0)
            RestoreAll(addon);
    }

    // Juste après la mise à jour du jeu (à chaque image) : taille du jeu × taille de la famille, place du jeu + écart du bloc des statuts.
    private void OnAddonPostUpdate(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || tracked.Count == 0)
            return;

        var statusShift = groups.StatusShift();
        CollectLiveNodes(addon);
        foreach (var (offset, entry) in tracked)
        {
            var node = LiveNode(addon, offset, entry);
            if (node == null)
                continue;

            Restore(node, entry);
            entry.GameScale = new Vector2(node->ScaleX, node->ScaleY);
            entry.GamePosition = new Vector2(node->X, node->Y);
            entry.WrittenScale = entry.GameScale * configuration.GetScale(entry.Category);
            entry.WrittenPosition = entry.GamePosition + ShiftFor(entry, statusShift);
            if (entry.WrittenScale != entry.GameScale)
                node->SetScale(entry.WrittenScale.X, entry.WrittenScale.Y);
            if (entry.WrittenPosition != entry.GamePosition)
                node->SetPositionFloat(entry.WrittenPosition.X, entry.WrittenPosition.Y);
            entry.Applied = true;
            Log("Apply", offset, node, entry);
        }
    }

    // Remet les valeurs du jeu sur tous les textes suivis, et oublie ceux dont la fiche désigne un autre nœud.
    private void RestoreAll(AtkUnitBase* addon)
    {
        CollectLiveNodes(addon);
        stale.Clear();
        foreach (var (offset, entry) in tracked)
        {
            if (!StillTracks(addon, offset, entry))
            {
                stale.Add(offset);
                continue;
            }

            // Nœud absent de la liste de l'addon (texte en attente, caché…) : on n'y écrit pas, mais on le garde.
            var node = LiveNode(addon, offset, entry);
            if (node != null)
            {
                Log("Restore", offset, node, entry);
                Restore(node, entry);
            }
        }

        foreach (var offset in stale)
            tracked.Remove(offset);
    }

    // Remet une valeur du jeu seulement si le nœud porte encore celle qu'on a écrite : sinon le jeu l'a changée entre-temps.
    private static void Restore(AtkResNode* node, Tracked entry)
    {
        if (!entry.Applied)
            return;

        if (node->ScaleX == entry.WrittenScale.X && node->ScaleY == entry.WrittenScale.Y && entry.WrittenScale != entry.GameScale)
            node->SetScale(entry.GameScale.X, entry.GameScale.Y);
        if (node->X == entry.WrittenPosition.X && node->Y == entry.WrittenPosition.Y && entry.WrittenPosition != entry.GamePosition)
            node->SetPositionFloat(entry.GamePosition.X, entry.GamePosition.Y);
        entry.Applied = false;
    }

    // Seuls les statuts du personnage changent de bloc ; ceux des autres restent sur eux.
    private static Vector2 ShiftFor(Tracked entry, Vector2 statusShift) =>
        entry.Category == FlyTextCategory.Status && entry.Actor == LocalPlayerActor ? statusShift : Vector2.Zero;

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

        // Même nœud repris pour un nouveau texte alors que nos réglages y sont encore : on remet d'abord les valeurs du jeu.
        if (tracked.TryGetValue((int)offset, out var previous) && previous.Node == node)
            Restore((AtkResNode*)node, previous);

        tracked[(int)offset] = new Tracked
        {
            Category = FlyTextLayout.Categorize(kind),
            Actor = currentActor,
            Node = node,
        };
    }

    // Addon détruit (déconnexion…) : ses nœuds n'existent plus.
    private void OnAddonFinalize(AddonEvent type, AddonArgs args) => tracked.Clear();

    // La fiche désigne toujours le nœud suivi.
    private static bool StillTracks(AtkUnitBase* addon, int offset, Tracked entry) => *(nint*)((byte*)addon + offset) == entry.Node;

    // Nœud du texte si on peut y écrire : la fiche désigne encore le nœud suivi, et ce nœud fait partie de l'addon.
    private AtkResNode* LiveNode(AtkUnitBase* addon, int offset, Tracked entry) =>
        StillTracks(addon, offset, entry) && liveNodes.Contains(entry.Node) ? (AtkResNode*)entry.Node : null;

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

    // Relevé (version de développement) : les premières images de chaque statut déplacé.
    [System.Diagnostics.Conditional("DEBUG")]
    private static void Log(string phase, int offset, AtkResNode* node, Tracked entry)
    {
#if DEBUG
        if (entry.Category != FlyTextCategory.Status || entry.Actor != LocalPlayerActor || entry.Logged >= 12)
            return;

        entry.Logged++;
        Plugin.Log.Information(
            "[diag] node addon+{Offset:X} {Phase} pos=({X:F0},{Y:F0}) game=({GX:F0},{GY:F0}) written=({WX:F0},{WY:F0})",
            offset, phase, node->X, node->Y, entry.GamePosition.X, entry.GamePosition.Y, entry.WrittenPosition.X, entry.WrittenPosition.Y);
#endif
    }
}
