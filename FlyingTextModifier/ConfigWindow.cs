using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace FlyingTextModifier;

internal sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;

    public ConfigWindow(Plugin plugin)
        : base("Flying Text Modifier###FlyingTextModifierConfig", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.plugin = plugin;
    }

    public override void Draw()
    {
        ImGui.TextUnformatted(Loc.T(
            "Drag the \"Flying text\" frames (hold Shift for fine moves), or set them to the pixel below.\n"
            + "They appear in the HUD layout editor and while this window is open.",
            "Fais glisser les cadres « Texte défilant » (Maj enfoncée : déplacement fin), ou règle-les au pixel ci-dessous.\n"
            + "Ils apparaissent dans la configuration de l'ATH et tant que cette fenêtre est ouverte."));

        if (plugin.Groups.NotFound)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, Loc.T(
                "The flying text could not be found in the game (game update?): it cannot be moved for now.",
                "Les textes défilants sont introuvables dans le jeu (mise à jour du jeu ?) : impossible de les déplacer pour l'instant."));
        }

        if (Plugin.IsFlyTextFilterLoaded)
        {
            ImGui.TextColored(ImGuiColors.DalamudOrange, Loc.T(
                "FlyTextFilter is loaded: if it also moves these texts, the positions chosen here take over.",
                "FlyTextFilter est chargé : s'il déplace aussi ces textes, les positions choisies ici l'emportent."));
        }

        ImGui.Spacing();
        DrawPositions();

        ImGui.Spacing();
        if (ImGui.Button(Loc.T("Test all texts", "Tester tous les textes")))
        {
            plugin.ShowTestTexts();
            plugin.ShowTargetTestTexts();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("Texts on the target need a target.", "Les textes sur la cible ont besoin d'une cible."));
        ImGui.SameLine();
        if (ImGui.Button(Loc.T("Reset all", "Tout réinitialiser")))
            plugin.ResetAll();

        ImGui.Separator();
        DrawLanguage();
    }

    // Positions au pixel près : X/Y à l'écran pour les groupes du personnage, écart avec la cible pour les textes sur la cible.
    private void DrawPositions()
    {
        var screen = plugin.Groups.Screen;
        if (screen == Vector2.Zero || !ImGui.BeginTable("##Positions", 4, ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn(Loc.T("Texts", "Textes"));
        ImGui.TableSetupColumn("X");
        ImGui.TableSetupColumn("Y");
        ImGui.TableSetupColumn(string.Empty);
        ImGui.TableHeadersRow();

        foreach (var group in FlyTextLayout.Groups)
        {
            if (plugin.Groups.GetPosition(group) is not { } ratio)
                continue;

            if (DrawRow($"{group}", Plugin.GroupName(group), ratio, screen, out var changed))
            {
                plugin.Groups.SetPosition(group, changed);
                plugin.Configuration.Save();
            }

            if (ResetButton($"{group}"))
                plugin.ResetGroup(group);
        }

        if (DrawRow("Target", Loc.T("On the target (offset)", "Sur la cible (décalage)"), plugin.Groups.TargetOffset, screen, out var offset))
        {
            plugin.Groups.TargetOffset = offset;
            plugin.Configuration.Save();
        }

        if (ResetButton("Target"))
            plugin.ResetTarget();

        ImGui.EndTable();
    }

    // Ligne du tableau : nom puis X et Y en pixels (boutons − / + au pixel, Ctrl+clic par 10).
    private static bool DrawRow(string id, string name, Vector2 ratio, Vector2 screen, out Vector2 changed)
    {
        var (x, y) = FlyTextLayout.ToWholePixels(ratio, screen);
        var width = 120 * ImGuiHelpers.GlobalScale;

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(name);

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(width);
        var edited = ImGui.InputInt($"##X{id}", ref x, 1, 10);

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(width);
        edited |= ImGui.InputInt($"##Y{id}", ref y, 1, 10);

        changed = new Vector2(x, y) / screen;
        return edited;
    }

    private static bool ResetButton(string id)
    {
        ImGui.TableNextColumn();
        var clicked = ImGuiComponents.IconButton($"##Reset{id}", FontAwesomeIcon.Undo);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("Original position", "Position d'origine"));
        return clicked;
    }

    private void DrawLanguage()
    {
        var current = plugin.Configuration.Language;
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        if (!ImGui.BeginCombo(Loc.T("Language", "Langue"), LanguageName(current)))
            return;

        foreach (var language in Enum.GetValues<PluginLanguage>())
        {
            if (ImGui.Selectable(LanguageName(language), language == current))
                plugin.SetLanguage(language);
        }

        ImGui.EndCombo();
    }

    private static string LanguageName(PluginLanguage language) => language switch
    {
        PluginLanguage.English => "English",
        PluginLanguage.French => "Français",
        _ => Loc.T("Automatic (Dalamud language)", "Automatique (langue de Dalamud)"),
    };
}
