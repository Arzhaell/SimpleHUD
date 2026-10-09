using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.FlyText;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace SimpleHUD;

/// <summary>
/// Valeur des boucliers reçus, affichée avec les soins. Le jeu ne la donne qu'en pourcentage des PV max (au pour cent
/// près) : à chaque hausse du bouclier, un texte de soin est créé sur le personnage (le jeu le range lui-même avec les
/// soins), puis, au moment où le jeu le crée, renommé « Bouclier » et coloré comme la barre de bouclier du jeu.
/// </summary>
internal sealed class ShieldTexts : IDisposable
{
    // Jaune des boucliers du jeu, au format des textes défilants (0xAABBGGRR, comme les couleurs d'ImGui).
    private const uint ShieldColor = 0xFF4AD8FF;

    // Texte créé par le plugin : reconnu à son type et à sa valeur, s'il arrive peu après.
    private static readonly TimeSpan PendingDelay = TimeSpan.FromSeconds(1);

    private readonly Configuration configuration;
    private readonly FlyTextNodes nodes;
    private readonly List<(int Value, DateTime Until)> pending = [];

    // Personnage suivi et son bouclier à l'image précédente (null : pas encore lu, après une connexion ou un changement
    // de zone, pour ne pas afficher un bouclier déjà là).
    private nint player;
    private byte? lastPercent;

    public ShieldTexts(Configuration configuration, FlyTextNodes nodes)
    {
        this.configuration = configuration;
        this.nodes = nodes;
        Plugin.Framework.Update += OnUpdate;
        Plugin.FlyTextGui.FlyTextCreated += OnFlyTextCreated;
    }

    public void Dispose()
    {
        Plugin.FlyTextGui.FlyTextCreated -= OnFlyTextCreated;
        Plugin.Framework.Update -= OnUpdate;
    }

    /// <summary>Fait défiler un bouclier de test (un dixième des PV max), si l'option est cochée.</summary>
    public void ShowTest()
    {
        if (configuration.ShowShields && Plugin.ObjectTable.LocalPlayer is { } character
            && FlyTextLayout.ShieldGain(0, 10, character.MaxHp) is { } gain)
            Show(character, gain);
    }

    private void OnUpdate(IFramework framework)
    {
        var character = Plugin.ObjectTable.LocalPlayer;
        if (character == null || character.Address != player)
        {
            player = character?.Address ?? 0;
            lastPercent = character?.ShieldPercentage;
            return;
        }

        var percent = character.ShieldPercentage;
        if (configuration.ShowShields && lastPercent is { } before && FlyTextLayout.ShieldGain(before, percent, character.MaxHp) is { } gain)
            Show(character, gain);
        lastPercent = percent;
    }

    private void Show(IBattleChara character, int value)
    {
        pending.Add((value, DateTime.UtcNow + PendingDelay));
        TestTexts.AddScreenLog(character, FlyTextKind.Healing, ScreenLogRelationKind.LocalPlayer, ScreenLogRelationKind.LocalPlayer, value, TestTexts.HealingActionId);
    }

    // Le texte de soin créé pour un bouclier devient « Bouclier », en jaune.
    private void OnFlyTextCreated(
        ref FlyTextKind kind, ref int val1, ref int val2, ref SeString text1, ref SeString text2,
        ref uint color, ref uint icon, ref uint damageTypeIcon, ref float yOffset, ref bool handled)
    {
        if (pending.Count == 0)
            return;

        var now = DateTime.UtcNow;
        pending.RemoveAll(shield => shield.Until < now);
        if (kind != FlyTextKind.Healing || FlyTextLayout.PlayerGroup(nodes.CurrentActor) != FlyTextGroup.Healing)
            return;

        var value = val1;
        var index = pending.FindIndex(shield => shield.Value == value);
        if (index < 0)
            return;

        pending.RemoveAt(index);
#if DEBUG
        Plugin.Log.Information("[diag] shield value={Value} text1=\"{Text1}\" text2=\"{Text2}\" color={Color:X8}", value, text1.TextValue, text2.TextValue, color);
#endif
        text1 = new SeString(new TextPayload(Loc.T("Shield", "Bouclier")));
        color = ShieldColor;
    }
}
