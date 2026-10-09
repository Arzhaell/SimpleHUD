using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace FlyingTextModifier;

/// <summary>Dans l'éditeur d'ATH du jeu : au-dessus du cadre de chaque élément, la position de son coin haut-gauche.</summary>
internal sealed class HudLabels
{
    private static readonly Vector4 Text = new(0.85f, 0.9f, 1f, 1f);
    private static readonly Vector4 Background = new(0.05f, 0.08f, 0.15f, 0.75f);

    private readonly Plugin plugin;

    public HudLabels(Plugin plugin) => this.plugin = plugin;

    public void Draw()
    {
        if (!plugin.Configuration.ShowHudPositions)
            return;

        var viewport = ImGuiHelpers.MainViewport;
        var scale = ImGuiHelpers.GlobalScale;
        var padding = new Vector2(4f, 1f) * scale;

        // Sous les fenêtres du plugin (cadres des textes défilants), par-dessus le jeu.
        var drawList = ImGui.GetBackgroundDrawList();
        foreach (var frame in plugin.Hud.Frames)
        {
            var text = HudLayout.Coordinates(frame.X, frame.Y);
            var size = ImGui.CalcTextSize(text) + (padding * 2);
            var frameMin = viewport.Pos + new Vector2(frame.X, frame.Y);
            var min = HudLayout.LabelMin(frameMin, new Vector2(frame.Width, frame.Height), size, viewport.Pos, viewport.Size);
            var max = min + size;
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(Background), 3f * scale);
            if (frame.Selected)
                drawList.AddRect(min, max, ImGui.GetColorU32(PlacementOverlay.Accent), 3f * scale, ImDrawFlags.None, 1.5f * scale);
            drawList.AddText(min + padding, ImGui.GetColorU32(frame.Selected ? PlacementOverlay.Accent : Text), text);
        }
    }
}
