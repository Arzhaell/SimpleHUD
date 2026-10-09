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
/// qui le reçoit sont alors connus), puis ajusté après chaque mise à jour du jeu (qui a lieu à chaque image).
/// Constaté en jeu : le jeu calcule l'affichage pendant sa mise à jour, à partir des valeurs des nœuds ; nos réglages
/// restent donc en place (une valeur retirée avant la mise à jour n'apparaît jamais). Le jeu fait défiler un texte à
/// partir de sa place actuelle, ce qui garde le décalage ; quand il le replace ou change sa taille, on réapplique.
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

        // Taille du jeu et dernière taille écrite.
        public Vector2 GameScale;
        public Vector2? WrittenScale;

        // Dernière place écrite et décalage qu'elle contenait.
        public Vector2? WrittenPosition;
        public Vector2 AppliedShift;

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
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, AddonName, OnAddonPostUpdate);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnAddonFinalize);
    }

    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, AddonName, OnAddonPostUpdate);
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
                if (node != null)
                    Restore(node, entry);
            }
        }

        tracked.Clear();
    }

    // Après chaque mise à jour du jeu : taille du jeu × taille de la famille, place du jeu + écart du bloc des statuts.
    private void OnAddonPostUpdate(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || tracked.Count == 0)
            return;

        var statusShift = groups.StatusShift();
        CollectLiveNodes(addon);
        stale.Clear();
        foreach (var (offset, entry) in tracked)
        {
            // La fiche désigne un autre nœud : le texte est terminé, on l'oublie.
            if (!StillTracks(addon, offset, entry))
            {
                stale.Add(offset);
                continue;
            }

            // Nœud absent de la liste de l'addon (texte en attente, caché…) : on n'y écrit pas, mais on le garde.
            var node = LiveNode(addon, offset, entry);
            if (node != null)
                Apply(node, entry, statusShift, offset);
        }

        foreach (var offset in stale)
            tracked.Remove(offset);
    }

    private void Apply(AtkResNode* node, Tracked entry, Vector2 statusShift, int offset)
    {
        // Taille différente de celle qu'on a écrite : le jeu vient de la changer (création, rebond).
        var scale = new Vector2(node->ScaleX, node->ScaleY);
        if (entry.WrittenScale != scale)
            entry.GameScale = scale;

        var wantedScale = entry.GameScale * configuration.GetScale(entry.Category);
        if (scale != wantedScale)
            node->SetScale(wantedScale.X, wantedScale.Y);
        entry.WrittenScale = wantedScale;

        // Seuls les statuts du personnage changent de bloc ; ceux des autres restent sur eux.
        var shift = entry.Category == FlyTextCategory.Status && entry.Actor == LocalPlayerActor ? statusShift : Vector2.Zero;
        if (shift == Vector2.Zero && entry.AppliedShift == Vector2.Zero)
            return;

        // Place du jeu : le texte a défilé depuis notre place (le décalage y est encore), ou le jeu l'a replacé.
        var position = new Vector2(node->X, node->Y);
        var replaced = entry.WrittenPosition is not { } written || FlyTextLayout.GameReplacedText(position, written, entry.AppliedShift);
        var gamePosition = replaced ? position : position - entry.AppliedShift;
        Log(replaced ? "Replaced" : "Scrolled", offset, position, entry);

        var wanted = gamePosition + shift;
        if (position != wanted)
            node->SetPositionFloat(wanted.X, wanted.Y);
        entry.WrittenPosition = wanted;
        entry.AppliedShift = shift;
    }

    // Remet la taille et la place du jeu, si le nœud porte encore nos valeurs.
    private static void Restore(AtkResNode* node, Tracked entry)
    {
        if (entry.WrittenScale is { } scale && node->ScaleX == scale.X && node->ScaleY == scale.Y)
            node->SetScale(entry.GameScale.X, entry.GameScale.Y);
        if (entry.WrittenPosition is { } position && node->X == position.X && node->Y == position.Y && entry.AppliedShift != Vector2.Zero)
            node->SetPositionFloat(position.X - entry.AppliedShift.X, position.Y - entry.AppliedShift.Y);
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

        // Même nœud repris pour un nouveau texte alors que nos réglages y sont encore : on remet d'abord les valeurs du jeu.
        if (tracked.TryGetValue((int)offset, out var previous) && previous.Node == node)
            Restore((AtkResNode*)node, previous);

        var entry = new Tracked
        {
            Category = FlyTextLayout.Categorize(kind),
            Actor = currentActor,
            Node = node,
        };
        tracked[(int)offset] = entry;

        // Réglé dès sa création, pour qu'il apparaisse directement à la bonne taille et à la bonne place.
        Apply((AtkResNode*)node, entry, groups.StatusShift(), (int)offset);
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
    private static void Log(string phase, int offset, Vector2 position, Tracked entry)
    {
#if DEBUG
        if (entry.Logged >= 30 && phase != "Replaced")
            return;
        if (entry.Logged >= 60)
            return;

        entry.Logged++;
        Plugin.Log.Information("[diag] node addon+{Offset:X} {Phase} pos=({X:F0},{Y:F0}) written={Written} shift=({SX:F0},{SY:F0})",
            offset, phase, position.X, position.Y,
            entry.WrittenPosition is { } w ? $"({w.X:F0},{w.Y:F0})" : "-", entry.AppliedShift.X, entry.AppliedShift.Y);
#endif
    }
}
