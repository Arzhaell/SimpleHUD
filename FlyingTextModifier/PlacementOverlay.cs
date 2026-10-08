using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace FlyingTextModifier;

/// <summary>Cadres « Texte défilant » à faire glisser, dessinés par-dessus l'éditeur d'ATH du jeu.</summary>
internal sealed class PlacementOverlay
{
    private static readonly Vector2 FrameSize = new(220, 64);

    private const ImGuiWindowFlags FrameFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground
        | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing
        | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollWithMouse;

    private readonly Plugin plugin;
    private FlyTextGroup? dragged;

    public PlacementOverlay(Plugin plugin) => this.plugin = plugin;

    public void Draw()
    {
        if (!plugin.IsPlacing)
        {
            dragged = null;
            return;
        }

        var viewport = ImGuiHelpers.MainViewport;
        foreach (var group in FlyTextLayout.Groups)
            DrawFrame(group, viewport.Pos, viewport.Size);
    }

    private void DrawFrame(FlyTextGroup group, Vector2 origin, Vector2 screen)
    {
        if (plugin.Groups.GetPosition(group) is not { } ratio)
            return;

        var scale = ImGuiHelpers.GlobalScale;
        var size = FrameSize * scale;
        var anchor = origin + (ratio * screen);
        var min = FlyTextLayout.FrameMin(group, anchor, size);

        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        if (ImGui.Begin($"##FlyTextFrame{group}", FrameFlags))
        {
            ImGui.InvisibleButton("##drag", size);
            var hovered = ImGui.IsItemHovered();
            var active = ImGui.IsItemActive();

            if (active && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
            {
                var delta = ImGui.GetIO().MouseDelta;
                if (delta != Vector2.Zero)
                {
                    plugin.Groups.SetPosition(group, ratio + (delta / screen));
                    dragged = group;
                }
            }

            // Cadre lâché : on enregistre et on montre un vrai texte à la nouvelle place.
            if (ImGui.IsItemDeactivated() && dragged == group)
            {
                dragged = null;
                plugin.Configuration.Save();
                plugin.ShowTestText(group);
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                plugin.ResetGroup(group);

            if (hovered && !active)
                ImGui.SetTooltip(Loc.T("Drag to move — right-click: original position", "Glisser pour déplacer — clic droit : position d'origine"));

            var highlight = hovered || active;
            var max = min + size;
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.05f, 0.08f, 0.15f, highlight ? 0.8f : 0.6f)), 4f * scale);
            drawList.AddRect(min, max, ImGui.GetColorU32(highlight ? new Vector4(1f, 0.82f, 0.35f, 1f) : new Vector4(0.7f, 0.82f, 1f, 0.9f)), 4f * scale, ImDrawFlags.None, 2f * scale);

            // Point d'ancrage : l'endroit exact où le jeu place les textes de ce groupe.
            drawList.AddCircleFilled(anchor, 4f * scale, ImGui.GetColorU32(new Vector4(1f, 0.82f, 0.35f, 1f)));

            var padding = new Vector2(10f, 8f) * scale;
            drawList.AddText(min + padding, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), Loc.T("Flying text", "Texte défilant"));
            drawList.AddText(min + padding + new Vector2(0, ImGui.GetTextLineHeightWithSpacing()), ImGui.GetColorU32(new Vector4(0.75f, 0.8f, 0.9f, 1f)), Plugin.GroupName(group));
        }

        ImGui.End();
        ImGui.PopStyleVar(3);
    }
}
