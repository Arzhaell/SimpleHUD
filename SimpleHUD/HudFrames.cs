using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text.ReadOnly;

namespace FlyingTextModifier;

/// <summary>Cadre d'un élément dans l'éditeur d'ATH du jeu : le nom que le jeu y écrit et sa place à l'écran, en pixels.</summary>
internal readonly record struct HudFrame(string Name, int X, int Y, int Width, int Height, bool Selected);

/// <summary>
/// Cadres que dessine l'éditeur d'ATH du jeu : un par élément, y compris les éléments masqués en jeu (cible, cible
/// focalisée…), lus à chaque image tant que l'éditeur est ouvert. Seul l'élément sélectionné peut être déplacé, et
/// seulement comme le ferait un glissé à la souris : c'est le bouton « Sauvegarder » de l'éditeur qui le garde.
/// </summary>
internal sealed unsafe class HudFrames : IDisposable
{
    // Garde-fous du parcours des nœuds de l'éditeur.
    private const int MaxNodes = 4000;
    private const int MaxDepth = 8;

    private readonly object sync = new();
    private HudFrame[] frames = [];

    // Déplacement demandé dans la fenêtre, fait à la mise à jour suivante du jeu.
    private (string Name, Vector2 Target)? pendingMove;

    public HudFrames() => Plugin.Framework.Update += OnFrameworkUpdate;

    /// <summary>Cadres affichés par l'éditeur (vide quand il est fermé).</summary>
    public HudFrame[] Frames
    {
        get
        {
            lock (sync)
                return frames;
        }
    }

    public void Dispose() => Plugin.Framework.Update -= OnFrameworkUpdate;

    /// <summary>Amène le coin haut-gauche du cadre de l'élément sélectionné (s'il s'appelle toujours ainsi) en X, Y.</summary>
    public void MoveSelected(string name, int x, int y)
    {
        lock (sync)
            pendingMove = (name, new Vector2(x, y));
    }

    /// <summary>Texte affiché par un composant de l'interface (son premier texte non vide), sans mise en forme.</summary>
    public static string TextOf(AtkComponentBase* component)
    {
        if (component == null)
            return string.Empty;

        var manager = &component->UldManager;
        for (var i = 0; i < manager->NodeListCount; i++)
        {
            var node = manager->NodeList[i];
            if (node == null || node->Type != NodeType.Text)
                continue;

            var text = new ReadOnlySeStringSpan(((AtkTextNode*)node)->NodeText.AsSpan()).ExtractText();
            if (text.Length > 0)
                return text;
        }

        return string.Empty;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        (string Name, Vector2 Target)? move;
        lock (sync)
        {
            move = pendingMove;
            pendingMove = null;
        }

        var screen = (AddonHudLayoutScreen*)Plugin.GameGui.GetAddonByName(Plugin.HudLayoutAddonName).Address;
        var open = screen != null && screen->IsVisible && screen->RootNode != null;
        if (open && move is { } request)
            Move(screen, request.Name, request.Target);

        var read = open ? Read(screen) : [];
        lock (sync)
            frames = read;
    }

    // Coin haut-gauche d'un cadre : sa place dans le calque de l'éditeur, qui couvre tout l'écran. La place à l'écran
    // calculée par le jeu (ScreenX) n'est à jour qu'à l'image suivante : un déplacement se verrait en retard.
    private static Vector2 FramePosition(AtkResNode* node)
    {
        var parent = node->ParentNode;
        var origin = parent == null ? Vector2.Zero : new Vector2(parent->ScreenX, parent->ScreenY);
        return origin + new Vector2(node->X, node->Y);
    }

    // Le cadre de la sélection et l'élément bougent ensemble, comme pendant un glissé ; l'éditeur sait alors qu'il y a
    // une position à enregistrer. Les pointeurs sont relus juste avant d'écrire : l'élément sélectionné a pu changer
    // depuis la saisie.
    private static void Move(AddonHudLayoutScreen* screen, string name, Vector2 target)
    {
        var info = screen->SelectedAddon;
        var overlay = screen->SelectedOverlayNode;
        if (info == null || info->SelectedAtkUnit == null || overlay == null || TextOf(overlay->Component) != name)
            return;

        var unit = info->SelectedAtkUnit;
        var current = FramePosition((AtkResNode*)overlay);
        var delta = HudLayout.MoveDelta(current, target);
        if (delta == Vector2.Zero)
            return;

#if DEBUG
        var unitFrom = new Vector2(unit->X, unit->Y);
#endif
        overlay->SetPositionFloat(overlay->X + delta.X, overlay->Y + delta.Y);
        unit->SetPosition((short)(unit->X + delta.X), (short)(unit->Y + delta.Y));
        info->PositionHasChanged = 1;
        var agent = AgentHUDLayout.Instance();
        if (agent != null)
            agent->NeedToSave = true;

#if DEBUG
        Plugin.Log.Information(
            "[hud] move {Name} frame ({FromX},{FromY}) -> ({ToX},{ToY}) unit ({UnitX},{UnitY}) -> ({NewX},{NewY})",
            name, current.X, current.Y, target.X, target.Y, unitFrom.X, unitFrom.Y, unit->X, unit->Y);
#endif
    }

    private static HudFrame[] Read(AddonHudLayoutScreen* screen)
    {
        var found = new List<HudFrame>();
        var visited = 0;
        Visit(screen->RootNode, 0, screen->SelectedOverlayNode, found, ref visited);
        return HudLayout.Merge(found);
    }

    // Parcourt les nœuds visibles : chaque composant qui affiche un nom est le cadre d'un élément.
    private static void Visit(AtkResNode* node, int depth, AtkComponentNode* selected, List<HudFrame> found, ref int visited)
    {
        for (; node != null && visited < MaxNodes; node = node->PrevSiblingNode)
        {
            visited++;
            if (!node->IsVisible())
                continue;

            if ((ushort)node->Type >= 1000)
            {
                var component = (AtkComponentNode*)node;
                var name = TextOf(component->Component);
                if (name.Length > 0)
                {
                    var position = FramePosition(node);
                    found.Add(new HudFrame(
                        name,
                        (int)MathF.Round(position.X),
                        (int)MathF.Round(position.Y),
                        (int)MathF.Round(node->Width * node->ScaleX),
                        (int)MathF.Round(node->Height * node->ScaleY),
                        component == selected));
                }

                continue;
            }

            if (depth < MaxDepth)
                Visit(node->ChildNode, depth + 1, selected, found, ref visited);
        }
    }
}
