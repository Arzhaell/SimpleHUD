using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;

namespace FlyingTextModifier;

/// <summary>
/// Dans l'éditeur d'ATH du jeu : la position de l'élément survolé (de tous, si on le demande), et sous l'élément
/// sélectionné un panneau pour le régler au pixel.
/// </summary>
internal sealed class HudEditorOverlay : IDisposable
{
    private static readonly Vector4 Text = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 Background = new(0.05f, 0.08f, 0.15f, 0.92f);
    private static readonly Vector4 Border = new(0.79f, 0.84f, 0.94f, 1f);

    private const ImGuiWindowFlags PanelFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize
        | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
        | ImGuiWindowFlags.NoMove;

    private readonly Plugin plugin;

    // Étiquette de l'élément survolé : police du jeu, plus grande et nette.
    private readonly IFontHandle largeFont;

    // Taille du panneau à l'image précédente, pour le placer avant de le dessiner.
    private Vector2 panelSize = new(320, 90);

    public HudEditorOverlay(Plugin plugin)
    {
        this.plugin = plugin;
        largeFont = Plugin.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Axis18));
    }

    public void Dispose() => largeFont.Dispose();

    public void Draw()
    {
        var frames = plugin.Hud.Frames;
        if (frames.Length == 0)
            return;

        var viewport = ImGuiHelpers.MainViewport;

        // Pas d'étiquette de survol quand la souris est sur une fenêtre du plugin (panneau, cadres des textes défilants).
        var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow)
            ? -1
            : HudLayout.FrameAt(frames, ImGui.GetIO().MousePos - viewport.Pos);

        // Sous les fenêtres du plugin, par-dessus le jeu. L'élément sélectionné a son panneau à la place.
        var drawList = ImGui.GetBackgroundDrawList();
        if (plugin.Configuration.ShowAllHudPositions)
        {
            for (var i = 0; i < frames.Length; i++)
            {
                if (i != hovered && !frames[i].Selected)
                    DrawLabel(drawList, frames[i], false);
            }
        }

        if (hovered >= 0 && !frames[hovered].Selected)
        {
            using (largeFont.Push())
                DrawLabel(drawList, frames[hovered], true);
        }

        var selected = Array.FindIndex(frames, frame => frame.Selected);
        if (selected >= 0)
            DrawPanel(frames[selected], viewport.Pos, viewport.Size);
    }

    // Position du coin haut-gauche, juste au-dessus du cadre ; encadrée pour l'élément survolé.
    private static void DrawLabel(ImDrawListPtr drawList, HudFrame frame, bool emphasized)
    {
        var viewport = ImGuiHelpers.MainViewport;
        var scale = ImGuiHelpers.GlobalScale;
        var padding = new Vector2(6f, 2f) * scale;
        var text = HudLayout.Coordinates(frame.X, frame.Y);
        var size = ImGui.CalcTextSize(text) + (padding * 2);
        var frameMin = viewport.Pos + new Vector2(frame.X, frame.Y);
        var min = HudLayout.LabelMin(frameMin, new Vector2(frame.Width, frame.Height), size, viewport.Pos, viewport.Size);
        var max = min + size;
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(Background), 4f * scale);
        if (emphasized)
            drawList.AddRect(min, max, ImGui.GetColorU32(Border), 4f * scale, ImDrawFlags.None, 1.5f * scale);
        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), min + padding, ImGui.GetColorU32(Text), text);
    }

    // Nom de l'élément, X et Y au pixel (boutons − / +, Ctrl+clic par 10). Le déplacement passe par l'éditeur :
    // son bouton « Sauvegarder » le garde.
    private void DrawPanel(HudFrame frame, Vector2 origin, Vector2 screen)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var frameMin = origin + new Vector2(frame.X, frame.Y);
        ImGui.SetNextWindowPos(HudLayout.PanelMin(frameMin, new Vector2(frame.Width, frame.Height), panelSize, origin, screen, 6f * scale));

        ImGui.PushStyleColor(ImGuiCol.WindowBg, Background);
        ImGui.PushStyleColor(ImGuiCol.Border, PlacementOverlay.Accent);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.5f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 8f) * scale);
        if (ImGui.Begin("##FlyingTextModifierHudPanel", PanelFlags))
        {
            ImGui.TextColored(PlacementOverlay.Accent, frame.Name);

            var width = 130 * scale;
            var x = frame.X;
            var y = frame.Y;
            ImGui.SetNextItemWidth(width);
            var edited = ImGui.InputInt("X##HudX", ref x, 1, 10);
            ImGui.SameLine(0, 14f * scale);
            ImGui.SetNextItemWidth(width);
            edited |= ImGui.InputInt("Y##HudY", ref y, 1, 10);
            if (edited)
            {
                var target = HudLayout.ClampFrame(new Vector2(x, y), new Vector2(frame.Width, frame.Height), screen);
                plugin.Hud.MoveSelected(frame.Name, (int)target.X, (int)target.Y);
            }

            ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.T(
                "Ctrl+click − / +: 10 px · the editor's \"Save\" keeps it",
                "Ctrl+clic sur − / + : 10 px · « Sauvegarder » dans l'éditeur la garde"));
            panelSize = ImGui.GetWindowSize();
        }

        ImGui.End();
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);
    }
}
