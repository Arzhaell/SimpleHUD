using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace FlyingTextModifier;

/// <summary>Dans l'éditeur d'ATH du jeu : la position de chaque élément, écrite dans son coin haut-gauche.</summary>
internal sealed class HudLabels
{
    private static readonly Vector4 Text = new(0.85f, 0.9f, 1f, 1f);
    private static readonly Vector4 Background = new(0.05f, 0.08f, 0.15f, 0.75f);

    private readonly Plugin plugin;

    public HudLabels(Plugin plugin) => this.plugin = plugin;

    public void Draw()
    {
        if (!plugin.HudLayoutOpen || !plugin.Configuration.ShowHudPositions)
            return;

        var viewport = ImGuiHelpers.MainViewport;
        var scale = ImGuiHelpers.GlobalScale;
        var padding = new Vector2(4f, 1f) * scale;

        // Sous les fenêtres du plugin (cadres des textes défilants), par-dessus le jeu.
        var drawList = ImGui.GetBackgroundDrawList();
        foreach (var element in plugin.Hud.Elements)
        {
            var text = HudLayout.Coordinates(element.X, element.Y);
            var size = ImGui.CalcTextSize(text) + (padding * 2);
            var min = HudLayout.LabelMin(viewport.Pos + new Vector2(element.X, element.Y), size, viewport.Pos, viewport.Size);
            var max = min + size;
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(Background), 3f * scale);
            if (element.Selected)
                drawList.AddRect(min, max, ImGui.GetColorU32(PlacementOverlay.Accent), 3f * scale, ImDrawFlags.None, 1.5f * scale);
            drawList.AddText(min + padding, ImGui.GetColorU32(element.Selected ? PlacementOverlay.Accent : Text), text);
        }
    }
}
