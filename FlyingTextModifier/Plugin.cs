using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.FlyText;
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

    // Couleur des textes de test (blanc opaque, quel que soit l'ordre des composantes attendu par le jeu).
    private const uint TestTextColor = 0xFFFFFFFF;

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IFlyTextGui FlyTextGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("FlyingTextModifier");
    private readonly ConfigWindow configWindow;
    private readonly PlacementOverlay overlay;

    public Plugin()
    {
        Configuration = LoadConfiguration();
        Loc.Update(Configuration.Language);
        Groups = new FlyTextGroups(Configuration);

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
    public bool IsPlacing => configWindow.IsOpen || GameGui.GetAddonByName(HudLayoutAddonName).IsVisible;

    public static bool IsFlyTextFilterLoaded => PluginInterface.InstalledPlugins.Any(p => p.IsLoaded && p.InternalName == "FlyTextFilter");

    public static string GroupName(FlyTextGroup group) => group switch
    {
        FlyTextGroup.Healing => Loc.T("Healing received", "Soins reçus"),
        _ => Loc.T("Status effects / damage taken", "Statuts / dégâts subis"),
    };

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
        Groups.Dispose();
    }

    public void ResetGroup(FlyTextGroup group)
    {
        Groups.Reset(group);
        Configuration.Save();
        ShowTestText(group);
    }

    public void ResetAll()
    {
        foreach (var group in FlyTextLayout.Groups)
            Groups.Reset(group);
        Configuration.Save();
        ShowTestTexts();
    }

    public void ShowTestTexts()
    {
        foreach (var group in FlyTextLayout.Groups)
            ShowTestText(group);
    }

    /// <summary>Fait apparaître sur le personnage un faux texte du groupe, pour voir où il s'affiche.</summary>
    public void ShowTestText(FlyTextGroup group) => Framework.RunOnFrameworkThread(() =>
    {
        // Acteur 1 = le personnage du joueur.
        if (group == FlyTextGroup.Healing)
            FlyTextGui.AddFlyText(FlyTextKind.Healing, 1, 1234, 0, Loc.T("Test", "Test"), string.Empty, TestTextColor, 0, 0);
        else
            FlyTextGui.AddFlyText(FlyTextKind.Buff, 1, 0, 0, Loc.T("Status effect (test)", "Effet de statut (test)"), string.Empty, TestTextColor, 0, 0);
    });

    public void SetLanguage(PluginLanguage language)
    {
        Configuration.Language = language;
        Configuration.Save();
        RefreshLanguage();
    }

    private void OnDraw()
    {
        windowSystem.Draw();
        overlay.Draw();
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
