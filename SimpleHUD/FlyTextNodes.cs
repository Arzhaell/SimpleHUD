using System;
using System.Collections.Generic;
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
/// Constaté en jeu : le jeu empile les textes d'un bloc d'après la place de leurs nœuds, et le revérifie à chaque
/// image (un texte plus ancien doit rester sous le plus récent). Le nœud d'un texte reste donc là où le jeu le met :
/// pour l'afficher dans un autre cadre, on décale son contenu (texte et icône). Quand un texte arrive, le jeu pousse vers
/// le bas ceux de son bloc ; venue d'un texte d'un autre cadre, cette poussée est retirée de l'affichage.
/// </summary>
internal sealed unsafe class FlyTextNodes : IDisposable
{
    private const string AddonName = "_FlyText";

    // Au-delà, un mouvement d'une image n'est pas le défilement habituel (en pixels).
    private const float MaxStep = 8f;

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

        // Contenu du texte, décalé pour l'afficher ailleurs que son nœud.
        public readonly List<Content> Contents = [];

        // Place du nœud à la dernière mise à jour, et défilement de la dernière image sans création dans son bloc.
        public Vector2 LastPosition;
        public float Step;

        // Total des poussées du jeu retirées de l'affichage (en pixels ; négatif : vers le haut).
        public float Cancelled;

        // Vrai une fois que le jeu a placé le texte (son premier mouvement après la création).
        public bool Placed;

#if DEBUG
        // Relevé : emplacement de la fiche et nombre de lignes déjà notées pour ce texte.
        public int Offset;
        public int Logged;
#endif
    }

    // Nœud du contenu d'un texte : place que lui donne le jeu, et dernière place écrite.
    private sealed class Content
    {
        public nint Node;
        public Vector2 Game;
        public Vector2? Written;
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

    // Après chaque mise à jour du jeu : taille du jeu × taille de la famille, contenu décalé vers son cadre à part,
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
                // La fiche désigne un autre nœud : le texte est terminé. On remet son contenu en place (s'il porte
                // encore nos valeurs) avant que le jeu ne reprenne le nœud pour un autre texte.
                if (!StillTracks(addon, offset, entry))
                {
                    if (liveNodes.Contains(entry.Node))
                        Restore((AtkResNode*)entry.Node, entry);
                    stale.Add(offset);
                    continue;
                }

                // Nœud absent de la liste de l'addon (texte en attente, caché…) : on n'y écrit pas, mais on le garde.
                var node = LiveNode(addon, offset, entry);
                if (node != null)
                    Apply(node, entry);
            }

            foreach (var offset in stale)
                tracked.Remove(offset);
        }

        foreach (var blocks in createdBlocks)
            blocks.Clear();
    }

    private void Apply(AtkResNode* node, Tracked entry)
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

        // Le jeu fait défiler le texte ; quand un texte arrive dans son bloc, il le pousse en plus vers le bas. Ce qui
        // dépasse le défilement habituel est une poussée, retirée de l'affichage si elle vient d'un texte d'un autre
        // cadre. Le premier mouvement après la création est le placement du texte par le jeu, pas une poussée.
        var block = BlockOf(entry, group);
        var created = createdBlocks[(int)group];
        var position = new Vector2(node->X, node->Y);
        var cancel = 0f;
        if (entry.Placed)
        {
            var moved = position.Y - entry.LastPosition.Y;
            if (created.Count == 0)
            {
                if (moved >= 0 && moved <= MaxStep)
                    entry.Step = moved;
            }
            else if (!created.Contains(block))
            {
                cancel = FlyTextLayout.GamePush(moved, entry.Step);
            }
        }

        entry.Placed |= position != entry.LastPosition;
        entry.LastPosition = position;
        entry.Cancelled -= cancel;

        var shift = block is { } separate ? groups.SeparateShift(separate, group) : Vector2.Zero;
        MoveContents(entry, shift + new Vector2(0, entry.Cancelled), wantedScale);
        Log(position, entry, cancel);
    }

    // Décale le contenu du texte (en pixels à l'écran). Il est dessiné à l'échelle du nœud du texte : l'écart y est
    // donc divisé par sa taille. Un contenu que le jeu a replacé reprend sa nouvelle place, plus l'écart.
    private static void MoveContents(Tracked entry, Vector2 shift, Vector2 scale)
    {
        var local = new Vector2(scale.X != 0 ? shift.X / scale.X : 0, scale.Y != 0 ? shift.Y / scale.Y : 0);
        foreach (var content in entry.Contents)
        {
            var node = (AtkResNode*)content.Node;
            var current = new Vector2(node->X, node->Y);
            if (content.Written != current)
                content.Game = current;

            // Contenu jamais décalé et rien à décaler : on n'y touche pas.
            if (content.Written == null && local == Vector2.Zero)
                continue;

            var wanted = content.Game + local;
            if (current != wanted)
                node->SetPositionFloat(wanted.X, wanted.Y);
            content.Written = wanted;
        }
    }

    // Cadre à part du texte (statuts, autres textes), ou null s'il reste dans le bloc du jeu.
    private PersonalBlock? BlockOf(Tracked entry, FlyTextGroup group) =>
        FlyTextLayout.SeparateBlock(entry.Category, group, configuration.Layout, configuration.SeparateOther);

    // Remet la taille et la place du jeu, là où le nœud et son contenu portent encore nos valeurs.
    private static void Restore(AtkResNode* node, Tracked entry)
    {
        if (entry.WrittenScale is { } scale && node->ScaleX == scale.X && node->ScaleY == scale.Y)
            node->SetScale(entry.GameScale.X, entry.GameScale.Y);

        foreach (var content in entry.Contents)
        {
            var contentNode = (AtkResNode*)content.Node;
            if (content.Written is { } written && contentNode->X == written.X && contentNode->Y == written.Y)
                contentNode->SetPositionFloat(content.Game.X, content.Game.Y);
        }
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
        if (!isEntry || !IsAddonNode(addon, node))
        {
#if DEBUG
            Plugin.Log.Information("[diag] create kind={Kind} actor={Actor} result={Result:X} (not followed)", kind, currentActor?.ToString() ?? "?", result);
#endif
            if (result != 0 && !reportedUnknownResult)
                Plugin.Log.Warning("Unexpected value returned when creating a flying text: it cannot be adjusted.");
            reportedUnknownResult |= result != 0;
            return;
        }

        // Nœud repris pour un nouveau texte alors que nos réglages y sont encore : on remet d'abord les valeurs du jeu.
        ForgetNode(node);

        var entry = new Tracked
        {
            Category = FlyTextLayout.Categorize(kind, currentActor),
            Group = FlyTextLayout.PlayerGroup(currentActor),
            Node = node,
        };
        FindContents((AtkResNode*)node, entry.Contents);
        tracked[(int)offset] = entry;
        if (entry.Group is { } group)
            createdBlocks[(int)group].Add(BlockOf(entry, group));
#if DEBUG
        entry.Offset = (int)offset;
        var created = (AtkResNode*)node;
        Plugin.Log.Information("[diag] create kind={Kind} actor={Actor} {Category} block={Block} fiche=addon+{Offset:X} type={Type} size={W}x{H} scale={S:F2} contents={Contents}",
            kind, currentActor?.ToString() ?? "?", entry.Category,
            entry.Group is { } logGroup ? BlockOf(entry, logGroup)?.ToString() ?? "game" : "-", offset, (int)created->Type,
            created->Width, created->Height, created->ScaleY, DescribeContents(entry));
#endif

        // Réglé dès sa création : le contenu apparaît directement dans son cadre, sans passer une image dans le
        // bloc du jeu.
        Apply((AtkResNode*)node, entry);
    }

    private void ForgetNode(nint node)
    {
        stale.Clear();
        foreach (var (offset, entry) in tracked)
        {
            if (entry.Node != node)
                continue;

            Restore((AtkResNode*)node, entry);
            stale.Add(offset);
        }

        foreach (var offset in stale)
            tracked.Remove(offset);
    }

    // Contenu d'un texte : le nœud racine de son composant, sinon ses nœuds enfants.
    private static void FindContents(AtkResNode* node, List<Content> contents)
    {
        if (node->Type == NodeType.Component)
        {
            var component = ((AtkComponentNode*)node)->Component;
            if (component != null && component->UldManager.RootNode != null)
                contents.Add(new Content { Node = (nint)component->UldManager.RootNode });
            return;
        }

        // Enfants reliés dans les deux sens à partir du premier : on les parcourt tous, sans jamais boucler.
        var seen = new HashSet<nint>();
        for (var child = node->ChildNode; child != null && seen.Count < 32 && seen.Add((nint)child); child = child->PrevSiblingNode)
            contents.Add(new Content { Node = (nint)child });
        for (var child = node->ChildNode == null ? null : node->ChildNode->NextSiblingNode; child != null && seen.Count < 32 && seen.Add((nint)child); child = child->NextSiblingNode)
            contents.Add(new Content { Node = (nint)child });
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

    // Relevé (version de développement) : les premières images de chaque texte du personnage, et chaque poussée retirée.
    [System.Diagnostics.Conditional("DEBUG")]
    private static void Log(Vector2 position, Tracked entry, float cancel)
    {
#if DEBUG
        if (entry.Logged >= 120 || (entry.Logged >= 30 && cancel == 0))
            return;

        entry.Logged++;
        Plugin.Log.Information("[diag] node addon+{Offset:X} {Category} pos=({X:F1},{Y:F1}) step={Step:F2} cancel={Cancel:F1} cancelled={Cancelled:F1} contents={Contents}",
            entry.Offset, entry.Category, position.X, position.Y, entry.Step, cancel, entry.Cancelled, DescribeContents(entry));
#endif
    }

#if DEBUG
    private static string DescribeContents(Tracked entry)
    {
        var text = new System.Text.StringBuilder();
        foreach (var content in entry.Contents)
        {
            var node = (AtkResNode*)content.Node;
            text.Append($"[t{(int)node->Type} ({node->X:F1},{node->Y:F1}) game=({content.Game.X:F1},{content.Game.Y:F1})");
            text.Append(content.Written is { } written ? $" written=({written.X:F1},{written.Y:F1})]" : "]");
        }

        return text.ToString();
    }
#endif
}
