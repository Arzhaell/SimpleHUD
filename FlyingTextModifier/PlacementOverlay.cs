using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace FlyingTextModifier;

/// <summary>Cadres « Texte défilant » à faire glisser, dessinés par-dessus l'éditeur d'ATH du jeu.</summary>
internal sealed class PlacementOverlay
{
    private static readonly Vector2 FrameSize = new(230, 76);

    private static readonly Vector4 Accent = new(1f, 0.82f, 0.35f, 1f);

    private const ImGuiWindowFlags FrameFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground
        | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing
        | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollWithMouse;

    private readonly Plugin plugin;

    // Cadre en cours de glissé (null si aucun).
    private string? dragged;

    public PlacementOverlay(Plugin plugin) => this.plugin = plugin;

    public void Draw()
    {
        if (!plugin.IsPlacing)
        {
            dragged = null;
            return;
        }

        var viewport = ImGuiHelpers.MainViewport;
        var size = FrameSize * ImGuiHelpers.GlobalScale;
        foreach (var group in FlyTextLayout.Groups)
            DrawGroupFrame(group, viewport.Pos, viewport.Size, size);
        DrawTargetFrame(viewport.Pos, viewport.Size, size);
    }

    private void DrawGroupFrame(FlyTextGroup group, Vector2 origin, Vector2 screen, Vector2 size)
    {
        if (plugin.Groups.GetPosition(group) is not { } ratio)
            return;

        var anchor = origin + (ratio * screen);
        var (x, y) = FlyTextLayout.ToWholePixels(ratio, plugin.Groups.Screen);
        var frame = DrawFrame($"{group}", FlyTextLayout.FrameMin(group, anchor, size), size, anchor, null, Plugin.GroupName(group), $"X {x}  ·  Y {y}");

        if (frame.Delta is { } delta)
            plugin.Groups.SetPosition(group, ratio + (delta / screen));
        if (frame.Released)
        {
            plugin.Configuration.Save();
            plugin.ShowTestTexts(group);
        }

        if (frame.ResetRequested)
            plugin.ResetGroup(group);
    }

    // Les textes sur la cible la suivent : le cadre montre l'écart choisi par rapport à elle
    // (par rapport au personnage s'il n'y a pas de cible, le temps du réglage).
    private void DrawTargetFrame(Vector2 origin, Vector2 screen, Vector2 size)
    {
        var target = Plugin.TargetManager.Target ?? (Dalamud.Game.ClientState.Objects.Types.IGameObject?)Plugin.ObjectTable.LocalPlayer;
        var reference = target != null && Plugin.GameGui.WorldToScreen(target.Position, out var onScreen)
            ? onScreen
            : origin + (screen / 2);

        var offset = plugin.Groups.TargetOffset;
        var anchor = reference + (offset * screen);
        var (x, y) = FlyTextLayout.ToWholePixels(offset, plugin.Groups.Screen);
        var subtitle = Plugin.TargetManager.Target != null
            ? Loc.T("On the target", "Sur la cible")
            : Loc.T("On the target (preview on you)", "Sur la cible (aperçu sur toi)");
        var frame = DrawFrame("Target", FlyTextLayout.CenteredFrameMin(anchor, size), size, anchor, reference, subtitle, $"{x:+0;-0;0}  ·  {y:+0;-0;0}");

        if (frame.Delta is { } delta)
            plugin.Groups.TargetOffset = offset + (delta / screen);
        if (frame.Released)
            plugin.Configuration.Save();
        if (frame.ResetRequested)
            plugin.ResetTarget();
    }

    private readonly record struct FrameInput(Vector2? Delta, bool Released, bool ResetRequested);

    /// <param name="origin">Point de départ du décalage (textes sur la cible), relié au cadre par un trait.</param>
    private FrameInput DrawFrame(string id, Vector2 min, Vector2 size, Vector2 anchor, Vector2? origin, string subtitle, string coordinates)
    {
        Vector2? delta = null;
        var released = false;
        var reset = false;
        var scale = ImGuiHelpers.GlobalScale;

        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        if (ImGui.Begin($"##FlyTextFrame{id}", FrameFlags))
        {
            ImGui.InvisibleButton("##drag", size);
            var hovered = ImGui.IsItemHovered();
            var active = ImGui.IsItemActive();

            if (active && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
            {
                var io = ImGui.GetIO();
                if (io.MouseDelta != Vector2.Zero)
                {
                    delta = FlyTextLayout.DragDelta(io.MouseDelta, io.KeyShift);
                    dragged = id;
                }
            }

            // Cadre lâché après un glissé.
            if (ImGui.IsItemDeactivated() && dragged == id)
            {
                dragged = null;
                released = true;
            }

            reset = ImGui.IsItemClicked(ImGuiMouseButton.Right);

            if (hovered && !active)
            {
                ImGui.SetTooltip(Loc.T(
                    "Drag to move (hold Shift for fine moves) — right-click: original position",
                    "Glisser pour déplacer (Maj enfoncée : déplacement fin) — clic droit : position d'origine"));
            }

            var highlight = hovered || active;
            var max = min + size;
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.05f, 0.08f, 0.15f, highlight ? 0.8f : 0.6f)), 4f * scale);
            drawList.AddRect(min, max, ImGui.GetColorU32(highlight ? Accent : new Vector4(0.7f, 0.82f, 1f, 0.9f)), 4f * scale, ImDrawFlags.None, 2f * scale);

            var padding = new Vector2(10f, 6f) * scale;
            var line = new Vector2(0, ImGui.GetTextLineHeightWithSpacing());
            drawList.AddText(min + padding, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), Loc.T("Flying text", "Texte défilant"));
            drawList.AddText(min + padding + line, ImGui.GetColorU32(new Vector4(0.75f, 0.8f, 0.9f, 1f)), subtitle);
            drawList.AddText(min + padding + (line * 2), ImGui.GetColorU32(Accent), coordinates);
        }

        ImGui.End();
        ImGui.PopStyleVar(3);

        // Point d'ancrage (et, pour la cible, trait depuis la position d'origine), dessinés par-dessus tout.
        var foreground = ImGui.GetForegroundDrawList();
        var accent = ImGui.GetColorU32(Accent);
        if (origin is { } from && from != anchor)
        {
            foreground.AddLine(from, anchor, accent, 1.5f * scale);
            foreground.AddCircle(from, 5f * scale, accent, 0, 1.5f * scale);
        }

        foreground.AddCircleFilled(anchor, 4f * scale, accent);

        return new FrameInput(delta, released, reset);
    }
}
