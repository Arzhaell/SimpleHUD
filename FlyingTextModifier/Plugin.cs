using System.Linq;
using System.Numerics;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace FlyingTextModifier;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/flytextmod";

    // Écran de l'éditeur d'ATH (« Configuration de l'ATH »), affiché tant que l'éditeur est ouvert.
    private const string HudLayoutAddonName = "_HudLayoutScreen";

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

    private readonly WindowSystem windowSystem = new("FlyingTextModifier");
    private readonly ConfigWindow configWindow;
    private readonly PlacementOverlay overlay;
    private readonly TestTexts testTexts = new();
    private readonly FlyTextScaler scaler;
    private readonly FlyTextHider hider;

    // La fenêtre s'ouvre avec l'éditeur d'ATH (pour les réglages au pixel) et se referme avec lui.
    private bool hudLayoutWasOpen;
    private bool openedWithHudLayout;
#if DEBUG
    private readonly FlyTextDiagnostics diagnostics = new();
#endif

    public Plugin()
    {
        Configuration = LoadConfiguration();
        Loc.Update(Configuration.Language);
        Groups = new FlyTextGroups(Configuration);
        scaler = new FlyTextScaler(Configuration);
        hider = new FlyTextHider(Configuration);

        configWindow = new ConfigWindow(this);
        overlay = new PlacementOverlay(this);
        windowSystem.AddWindow(configWindow);
        PluginInterface.UiBuilder.Draw += OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += configWindow.Toggle;
        PluginInterface.LanguageChanged += OnDalamudLanguageChanged;

        RegisterCommand();
    }

    public Configuration Configuration { get; }

    internal FlyTextGroups Groups { get; }

    /// <summary>Les cadres sont affichés dans l'éditeur d'ATH, et tant que la fenêtre du plugin est ouverte.</summary>
    public bool IsPlacing => configWindow.IsOpen || hudLayoutWasOpen;

    public static bool IsFlyTextFilterLoaded => PluginInterface.InstalledPlugins.Any(p => p.IsLoaded && p.InternalName == "FlyTextFilter");

    public static string GroupName(FlyTextGroup group) => group switch
    {
        FlyTextGroup.Healing => Loc.T("Healing received", "Soins reçus"),
        _ => Loc.T("Status effects / damage taken", "Statuts / dégâts subis"),
    };

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
        scaler.Dispose();
        Groups.Dispose();
#if DEBUG
        diagnostics.Dispose();
#endif
    }

    public void ResetGroup(FlyTextGroup group)
    {
        Groups.Reset(group);
        Configuration.Save();
        ShowTestTexts(group);
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
