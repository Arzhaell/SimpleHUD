using System.Linq;
using System.Numerics;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace SimpleHUD;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/simplehud";

    // Écran de l'éditeur d'ATH (« Configuration de l'ATH »), affiché tant que l'éditeur est ouvert.
    internal const string HudLayoutAddonName = "_HudLayoutScreen";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IFlyTextGui FlyTextGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("SimpleHUD");
    private readonly ConfigWindow configWindow;
    private readonly PlacementOverlay overlay;
    private readonly TestTexts testTexts = new();
    private readonly FlyTextNodes nodes;
    private readonly FlyTextHider hider;
    private readonly HudEditorOverlay hudOverlay;

    // La fenêtre s'ouvre avec l'éditeur d'ATH (pour les réglages au pixel) et se referme avec lui.
    private bool hudLayoutWasOpen;
    private bool openedWithHudLayout;
#if DEBUG
    private readonly FlyTextDiagnostics diagnostics = new();
    private readonly HudDiagnostics hudDiagnostics = new();
#endif

    public Plugin()
    {
        Configuration = LoadConfiguration();
        if (Configuration.Migrate())
            Configuration.Save();
        Loc.Update(Configuration.Language);
        Groups = new FlyTextGroups(Configuration);
        nodes = new FlyTextNodes(Configuration, Groups);
        hider = new FlyTextHider(Configuration);

        configWindow = new ConfigWindow(this);
        overlay = new PlacementOverlay(this);
        Hud = new HudFrames();
        hudOverlay = new HudEditorOverlay(this);
        windowSystem.AddWindow(configWindow);
        PluginInterface.UiBuilder.Draw += OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += configWindow.Toggle;
        PluginInterface.LanguageChanged += OnDalamudLanguageChanged;

        RegisterCommand();
    }

    public Configuration Configuration { get; }

    internal FlyTextGroups Groups { get; }

    /// <summary>Cadres des éléments de l'ATH dans l'éditeur du jeu (lecture seule).</summary>
    internal HudFrames Hud { get; }

    /// <summary>Les cadres sont affichés dans l'éditeur d'ATH, et tant que la fenêtre du plugin est ouverte.</summary>
    public bool IsPlacing => configWindow.IsOpen || hudLayoutWasOpen;

    public static bool IsFlyTextFilterLoaded => PluginInterface.InstalledPlugins.Any(p => p.IsLoaded && p.InternalName == "FlyTextFilter");

    public static string BlockName(PersonalBlock block) => block switch
    {
        PersonalBlock.All => Loc.T("Texts on you", "Textes sur toi"),
        PersonalBlock.Healing => Loc.T("Healing received", "Soins reçus"),
        PersonalBlock.StatusDamage => Loc.T("Status effects / damage taken", "Statuts / dégâts subis"),
        PersonalBlock.Status => Loc.T("Status effects", "Statuts"),
        PersonalBlock.HealingDamage => Loc.T("Healing / damage taken", "Soins / dégâts subis"),
        _ => Loc.T("Damage taken", "Dégâts subis"),
    };

    public static string LayoutName(PersonalLayout layout) => layout switch
    {
        PersonalLayout.Grouped => Loc.T("All grouped", "Tout regroupé"),
        PersonalLayout.HealingSeparate => Loc.T("Healing separate", "Soins séparés"),
        PersonalLayout.StatusSeparate => Loc.T("Status effects separate", "Statuts séparés"),
        _ => Loc.T("All separate", "Tout séparé"),
    };

    /// <summary>
    /// Point de référence d'un cadre (fraction de l'écran) : son point d'ancrage, ou celui des statuts/dégâts
    /// pour un cadre qui regroupe les deux blocs du jeu.
    /// </summary>
    public Vector2? BlockPosition(PersonalBlock block) => block switch
    {
        PersonalBlock.Status => Groups.StatusPosition,
        PersonalBlock.Healing => Groups.GetPosition(FlyTextGroup.Healing),
        _ => Groups.GetPosition(FlyTextGroup.StatusDamage),
    };

    /// <summary>Déplace un cadre (écart en fraction de l'écran) : tous les blocs du jeu qu'il contient bougent ensemble.</summary>
    public void MoveBlock(PersonalBlock block, Vector2 delta)
    {
        if (block == PersonalBlock.Status)
        {
            if (Groups.StatusPosition is { } status)
                Groups.StatusPosition = status + delta;
            return;
        }

        foreach (var group in FlyTextLayout.GroupsOf(block))
        {
            if (Groups.GetPosition(group) is { } position)
                Groups.SetPosition(group, position + delta);
        }
    }

    public void ResetBlock(PersonalBlock block)
    {
        if (block == PersonalBlock.Status)
        {
            if (Groups.GetPosition(FlyTextGroup.StatusDamage) is { } statusDamage)
                Groups.StatusPosition = FlyTextLayout.DefaultStatusPosition(statusDamage);
        }
        else
        {
            foreach (var group in FlyTextLayout.GroupsOf(block))
                Groups.Reset(group);
        }

        Configuration.Save();
        ShowBlockTest(block);
    }

    /// <summary>Quelques textes du cadre pour voir où ils tombent.</summary>
    public void ShowBlockTest(PersonalBlock block)
    {
        if (block == PersonalBlock.Healing)
            ShowTestTexts(FlyTextGroup.Healing);
        else if (block is PersonalBlock.All or PersonalBlock.HealingDamage)
            ShowTestTexts();
        else
            ShowTestTexts(FlyTextGroup.StatusDamage);
    }

    /// <summary>
    /// Change la disposition. Les blocs du jeu qui se retrouvent ensemble reprennent leur écart d'origine
    /// (soins à côté des statuts/dégâts), et le bloc des statuts, la première fois, se place au-dessus des dégâts.
    /// </summary>
    public void SetLayout(PersonalLayout layout)
    {
        Configuration.Layout = layout;
        if (Groups.GetPosition(FlyTextGroup.StatusDamage) is { } statusDamage)
        {
            if (FlyTextLayout.LinksGroups(layout))
            {
                var defaultHealing = Groups.GetGameDefault(FlyTextGroup.Healing) ?? new Vector2(0.49f, 0.5f);
                var defaultStatusDamage = Groups.GetGameDefault(FlyTextGroup.StatusDamage) ?? new Vector2(0.55f, 0.5f);
                Groups.SetPosition(FlyTextGroup.Healing, FlyTextLayout.RegroupedHealing(statusDamage, defaultHealing, defaultStatusDamage));
            }

            if (FlyTextLayout.SeparatesStatuses(layout) && Groups.StatusPosition == null)
                Groups.StatusPosition = FlyTextLayout.DefaultStatusPosition(statusDamage);
        }

        Configuration.Save();
        ShowTestTexts();
    }

    public static string CategoryName(FlyTextCategory category) => category switch
    {
        FlyTextCategory.Status => Loc.T("Buffs / debuffs", "Buffs / débuffs"),
        FlyTextCategory.Healing => Loc.T("Healing", "Soins"),
        FlyTextCategory.Damage => Loc.T("Damage", "Dégâts"),
        _ => Loc.T("Other (EXP, MP…)", "Autres (EXP, PM…)"),
    };

    /// <summary>Après un changement de taille : quelques textes de la famille pour voir le résultat.</summary>
    public void ShowScaleTest(FlyTextCategory category)
    {
        // Pas de texte de test pour les autres familles (expérience, PM…).
        if (category == FlyTextCategory.Other)
            return;

        if (category == FlyTextCategory.Healing)
        {
            ShowTestTexts(FlyTextGroup.Healing);
            return;
        }

        ShowTestTexts(FlyTextGroup.StatusDamage);
        if (category == FlyTextCategory.Damage)
            ShowTargetTestTexts();
    }

    // Un fichier de configuration illisible ne doit pas empêcher le plugin de se charger : on repart des réglages par défaut.
    private static Configuration LoadConfiguration()
    {
        try
        {
            return PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        }
        catch (System.Exception e)
        {
            Log.Error(e, "Could not read the configuration, falling back to default settings.");
            return new Configuration();
        }
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.LanguageChanged -= OnDalamudLanguageChanged;
        PluginInterface.UiBuilder.Draw -= OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi -= configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi -= configWindow.Toggle;
        windowSystem.RemoveAllWindows();
        hider.Dispose();
        hudOverlay.Dispose();
        Hud.Dispose();
        nodes.Dispose();
        Groups.Dispose();
#if DEBUG
        diagnostics.Dispose();
        hudDiagnostics.Dispose();
#endif
    }

    public void ResetTarget()
    {
        Groups.TargetOffset = Vector2.Zero;
        Configuration.Save();
    }

    public void ResetAll()
    {
        foreach (var group in FlyTextLayout.Groups)
            Groups.Reset(group);
        Configuration.Layout = PersonalLayout.Grouped;
        Groups.StatusPosition = null;
        Groups.TargetOffset = Vector2.Zero;
        foreach (var category in Categories)
        {
            Configuration.SetScale(category, 1f);
            Configuration.SetHidden(category, false);
        }

        Configuration.Save();
        ShowTestTexts();
    }

    /// <summary>Familles dont la taille et l'affichage se règlent dans la fenêtre.</summary>
    public static readonly FlyTextCategory[] Categories = [FlyTextCategory.Status, FlyTextCategory.Healing, FlyTextCategory.Damage, FlyTextCategory.Other];

    /// <summary>Fait défiler sur le personnage tous les types de textes des groupes donnés (tous si aucun).</summary>
    public void ShowTestTexts(params FlyTextGroup[] groups) =>
        testTexts.ShowOnPlayer(groups.Length == 0 ? FlyTextLayout.Groups : groups);

    /// <summary>Fait défiler tes coups sur la cible actuelle. Faux s'il n'y a pas de cible.</summary>
    public bool ShowTargetTestTexts() => testTexts.ShowOnTarget();

    public void SetLanguage(PluginLanguage language)
    {
        Configuration.Language = language;
        Configuration.Save();
        RefreshLanguage();
    }

    private void OnDraw()
    {
        FollowHudLayout();
        hudOverlay.Draw();
        windowSystem.Draw();
        overlay.Draw();
    }

    private void FollowHudLayout()
    {
        var open = GameGui.GetAddonByName(HudLayoutAddonName).IsVisible;
        if (open == hudLayoutWasOpen)
            return;
        hudLayoutWasOpen = open;

        if (open && !configWindow.IsOpen)
        {
            configWindow.IsOpen = true;
            openedWithHudLayout = true;
        }
        else if (!open && openedWithHudLayout)
        {
            configWindow.IsOpen = false;
            openedWithHudLayout = false;
        }
    }

    private void OnDalamudLanguageChanged(string languageCode)
    {
        if (Configuration.Language == PluginLanguage.Auto)
            RefreshLanguage();
    }

    private void RefreshLanguage()
    {
        Loc.Update(Configuration.Language);
        CommandManager.RemoveHandler(CommandName);
        RegisterCommand();
    }

    private void RegisterCommand()
    {
        CommandManager.AddHandler(CommandName, new CommandInfo((_, _) => configWindow.Toggle())
        {
            HelpMessage = Loc.T(
                "Opens the window and shows the flying text frames to drag (they also appear in the HUD layout editor).",
                "Ouvre la fenêtre et affiche les cadres des textes défilants à faire glisser (ils apparaissent aussi dans la configuration de l'ATH)."),
        });
    }
}
