using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text.ReadOnly;

namespace FlyingTextModifier;

/// <summary>Cadre d'un élément dans l'éditeur d'ATH du jeu : le nom que le jeu y écrit et sa place à l'écran, en pixels.</summary>
internal readonly record struct HudFrame(string Name, int X, int Y, int Width, int Height, bool Selected);

/// <summary>
/// Lit à chaque image, tant que l'éditeur d'ATH est ouvert, les cadres qu'il dessine : un par élément, y compris les
/// éléments masqués en jeu (cible, cible focalisée…). Lecture seule : rien n'est écrit dans le jeu.
/// </summary>
internal sealed unsafe class HudFrames : IDisposable
{
    // Garde-fous du parcours des nœuds de l'éditeur.
    private const int MaxNodes = 4000;
    private const int MaxDepth = 8;

    private readonly object sync = new();
    private HudFrame[] frames = [];

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
        var read = Read();
        lock (sync)
            frames = read;
    }

    private static HudFrame[] Read()
    {
        var screen = (AddonHudLayoutScreen*)Plugin.GameGui.GetAddonByName(Plugin.HudLayoutAddonName).Address;
        if (screen == null || !screen->IsVisible || screen->RootNode == null)
            return [];

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
                    found.Add(new HudFrame(
                        name,
                        (int)MathF.Round(node->ScreenX),
                        (int)MathF.Round(node->ScreenY),
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
