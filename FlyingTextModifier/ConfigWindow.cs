using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
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
            "The \"Flying text\" frames appear in the HUD layout editor and while this window is open.\n"
            + "Drag a frame to move the text. Right-click a frame to put it back where the game had it.",
            "Les cadres « Texte défilant » apparaissent dans la configuration de l'ATH et tant que cette fenêtre est ouverte.\n"
            + "Fais glisser un cadre pour déplacer les textes. Clic droit sur un cadre pour le remettre à sa place d'origine."));

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
        if (ImGui.Button(Loc.T("Test", "Tester")))
            plugin.ShowTestTexts();
        ImGui.SameLine();
        if (ImGui.Button(Loc.T("Reset all", "Tout réinitialiser")))
            plugin.ResetAll();

        ImGui.Separator();
        DrawLanguage();
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
