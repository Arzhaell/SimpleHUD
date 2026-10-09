using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.FlyText;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaStatus = Lumina.Excel.Sheets.Status;

namespace SimpleHUD;

/// <summary>
/// Valeur des boucliers reçus, affichée avec les soins et nommée d'après le sort qui les donne. Le jeu ne donne le
/// bouclier qu'en pourcentage des PV max (au pour cent près) : à chaque hausse du bouclier, un texte de soin est créé
/// sur le personnage (le jeu le range lui-même avec les soins), puis, au moment où le jeu le crée, renommé et coloré
/// comme la barre de bouclier du jeu.
/// </summary>
internal sealed unsafe class ShieldTexts : IDisposable
{
    // Jaune des boucliers du jeu, au format des textes défilants (0xAABBGGRR, comme les couleurs d'ImGui).
    private const uint ShieldColor = 0xFF4AD8FF;

    // Types d'effets d'une action : effet de statut donné à la cible, ou au lanceur (sort lancé sur soi).
    private const byte StatusOnTarget = 14;
    private const byte StatusOnSource = 15;

    // Type d'action d'un sort ou d'une aptitude (feuille Action du jeu).
    private const byte SpellAction = 1;

    // Texte créé par le plugin : reconnu à son type et à sa valeur, s'il arrive peu après.
    private static readonly TimeSpan PendingDelay = TimeSpan.FromSeconds(1);

    // Le bouclier est affiché un instant après sa hausse, le temps que ses effets arrivent ; un effet reçu ou renouvelé
    // dans la seconde qui précède est celui du bouclier.
    private static readonly TimeSpan NameDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan FreshWindow = TimeSpan.FromSeconds(1);

    // Un sort reçu dans les deux secondes qui précèdent est celui du bouclier (le jeu applique l'effet un peu après le
    // sort).
    private static readonly TimeSpan SpellWindow = TimeSpan.FromSeconds(2);

    private readonly Configuration configuration;
    private readonly FlyTextNodes nodes;
    private readonly List<(int Value, string Name, DateTime Until)> pending = [];
    private readonly List<(int Value, DateTime Due)> waiting = [];

    // Personnage suivi et son bouclier à l'image précédente (null : pas encore lu, après une connexion ou un changement
    // de zone, pour ne pas afficher un bouclier déjà là).
    private nint player;
    private byte? lastPercent;

    // Effets du personnage à l'image précédente (temps restant, paramètre), et moment où chacun a été reçu ou renouvelé.
    private readonly Dictionary<uint, (float Remaining, ushort Param)> statuses = [];
    private readonly Dictionary<uint, (float Remaining, ushort Param)> current = [];
    private readonly Dictionary<uint, DateTime> refreshed = [];

    // Sort qui a donné chaque effet reçu avec un bouclier : pour nommer les boucliers qui se renouvellent seuls (Haima).
    private readonly Dictionary<uint, string> spellsByStatus = [];
    private readonly Dictionary<uint, string> statusNames = [];

    // Noms des sorts des joueurs (feuille Action du jeu), lus au premier bouclier.
    private HashSet<string>? spellNames;

    // Effets d'action reçus par le personnage (numéro d'ordre du serveur) et moment où ils sont apparus ; le plus récent
    // vu, et le dernier attribué à un bouclier. Null : liste pas encore lue (les effets déjà là sont anciens).
    private readonly Dictionary<uint, DateTime> effectsSeen = [];
    private uint? newestEffect;
    private uint lastAttributed;

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
            Show(character, gain, Loc.T("Shield", "Bouclier"));
    }

    private void OnUpdate(IFramework framework)
    {
        var character = Plugin.ObjectTable.LocalPlayer;
        if (character == null || character.Address != player)
        {
            player = character?.Address ?? 0;
            lastPercent = character?.ShieldPercentage;
            statuses.Clear();
            refreshed.Clear();
            waiting.Clear();
            effectsSeen.Clear();
            newestEffect = null;
            return;
        }

        var now = DateTime.UtcNow;
        WatchStatuses(character, now);
        WatchEffects(character, now);

        var percent = character.ShieldPercentage;
        if (configuration.ShowShields && lastPercent is { } before && FlyTextLayout.ShieldGain(before, percent, character.MaxHp) is { } gain)
            waiting.Add((gain, now + NameDelay));
        lastPercent = percent;

        for (var i = waiting.Count - 1; i >= 0; i--)
        {
            if (waiting[i].Due > now)
                continue;

            Show(character, waiting[i].Value, FindName(character, now) ?? Loc.T("Shield", "Bouclier"));
            waiting.RemoveAt(i);
        }
    }

    // Note les effets reçus ou renouvelés (temps restant qui remonte, paramètre qui change : charges de Haima…).
    private void WatchStatuses(IBattleChara character, DateTime now)
    {
        current.Clear();
        foreach (var status in character.StatusList)
        {
            if (status.StatusId == 0)
                continue;

            var state = (status.RemainingTime, status.Param);
            if (!statuses.TryGetValue(status.StatusId, out var previous)
                || state.RemainingTime > previous.Remaining + 0.5f || state.Param != previous.Param)
                refreshed[status.StatusId] = now;
            current[status.StatusId] = state;
        }

        statuses.Clear();
        foreach (var (id, state) in current)
            statuses[id] = state;
    }

    // Note le moment où chaque nouvel effet d'action reçu apparaît : le personnage garde les derniers effets d'action
    // reçus (sort, lanceur, effets), sans dire quand.
    private void WatchEffects(IBattleChara character, DateTime now)
    {
        var handler = ((Character*)character.Address)->GetActionEffectHandler();
        if (handler == null)
            return;

        var newest = newestEffect ?? 0;
        foreach (ref var entry in handler->IncomingEffects)
        {
            if (newestEffect != null && entry.GlobalSequence > newestEffect)
                effectsSeen.TryAdd(entry.GlobalSequence, now);
            newest = Math.Max(newest, entry.GlobalSequence);
        }

        newestEffect = newest;
        foreach (var old in effectsSeen.Where(pair => now - pair.Value > SpellWindow).Select(pair => pair.Key).ToList())
            effectsSeen.Remove(old);
    }

    private string? FindName(IBattleChara character, DateTime now)
    {
        var fresh = refreshed.Where(pair => statuses.ContainsKey(pair.Key) && now - pair.Value <= FreshWindow)
            .OrderByDescending(pair => pair.Value)
            .Select(pair => pair.Key)
            .ToList();
        foreach (var status in fresh)
            statusNames.TryAdd(status, StatusName(status));

        var spell = RecentSpell(character, now);
        if (spell is { } found)
        {
            foreach (var status in found.Statuses)
                spellsByStatus[status] = found.Name;
        }

        spellNames ??= Plugin.DataManager.GetExcelSheet<LuminaAction>()
            .Where(action => action.IsPlayerAction)
            .Select(action => action.Name.ExtractText())
            .Where(name => name.Length > 0)
            .ToHashSet();
        var name = FlyTextLayout.ShieldName(spell?.Name, fresh, spellsByStatus, statusNames, spellNames);
#if DEBUG
        Plugin.Log.Information("[diag] shield name={Name} spell={Spell} fresh=[{Fresh}]", name ?? "-", spell?.Name ?? "-",
            string.Join(" ", fresh.Select(id => $"{id}:{statusNames.GetValueOrDefault(id)}")));
#endif
        return name;
    }

    // Sort le plus récent reçu par le personnage dans les deux dernières secondes, d'un allié, qui lui a donné un effet,
    // et pas encore attribué à un bouclier.
    private (string Name, List<uint> Statuses)? RecentSpell(IBattleChara character, DateTime now)
    {
        var handler = ((Character*)character.Address)->GetActionEffectHandler();
        if (handler == null)
            return null;

        var me = character.GameObjectId;
        (uint Sequence, uint Action, List<uint> Statuses)? best = null;
        foreach (ref var entry in handler->IncomingEffects)
        {
            if (entry.GlobalSequence <= lastAttributed || (ulong)entry.Target != me || entry.ActionType != SpellAction
                || !effectsSeen.TryGetValue(entry.GlobalSequence, out var seen) || now - seen > SpellWindow)
                continue;

            var given = new List<uint>();
            foreach (var effect in entry.Effects.Effects)
            {
                if (effect.Type is StatusOnTarget or StatusOnSource && effect.Value != 0)
                    given.Add(effect.Value);
            }

            if (given.Count == 0 || IsEnemy(entry.Source) || entry.GlobalSequence <= (best?.Sequence ?? 0))
                continue;

            best = (entry.GlobalSequence, entry.ActionId, given);
        }

        if (best is { } attributed)
            lastAttributed = attributed.Sequence;
#if DEBUG
        Plugin.Log.Information("[diag] shield spell sequence={Sequence} action={Action} statuses=[{Statuses}]",
            best?.Sequence ?? 0, best?.Action ?? 0, best is { } logged ? string.Join(" ", logged.Statuses) : string.Empty);
#endif
        if (best is not { } spell)
            return null;

        var name = Plugin.DataManager.GetExcelSheet<LuminaAction>().GetRowOrDefault(spell.Action)?.Name.ExtractText();
        return string.IsNullOrEmpty(name) ? null : (name, spell.Statuses);
    }

    private static bool IsEnemy(ulong source) =>
        Plugin.ObjectTable.SearchById(source) is ICharacter caster && caster.StatusFlags.HasFlag(StatusFlags.Hostile);

    private static string StatusName(uint status) =>
        Plugin.DataManager.GetExcelSheet<LuminaStatus>().GetRowOrDefault(status)?.Name.ExtractText() ?? string.Empty;

    private void Show(IBattleChara character, int value, string name)
    {
        pending.Add((value, name, DateTime.UtcNow + PendingDelay));
        TestTexts.AddScreenLog(character, FlyTextKind.Healing, ScreenLogRelationKind.LocalPlayer, ScreenLogRelationKind.LocalPlayer, value, TestTexts.HealingActionId);
    }

    // Le texte de soin créé pour un bouclier prend le nom de son sort, en jaune.
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

        var name = pending[index].Name;
        pending.RemoveAt(index);
        text1 = new SeString(new TextPayload(name));
        color = ShieldColor;
    }
}
