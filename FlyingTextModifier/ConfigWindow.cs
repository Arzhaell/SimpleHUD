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
        DrawLayout();
        DrawPositions();

        ImGui.Spacing();
        DrawScales();

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
        DrawHud();

        ImGui.Separator();
        DrawLanguage();
    }

    // Positions des éléments de l'ATH du jeu (lecture seule) : coin haut-gauche du cadre de chaque élément dans
    // l'éditeur d'ATH, en pixels.
    private void DrawHud()
    {
        if (!ImGui.CollapsingHeader(Loc.T("Game HUD positions", "Positions de l'ATH du jeu")))
            return;

        var show = plugin.Configuration.ShowHudPositions;
        if (ImGui.Checkbox(Loc.T("Show them in the HUD layout editor", "Les afficher dans la configuration de l'ATH"), ref show))
        {
            plugin.Configuration.ShowHudPositions = show;
            plugin.Configuration.Save();
        }

        var frames = plugin.Hud.Frames;
        if (frames.Length == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.T(
                "Open the HUD layout editor to see where its elements are.",
                "Ouvre la configuration de l'ATH pour voir la position de ses éléments."));
            return;
        }

        // Une douzaine de lignes visibles, les autres en faisant défiler.
        var scale = ImGuiHelpers.GlobalScale;
        var rowHeight = ImGui.GetTextLineHeight() + (2 * ImGui.GetStyle().CellPadding.Y);
        var size = new Vector2(460 * scale, (Math.Min(frames.Length, 12) + 1) * rowHeight);
        if (!ImGui.BeginTable("##Hud", 3, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg, size))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Loc.T("Element", "Élément"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("X", ImGuiTableColumnFlags.WidthFixed, 60 * scale);
        ImGui.TableSetupColumn("Y", ImGuiTableColumnFlags.WidthFixed, 60 * scale);
        ImGui.TableHeadersRow();

        // L'élément sélectionné dans l'éditeur d'ATH ressort en jaune, comme son étiquette.
        foreach (var frame in frames)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (frame.Selected)
                ImGui.TextColored(PlacementOverlay.Accent, frame.Name);
            else
                ImGui.TextUnformatted(frame.Name);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{frame.X}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{frame.Y}");
        }

        ImGui.EndTable();
    }

    // Disposition des textes sur le personnage : un, deux ou trois cadres.
    private void DrawLayout()
    {
        var current = plugin.Configuration.Layout;
        ImGui.SetNextItemWidth(244 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo(Loc.T("Texts on you", "Textes sur toi"), Plugin.LayoutName(current)))
        {
            foreach (var layout in Enum.GetValues<PersonalLayout>())
            {
                if (ImGui.Selectable(Plugin.LayoutName(layout), layout == current) && layout != current)
                    plugin.SetLayout(layout);
            }

            ImGui.EndCombo();
        }

        if (FlyTextLayout.SeparatesStatuses(current))
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.T(
                "The game stacks status effects with damage taken: when both arrive together, a block may show a gap.",
                "Le jeu empile les statuts avec les dégâts subis : quand les deux arrivent ensemble, un bloc peut garder un trou."));
        }
    }

    // Positions au pixel près : X/Y à l'écran pour chaque cadre du personnage, écart avec la cible pour les textes sur la cible.
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

        foreach (var block in FlyTextLayout.Blocks(plugin.Configuration.Layout))
        {
            if (plugin.BlockPosition(block) is not { } ratio)
                continue;

            if (DrawRow($"{block}", Plugin.BlockName(block), ratio, screen, out var changed))
            {
                plugin.MoveBlock(block, changed - ratio);
                plugin.Configuration.Save();
            }

            if (ResetButton($"{block}"))
                plugin.ResetBlock(block);
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

    // Par famille de textes : affichée ou masquée, et taille en % de celle du jeu. Au lâcher du curseur, quelques textes de test.
    private void DrawScales()
    {
        if (!ImGui.BeginTable("##Scales", 4, ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn(Loc.T("Texts", "Textes"));
        ImGui.TableSetupColumn(Loc.T("Show", "Afficher"));
        ImGui.TableSetupColumn(Loc.T("Size", "Taille"));
        ImGui.TableSetupColumn(string.Empty);
        ImGui.TableHeadersRow();

        foreach (var category in Plugin.Categories)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Plugin.CategoryName(category));

            ImGui.TableNextColumn();
            var shown = !plugin.Configuration.IsHidden(category);
            if (ImGui.Checkbox($"##Show{category}", ref shown))
            {
                plugin.Configuration.SetHidden(category, !shown);
                plugin.Configuration.Save();
            }

            // Taille sans effet sur une famille masquée : curseur grisé.
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(!shown);
            var percent = (int)MathF.Round(plugin.Configuration.GetScale(category) * 100);
            ImGui.SetNextItemWidth(244 * ImGuiHelpers.GlobalScale);
            if (ImGui.SliderInt($"##Scale{category}", ref percent, 50, 200, "%d %%"))
                plugin.Configuration.SetScale(category, percent / 100f);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                plugin.Configuration.Save();
                plugin.ShowScaleTest(category);
            }

            if (ResetButton($"Scale{category}"))
            {
                plugin.Configuration.SetScale(category, 1f);
                plugin.Configuration.Save();
                plugin.ShowScaleTest(category);
            }

            ImGui.EndDisabled();
        }

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
