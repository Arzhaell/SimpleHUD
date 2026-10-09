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
/// textes du personnage qui ont leur propre cadre (statuts, autres textes) et empilement de chaque cadre à part. Chaque
/// texte est repéré au moment où le jeu le crée (son type et le personnage qui le reçoit sont alors connus), puis ajusté
/// après chaque mise à jour du jeu (qui a lieu à chaque image).
/// Constaté en jeu : le jeu empile les textes d'un bloc d'après la place de leurs nœuds, et le revérifie à chaque
/// image (un texte plus ancien doit rester sous le plus récent). Le nœud d'un texte reste donc là où le jeu le met :
/// pour l'afficher ailleurs, on décale son contenu (texte et icône). Quand des textes arrivent, le jeu pousse vers le bas
/// les plus anciens de son bloc ; le plugin retire cette poussée et refait l'empilement de chaque cadre avec ses seuls
/// textes, d'après les écarts mesurés sur l'empilement du jeu.
/// </summary>
internal sealed unsafe class FlyTextNodes : IDisposable
{
    private const string AddonName = "_FlyText";

    // Au-delà, un mouvement d'une image n'est pas le défilement habituel (en pixels).
    private const float MaxStep = 8f;

    // Écart sous un modèle de texte jamais mesuré : sa hauteur, moins le chevauchement que laisse le jeu (constaté :
    // une dizaine de pixels).
    private const float GapOverlap = 10f;

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

    // Mesures faites sur l'empilement du jeu, par modèle de texte (type de son nœud) : écart qu'il laisse sous lui (à
    // la taille 1), et son point de départ par rapport à son groupe.
    private readonly Dictionary<ushort, float> gaps = [];
    private readonly Dictionary<ushort, float> starts = [];

    // Textes du personnage vus à cette mise à jour, par bloc du jeu, et ceux qui viennent d'arriver.
    private readonly List<Tracked>[] frame = [[], []];
    private readonly List<Tracked> arrivals = [];

    private sealed class Tracked
    {
        public FlyTextCategory Category;

        // Bloc du jeu sur le personnage (null : texte sur un autre personnage, seule sa taille change).
        public FlyTextGroup? Group;
        public nint Node;

        // Modèle du texte (type de son nœud) et hauteur de son nœud.
        public ushort Model;
        public float Height;

        // Taille du jeu et dernière taille écrite.
        public Vector2 GameScale;
        public Vector2? WrittenScale;

        // Contenu du texte, décalé pour l'afficher ailleurs que son nœud.
        public readonly List<Content> Contents = [];

        // Place du nœud à la mise à jour précédente, et défilement de la dernière image sans arrivée dans son bloc.
        public Vector2 LastPosition;
        public float Step;

        // Vrai une fois que le jeu a placé le texte (son premier mouvement après la création).
        public bool Placed;

        // Hauteur où le texte est affiché, dans le calque des textes et avant l'écart de son cadre (null tant qu'il
        // n'est pas placé).
        public float? Display;

        // Pendant une mise à jour : cadre, hauteur du nœud, mouvement depuis la mise à jour précédente, vient d'arriver.
        public PersonalBlock? Block;
        public float NaturalY;
        public float Moved;
        public bool Arrived;

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

    // Après chaque mise à jour du jeu : taille du jeu × taille de la famille ; sur le personnage, empilement de chaque
    // cadre et contenu décalé vers son cadre.
    private void OnAddonPostUpdate(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || tracked.Count == 0)
            return;

        CollectLiveNodes(addon);
        stale.Clear();
        foreach (var texts in frame)
            texts.Clear();

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
            if (node == null)
                continue;

            ApplyScale(node, entry);
            if (entry.Group is { } group)
            {
                Observe(node, entry, group);
                frame[(int)group].Add(entry);
            }
        }

        foreach (var offset in stale)
            tracked.Remove(offset);

        foreach (var group in FlyTextLayout.Groups)
        {
            var texts = frame[(int)group];
            Stack(group, texts);
            foreach (var entry in texts)
                MoveContents(entry, group);
        }
    }

    // Taille différente de celle qu'on a écrite : le jeu vient de la changer (création, rebond).
    private void ApplyScale(AtkResNode* node, Tracked entry)
    {
        var scale = new Vector2(node->ScaleX, node->ScaleY);
        if (entry.WrittenScale != scale)
            entry.GameScale = scale;

        var wanted = entry.GameScale * configuration.GetScale(entry.Category);
        if (scale != wanted)
            node->SetScale(wanted.X, wanted.Y);
        entry.WrittenScale = wanted;
    }

    // Ce que le jeu a fait du texte depuis la mise à jour précédente. Son premier mouvement après la création est son
    // placement par le jeu : il vient d'arriver.
    private void Observe(AtkResNode* node, Tracked entry, FlyTextGroup group)
    {
        var position = new Vector2(node->X, node->Y);
        entry.Block = BlockOf(entry, group);
        entry.NaturalY = position.Y;
        entry.Arrived = !entry.Placed && position != entry.LastPosition;
        entry.Moved = entry.Placed ? position.Y - entry.LastPosition.Y : 0f;
        entry.Placed |= entry.Arrived;
        entry.LastPosition = position;

        // Constaté en jeu : à la création, le contenu n'est pas toujours en place ; on le cherche à chaque image.
        if (entry.Contents.Count == 0)
            FindContents(node, entry.Contents);
    }

    // Hauteur d'affichage des textes d'un bloc du jeu. Sans cadre à part dans ce bloc, c'est celle du jeu. Sinon, les
    // textes défilent comme dans le jeu, sans ses poussées ; à chaque arrivée, chaque cadre est empilé avec ses seuls
    // textes, aux écarts mesurés sur l'empilement du jeu.
    private void Stack(FlyTextGroup group, List<Tracked> texts)
    {
        var separate = FlyTextLayout.HasSeparateBlocks(group, configuration.Layout, configuration.SeparateOther);
        var arriving = texts.Any(entry => entry.Arrived);

        float? pushedBelow = null;
        foreach (var entry in texts)
        {
            if (!entry.Placed || entry.Arrived)
                continue;

            var push = arriving ? FlyTextLayout.GamePush(entry.Moved, entry.Step) : 0f;
            if (!arriving && entry.Moved >= 0 && entry.Moved <= MaxStep)
                entry.Step = entry.Moved;
            if (push > 0)
                pushedBelow = MathF.Min(pushedBelow ?? float.MaxValue, entry.NaturalY);

            entry.Display = separate && entry.Display is { } display ? display + entry.Moved - push : entry.NaturalY;
        }

        arrivals.Clear();
        arrivals.AddRange(texts.Where(entry => entry.Arrived).OrderBy(entry => entry.NaturalY));
        if (arrivals.Count == 0)
            return;

        // Écarts mesurés sur l'empilement du jeu (gardés par modèle pour les fois où ils ne se voient pas), et point de
        // départ du texte du haut, qui n'a pas été poussé.
        var measured = FlyTextLayout.MeasuredGaps(arrivals.Select(entry => entry.NaturalY).ToArray(), pushedBelow);
        for (var i = 0; i < arrivals.Count; i++)
        {
            if (measured[i] is { } gap && ScaleY(arrivals[i]) > 0)
                gaps[arrivals[i].Model] = gap / ScaleY(arrivals[i]);
        }

        var layer = groups.LayerY(group);
        if (layer is { } layerY)
            starts[arrivals[0].Model] = arrivals[0].NaturalY - layerY;

        if (!separate)
        {
            foreach (var entry in arrivals)
                entry.Display = entry.NaturalY;
            return;
        }

        foreach (var block in arrivals.Select(entry => entry.Block).Distinct().ToList())
        {
            var arrived = new List<float>();
            var indexes = new List<int>();
            for (var i = 0; i < arrivals.Count; i++)
            {
                if (arrivals[i].Block != block)
                    continue;
                indexes.Add(i);
                arrived.Add(measured[i] ?? Gap(arrivals[i]));
            }

            var first = arrivals[indexes[0]];
            var start = layer is { } y && starts.TryGetValue(first.Model, out var offset) ? y + offset : arrivals[0].NaturalY;
            var newestOlder = texts.Where(entry => entry.Placed && !entry.Arrived && entry.Block == block && entry.Display != null)
                .Select(entry => entry.Display!.Value)
                .DefaultIfEmpty(float.NaN)
                .Min();

            var places = new float[arrived.Count];
            var push = FlyTextLayout.StackArrivals(start, arrived, float.IsNaN(newestOlder) ? null : newestOlder, places);
            for (var i = 0; i < indexes.Count; i++)
                arrivals[indexes[i]].Display = places[i];

            if (push > 0)
            {
                foreach (var entry in texts)
                {
                    if (entry.Placed && !entry.Arrived && entry.Block == block && entry.Display is { } display)
                        entry.Display = display + push;
                }
            }

            LogStack(group, block, start, arrived, push);
        }
    }

    private static float ScaleY(Tracked entry) => entry.WrittenScale?.Y ?? 1f;

    // Écart sous un texte : mesuré auparavant sur ce modèle, sinon estimé d'après sa hauteur.
    private float Gap(Tracked entry) => gaps.TryGetValue(entry.Model, out var gap)
        ? gap * ScaleY(entry)
        : MathF.Max(0f, (entry.Height * ScaleY(entry)) - GapOverlap);

    // Décale le contenu du texte vers son cadre et à sa hauteur d'affichage (en pixels à l'écran). Il est dessiné à
    // l'échelle du nœud du texte : l'écart y est donc divisé par sa taille. Un contenu que le jeu a replacé reprend sa
    // nouvelle place, plus l'écart.
    private void MoveContents(Tracked entry, FlyTextGroup group)
    {
        var shift = entry.Block is { } separate ? groups.SeparateShift(separate, group) : Vector2.Zero;
        if (entry.Display is { } display)
            shift.Y += display - entry.NaturalY;
        MoveContents(entry, shift);
        Log(entry, shift);
    }

    private static void MoveContents(Tracked entry, Vector2 shift)
    {
        var scale = entry.WrittenScale ?? Vector2.One;
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

        var created = (AtkResNode*)node;
        var entry = new Tracked
        {
            Category = FlyTextLayout.Categorize(kind, currentActor),
            Group = FlyTextLayout.PlayerGroup(currentActor),
            Node = node,
            Model = (ushort)created->Type,
            Height = created->Height,
        };
        FindContents(created, entry.Contents);
        tracked[(int)offset] = entry;
#if DEBUG
        entry.Offset = (int)offset;
        Plugin.Log.Information("[diag] create kind={Kind} actor={Actor} {Category} block={Block} fiche=addon+{Offset:X} model={Model} size={W}x{H} contents={Contents}",
            kind, currentActor?.ToString() ?? "?", entry.Category,
            entry.Group is { } logGroup ? BlockOf(entry, logGroup)?.ToString() ?? "game" : "-", offset, entry.Model,
            created->Width, created->Height, entry.Contents.Count);
#endif

        // Réglé dès sa création : le contenu apparaît directement dans son cadre, sans passer une image dans le
        // bloc du jeu.
        ApplyScale(created, entry);
        if (entry.Group is { } group)
        {
            entry.Block = BlockOf(entry, group);
            MoveContents(entry, group);
        }
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

    // Constaté en jeu : chaque texte est un composant, de type 1000 + numéro de son modèle (1017 à 1034 selon le texte).
    private static bool IsComponent(AtkResNode* node) => (ushort)node->Type >= 1000;

    // Contenu d'un texte : le nœud racine de son composant, sinon ses nœuds enfants.
    private static void FindContents(AtkResNode* node, List<Content> contents)
    {
        if (IsComponent(node))
        {
            var component = ((AtkComponentNode*)node)->Component;
            var root = component != null ? component->UldManager.RootNode : null;
            if (root == null)
                root = node->ChildNode;
            if (root != null)
                contents.Add(new Content { Node = (nint)root });
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

    // Relevé (version de développement) : les premières images de chaque texte du personnage.
    [System.Diagnostics.Conditional("DEBUG")]
    private static void Log(Tracked entry, Vector2 shift)
    {
#if DEBUG
        if (entry.Logged >= 30)
            return;

        entry.Logged++;
        Plugin.Log.Information("[diag] node addon+{Offset:X} {Category} {Block} y={Y:F1} moved={Moved:F2} step={Step:F2} display={Display} shift=({SX:F1},{SY:F1}) arrived={Arrived}",
            entry.Offset, entry.Category, entry.Block?.ToString() ?? "game", entry.NaturalY, entry.Moved, entry.Step,
            entry.Display?.ToString("F1") ?? "-", shift.X, shift.Y, entry.Arrived);
#endif
    }

    // Relevé (version de développement) : chaque empilement d'un cadre à l'arrivée de textes.
    [System.Diagnostics.Conditional("DEBUG")]
    private void LogStack(FlyTextGroup group, PersonalBlock? block, float start, List<float> arrived, float push)
    {
#if DEBUG
        Plugin.Log.Information("[diag] stack {Group} {Block} start={Start:F1} gaps=[{Gaps}] push={Push:F1} learned=[{Learned}]",
            group, block?.ToString() ?? "game", start, string.Join(" ", arrived.Select(gap => gap.ToString("F1"))), push,
            string.Join(" ", gaps.Select(pair => $"{pair.Key}:{pair.Value:F1}")));
#endif
    }
}
