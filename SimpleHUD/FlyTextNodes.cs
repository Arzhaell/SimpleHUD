using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;

namespace SimpleHUD;

/// <summary>
/// Réglages texte par texte : taille par famille (statuts, soins, dégâts subis ou infligés, autres), déplacement des
/// textes du personnage qui ont leur propre cadre (statuts, autres textes) et indépendance des cadres. Chaque texte est
/// repéré au moment où le jeu le crée (son type et le personnage qui le reçoit sont alors connus), puis ajusté après
/// chaque mise à jour du jeu (qui a lieu à chaque image).
/// Constaté en jeu : le jeu calcule l'affichage pendant sa mise à jour, à partir des valeurs des nœuds ; nos réglages
/// restent donc en place (une valeur retirée avant la mise à jour n'apparaît jamais). Le jeu fait défiler un texte à
/// partir de sa place actuelle, ce qui garde le décalage ; quand il le replace ou change sa taille, on réapplique.
/// À la création d'un texte, le jeu pousse vers le bas ceux de son bloc pour lui faire de la place : quand le nouveau
/// texte s'affiche dans un autre cadre, le plugin annule cette poussée.
/// </summary>
internal sealed unsafe class FlyTextNodes : IDisposable
{
    private const string AddonName = "_FlyText";

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

    // Création en cours de textes sur le personnage (null en dehors).
    private PushTrace? trace;

    // Cadre de chaque texte créé depuis la dernière mise à jour, par bloc du jeu du personnage (null : bloc du jeu).
    private readonly List<PersonalBlock?>[] createdBlocks = [[], []];

    private sealed class Tracked
    {
        public FlyTextCategory Category;

        // Bloc du jeu sur le personnage (null : texte sur un autre personnage, seule sa taille change).
        public FlyTextGroup? Group;
        public nint Node;

        // Taille du jeu et dernière taille écrite.
        public Vector2 GameScale;
        public Vector2? WrittenScale;

        // Dernière place écrite, avec l'écart de son cadre à part et le total des poussées du jeu annulées
        // (négatif : vers le haut).
        public Vector2? WrittenPosition;
        public Vector2 BlockShift;
        public float Cancelled;

        // Relevé pendant la création d'autres textes : poussée totale, et part due aux textes d'un autre cadre.
        public float SeenPush;
        public float PushToCancel;

        // Défilement de la dernière image sans création dans son bloc.
        public float Step;

        // Vrai une fois que le jeu a placé le texte (son premier mouvement après la création).
        public bool Placed;

        public Vector2 AppliedShift => BlockShift + new Vector2(0, Cancelled);

#if DEBUG
        // Relevé : emplacement de la fiche et nombre de lignes déjà notées pour ce texte.
        public int Offset;
        public int Logged;
#endif
    }

    // Création de textes sur le personnage : hauteur des textes de son bloc relevée avant et après chaque création.
    private sealed class PushTrace(FlyTextGroup group)
    {
        public readonly FlyTextGroup Group = group;
        public readonly Dictionary<Tracked, float> LastY = [];

        // Mouvements relevés, par tranche (tranche k : entre la création k − 1 et la création k).
        public readonly List<(Tracked Entry, float Moved, int Slice)> Moves = [];

        // Textes créés, dans l'ordre : suivi ou non (masqué, inattendu), et leur cadre.
        public readonly List<(bool Tracked, PersonalBlock? Block)> Created = [];
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

    /// <summary>Acteur qui reçoit les textes en cours de création (null en dehors) : sert à trier les dégâts subis ou infligés.</summary>
    public uint? CurrentActor => currentActor;

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

    // Après chaque mise à jour du jeu : taille du jeu × taille de la famille, place du jeu + écart du cadre à part,
    // moins les poussées venues d'un autre cadre.
    private void OnAddonPostUpdate(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon != null && tracked.Count > 0)
        {
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
                    Apply(addon, node, entry, offset);
            }

            foreach (var offset in stale)
                tracked.Remove(offset);
        }

        foreach (var blocks in createdBlocks)
            blocks.Clear();
    }

    private void Apply(AtkUnitBase* addon, AtkResNode* node, Tracked entry, int offset)
    {
        // Taille différente de celle qu'on a écrite : le jeu vient de la changer (création, rebond).
        var scale = new Vector2(node->ScaleX, node->ScaleY);
        if (entry.WrittenScale != scale)
            entry.GameScale = scale;

        var wantedScale = entry.GameScale * configuration.GetScale(entry.Category);
        if (scale != wantedScale)
            node->SetScale(wantedScale.X, wantedScale.Y);
        entry.WrittenScale = wantedScale;

        // Textes sur les autres personnages : seule leur taille change.
        if (entry.Group is not { } group)
            return;

        var block = BlockOf(entry, group);
        var shift = block is { } separate ? groups.SeparateShift(separate, group) : Vector2.Zero;
        var created = createdBlocks[(int)group];
        var position = new Vector2(node->X, node->Y);

        // Juste créé, le texte est à (0, 0) : le jeu le placera en ajoutant sa position à celle du texte.
        var gamePosition = position;
        var cancel = entry.PushToCancel;
        if (entry.WrittenPosition is { } written)
        {
            // Place du jeu : le texte a défilé depuis notre place (nos écarts y sont encore), ou le jeu l'a replacé.
            // Son premier mouvement après la création est son placement par le jeu, qui ajoute sa position à celle du
            // texte : l'écart posé à la création y est donc déjà (ce n'est pas un replacement). Seul un texte d'un
            // cadre à part peut trahir un replacement (il quitte la colonne de son cadre).
            var pushPossible = created.Count > 0 || entry.SeenPush != 0;
            var replaced = entry.Placed && entry.BlockShift != Vector2.Zero
                && FlyTextLayout.GameReplacedText(position, written, entry.BlockShift, pushPossible);
            if (!replaced)
                gamePosition = position - entry.AppliedShift;

            // Défilement habituel relevé sur les images sans création dans le bloc ; sur les autres, ce qui le
            // dépasse est une poussée du jeu, à annuler si elle vient d'un texte d'un autre cadre.
            if (entry.Placed && !replaced)
            {
                var moved = position.Y - written.Y;
                if (created.Count == 0)
                    entry.Step = moved;
                else if (!created.Contains(block))
                    cancel += FlyTextLayout.UnseenPush(moved, entry.SeenPush, entry.Step);
            }

            if (position != written)
                entry.Placed = true;
            Log(addon, replaced ? "Replaced" : "Scrolled", position, entry, cancel);
        }

        entry.Cancelled -= cancel;
        entry.SeenPush = 0;
        entry.PushToCancel = 0;
        entry.BlockShift = shift;

        var wanted = gamePosition + entry.AppliedShift;
        if (position != wanted)
            node->SetPositionFloat(wanted.X, wanted.Y);
        entry.WrittenPosition = wanted;
    }

    // Cadre à part du texte (statuts, autres textes), ou null s'il reste dans le bloc du jeu.
    private PersonalBlock? BlockOf(Tracked entry, FlyTextGroup group) =>
        FlyTextLayout.SeparateBlock(entry.Category, group, configuration.Layout, configuration.SeparateOther);

    // Remet la taille et la place du jeu, si le nœud porte encore nos valeurs.
    private static void Restore(AtkResNode* node, Tracked entry)
    {
        if (entry.WrittenScale is { } scale && node->ScaleX == scale.X && node->ScaleY == scale.Y)
            node->SetScale(entry.GameScale.X, entry.GameScale.Y);
        var applied = entry.AppliedShift;
        if (entry.WrittenPosition is { } position && node->X == position.X && node->Y == position.Y && applied != Vector2.Zero)
            node->SetPositionFloat(position.X - applied.X, position.Y - applied.Y);
    }

    // Le jeu crée les textes d'un acteur à la fois : on retient lequel pour les textes créés pendant ce temps, et,
    // sur le personnage, on relève les poussées que causent ces créations.
    private void OnAddFlyText(
        AddonFlyText* addon, uint actorIndex, uint messageMax, NumberArrayData* numberArrayData, uint offsetNum, uint offsetNumMax,
        StringArrayData* stringArrayData, uint offsetStr, uint offsetStrMax, int unknown)
    {
        currentActor = actorIndex;
        StartTrace((AtkUnitBase*)addon, actorIndex);
        try
        {
            addHook.Original(addon, actorIndex, messageMax, numberArrayData, offsetNum, offsetNumMax, stringArrayData, offsetStr, offsetStrMax, unknown);
        }
        finally
        {
            EndTrace((AtkUnitBase*)addon);
            currentActor = null;
        }
    }

    private nint OnCreateFlyText(
        AddonFlyText* addon, int kind, int val1, int val2, CStringPointer text2, uint color, uint icon, uint damageTypeIcon, CStringPointer text1, float yOffset)
    {
        if (trace != null)
            SafeMeasure((AtkUnitBase*)addon);

        var result = createHook.Original(addon, kind, val1, val2, text2, color, icon, damageTypeIcon, text1, yOffset);
        try
        {
            var entry = Track((AtkUnitBase*)addon, result, kind);
            if (trace != null)
            {
                trace.Created.Add(entry is { Group: { } group } ? (true, BlockOf(entry, group)) : (false, null));
                if (entry != null)
                    trace.LastY[entry] = ((AtkResNode*)entry.Node)->Y;
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Could not follow a new flying text.");
        }

        return result;
    }

    // Le jeu renvoie la fiche du texte créé, rangée dans l'addon ; son premier champ est le nœud du texte.
    // On ne lit jamais une adresse inconnue : seulement la mémoire de l'addon, et seulement des nœuds de l'addon.
    private Tracked? Track(AtkUnitBase* addon, nint result, int kind)
    {
        var offset = (long)(result - (nint)addon);
        var isEntry = result != 0 && offset >= 0 && offset <= sizeof(AddonFlyText) - sizeof(nint);
        var node = isEntry ? *(nint*)result : 0;
        if (!isEntry || !IsAddonNode(addon, node))
        {
#if DEBUG
            Plugin.Log.Information("[diag] create kind={Kind} actor={Actor} result={Result:X} (not followed)", kind, currentActor?.ToString() ?? "?", result);
#endif
            if (result != 0 && !reportedUnknownResult)
                Plugin.Log.Warning("Unexpected value returned when creating a flying text: it cannot be adjusted.");
            reportedUnknownResult |= result != 0;
            return null;
        }

        // Même nœud repris pour un nouveau texte alors que nos réglages y sont encore : on remet d'abord les valeurs du jeu.
        if (tracked.TryGetValue((int)offset, out var previous) && previous.Node == node)
            Restore((AtkResNode*)node, previous);

        var entry = new Tracked
        {
            Category = FlyTextLayout.Categorize(kind, currentActor),
            Group = FlyTextLayout.PlayerGroup(currentActor),
            Node = node,
        };
#if DEBUG
        entry.Offset = (int)offset;
        var created = (AtkResNode*)node;
        Plugin.Log.Information("[diag] create kind={Kind} actor={Actor} {Category} block={Block} fiche=addon+{Offset:X} size={W}x{H} scale={S:F2}",
            kind, currentActor?.ToString() ?? "?", entry.Category,
            entry.Group is { } logGroup ? BlockOf(entry, logGroup)?.ToString() ?? "game" : "-", offset, created->Width, created->Height, created->ScaleY);
#endif
        tracked[(int)offset] = entry;

        // Réglé dès sa création : à ce moment le texte est à (0, 0) et le jeu le place ensuite en ajoutant sa
        // position à celle du texte. Décalé maintenant, il apparaît directement dans son cadre, sans passer une
        // image dans le bloc du jeu.
        Apply(addon, (AtkResNode*)node, entry, (int)offset);
        return entry;
    }

    // Textes du personnage : hauteur de chaque texte de leur bloc avant toute création.
    private void StartTrace(AtkUnitBase* addon, uint actor)
    {
        trace = FlyTextLayout.PlayerGroup(actor) is { } group ? new PushTrace(group) : null;
        if (trace != null)
            SafeMeasure(addon);
    }

    // Fin des créations : chaque poussée relevée est attribuée au texte qui l'a causée. Venue d'un texte d'un autre
    // cadre, elle sera annulée à la mise à jour suivante.
    private void EndTrace(AtkUnitBase* addon)
    {
        if (trace is not { } current)
            return;

        try
        {
            Measure(addon, current);
            var pushesBeforeCreating = current.Moves.Any(move => move.Slice == 0);
            foreach (var (entry, moved, slice) in current.Moves)
            {
                entry.SeenPush += moved;
                var cause = FlyTextLayout.PushCause(slice, current.Created.Count, pushesBeforeCreating);
                if (cause >= 0 && current.Created[cause] is { Tracked: true } creation && creation.Block != BlockOf(entry, current.Group))
                    entry.PushToCancel += moved;
            }

            foreach (var (followed, block) in current.Created)
            {
                if (followed)
                    createdBlocks[(int)current.Group].Add(block);
            }

            LogTrace(addon, current, pushesBeforeCreating);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Could not follow the flying text pushed by a new one.");
        }
        finally
        {
            trace = null;
        }
    }

    private void SafeMeasure(AtkUnitBase* addon)
    {
        try
        {
            if (trace != null)
                Measure(addon, trace);
        }
        catch (Exception e)
        {
            trace = null;
            Plugin.Log.Error(e, "Could not follow the flying text pushed by a new one.");
        }
    }

    // Relève la hauteur des textes du bloc et note ceux qui ont bougé depuis le relevé précédent.
    private void Measure(AtkUnitBase* addon, PushTrace current)
    {
        CollectLiveNodes(addon);
        var slice = current.Created.Count;
        foreach (var (offset, entry) in tracked)
        {
            if (entry.Group != current.Group)
                continue;

            var node = LiveNode(addon, offset, entry);
            if (node == null)
                continue;

            var y = node->Y;
            if (current.LastY.TryGetValue(entry, out var last) && y != last)
                current.Moves.Add((entry, y - last, slice));
            current.LastY[entry] = y;
        }
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

    // Relevé (version de développement) : les premières images de chaque texte du personnage, et chaque poussée annulée,
    // avec le contenu de sa fiche pour trouver où le jeu range sa propre position.
    [System.Diagnostics.Conditional("DEBUG")]
    private static void Log(AtkUnitBase* addon, string phase, Vector2 position, Tracked entry, float cancel)
    {
#if DEBUG
        if (entry.Logged >= 120 || (entry.Logged >= 30 && phase != "Replaced" && cancel == 0))
            return;

        entry.Logged++;
        Plugin.Log.Information("[diag] node addon+{Offset:X} {Category} {Phase} pos=({X:F1},{Y:F1}) shift=({SX:F0},{SY:F0}) cancelled={Cancelled:F1} cancel={Cancel:F1} seen={Seen:F1} step={Step:F2} fiche=[{Fiche}]",
            entry.Offset, entry.Category, phase, position.X, position.Y, entry.BlockShift.X, entry.BlockShift.Y,
            entry.Cancelled, cancel, entry.SeenPush, entry.Step, Fiche(addon, entry.Offset));
#endif
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void LogTrace(AtkUnitBase* addon, PushTrace current, bool pushesBeforeCreating)
    {
#if DEBUG
        if (current.Moves.Count == 0)
            return;

        var created = string.Join(",", current.Created.Select(c => c.Tracked ? c.Block?.ToString() ?? "game" : "?"));
        var moves = string.Join(" ", current.Moves.Select(m => $"s{m.Slice}:{m.Entry.Category}@{m.Entry.Offset:X}{m.Moved:+0.0;-0.0}"));
        Plugin.Log.Information("[diag] push group={Group} before={Before} created=[{Created}] moves=[{Moves}]",
            current.Group, pushesBeforeCreating, created, moves);
#endif
    }

#if DEBUG
    // Fiche du texte vue comme des nombres à virgule (après le pointeur du nœud), pour la mise au point.
    private static string Fiche(AtkUnitBase* addon, int offset)
    {
        const int size = 0x50;
        if (offset < 0 || offset + size > sizeof(AddonFlyText))
            return string.Empty;

        var values = (float*)((byte*)addon + offset);
        var text = new System.Text.StringBuilder();
        for (var i = 2; i < size / sizeof(float); i++)
            text.Append(values[i].ToString("0.#")).Append(' ');
        return text.ToString().TrimEnd();
    }
#endif
}
