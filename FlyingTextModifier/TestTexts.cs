using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.FlyText;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FlyingTextModifier;

/// <summary>
/// Faux textes de test, créés comme ceux du combat (journal d'écran du personnage) : le jeu les range lui-même dans le
/// bon groupe et les met en forme (nom et icône des statuts, nom de l'action, couleurs).
/// </summary>
internal sealed unsafe class TestTexts
{
    // Feuille Action du jeu : 7541 = Second souffle (soin), 53 = Volée de coups (attaque).
    private const uint HealingActionId = 7541;
    private const uint AttackActionId = 53;

    // Type d'identifiant « action » dans le journal d'écran.
    private const byte ActionKind = 1;

    // Les textes partent les uns après les autres : un groupe n'en affiche que 8 à la fois.
    private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(400);

    private readonly Random random = new();
    private uint[]? buffs;
    private uint[]? debuffs;

    private enum Recipient
    {
        Player,
        Target,
    }

    private readonly record struct Sample(FlyTextKind Kind, ScreenLogRelationKind Source, int Value, uint ActionId);

    /// <summary>Tous les types de textes des groupes donnés, sur le personnage.</summary>
    public void ShowOnPlayer(IEnumerable<FlyTextGroup> groups)
    {
        LoadStatuses();
        foreach (var group in groups)
            Schedule(Recipient.Player, group == FlyTextGroup.Healing ? HealingSamples() : StatusDamageSamples());
    }

    /// <summary>Tes coups sur la cible actuelle. Faux s'il n'y a pas de cible qui puisse en recevoir.</summary>
    public bool ShowOnTarget()
    {
        if (Plugin.TargetManager.Target is not IBattleChara)
            return false;

        LoadStatuses();
        Schedule(Recipient.Target, TargetSamples());
        return true;
    }

    private static Sample[] HealingSamples() =>
    [
        new(FlyTextKind.Healing, ScreenLogRelationKind.LocalPlayer, 1234, HealingActionId),
        new(FlyTextKind.HealingCrit, ScreenLogRelationKind.LocalPlayer, 2468, HealingActionId),
    ];

    // Statuts gagnés puis perdus (de vrais statuts tirés au hasard), puis toutes les sortes de dégâts subis.
    private Sample[] StatusDamageSamples()
    {
        var gained = Pick(buffs!, 3);
        var inflicted = Pick(debuffs!, 2);
        return
        [
            .. gained.Select(id => Status(FlyTextKind.Buff, id)),
            Status(FlyTextKind.BuffFading, gained[0]),
            .. inflicted.Select(id => Status(FlyTextKind.Debuff, id)),
            Status(FlyTextKind.DebuffFading, inflicted[0]),
            Hit(FlyTextKind.Damage, ScreenLogRelationKind.Enemy, 1234),
            Hit(FlyTextKind.DamageCrit, ScreenLogRelationKind.Enemy, 2345),
            Hit(FlyTextKind.DamageDh, ScreenLogRelationKind.Enemy, 1456),
            Hit(FlyTextKind.DamageCritDh, ScreenLogRelationKind.Enemy, 2789),
            Hit(FlyTextKind.AutoAttackOrDot, ScreenLogRelationKind.Enemy, 321),
            Hit(FlyTextKind.Miss, ScreenLogRelationKind.Enemy, 0),
            Hit(FlyTextKind.Dodge, ScreenLogRelationKind.Enemy, 0),
        ];
    }

    private Sample[] TargetSamples() =>
    [
        Hit(FlyTextKind.Damage, ScreenLogRelationKind.LocalPlayer, 1234),
        Hit(FlyTextKind.DamageCrit, ScreenLogRelationKind.LocalPlayer, 2345),
        Hit(FlyTextKind.DamageDh, ScreenLogRelationKind.LocalPlayer, 1456),
        Hit(FlyTextKind.DamageCritDh, ScreenLogRelationKind.LocalPlayer, 2789),
        Hit(FlyTextKind.AutoAttackOrDot, ScreenLogRelationKind.LocalPlayer, 321),
        Hit(FlyTextKind.Miss, ScreenLogRelationKind.LocalPlayer, 0),
        new(FlyTextKind.Debuff, ScreenLogRelationKind.LocalPlayer, (int)Pick(debuffs!, 1)[0], AttackActionId),
    ];

    // Pour un statut, la valeur est le numéro du statut dans la feuille Status du jeu.
    private static Sample Status(FlyTextKind kind, uint statusId) => new(kind, ScreenLogRelationKind.LocalPlayer, (int)statusId, AttackActionId);

    private static Sample Hit(FlyTextKind kind, ScreenLogRelationKind source, int damage) => new(kind, source, damage, AttackActionId);

    private static void Schedule(Recipient recipient, Sample[] samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = samples[i];
            Plugin.Framework.RunOnTick(() => Add(recipient, sample), Spacing * i);
        }
    }

    // Exécuté sur le fil du jeu : le personnage (ou la cible) est relu à ce moment-là, il a pu disparaître entre-temps.
    private static void Add(Recipient recipient, Sample sample)
    {
        IBattleChara? character = recipient == Recipient.Player ? Plugin.ObjectTable.LocalPlayer : Plugin.TargetManager.Target as IBattleChara;
        if (character == null)
            return;

        var entry = new ScreenLogEntry
        {
            ScreenLogKind = (int)sample.Kind,
            SourceRelation = sample.Source,
            TargetRelation = recipient == Recipient.Player ? ScreenLogRelationKind.LocalPlayer : ScreenLogRelationKind.Enemy,
            Option = (byte)ScreenLogOption.Default,
            ActionKind = ActionKind,
            ActionId = sample.ActionId,
            Value1 = sample.Value,
            Value3 = 1,
        };

        var battleChara = (BattleChara*)character.Address;
        ScreenLog.AddScreenLogEntry(&battleChara->ScreenLogManager, &entry);
    }

    // Tirés au hasard à chaque test, pour voir passer toutes sortes de statuts au fil des essais.
    private uint[] Pick(uint[] statuses, int count) =>
        Enumerable.Range(0, count).Select(_ => statuses[random.Next(statuses.Length)]).ToArray();

    // Statuts du jeu qui ont un nom et une icône : catégorie 1 = bénéfique, 2 = néfaste.
    private void LoadStatuses()
    {
        if (buffs != null && debuffs != null)
            return;

        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>()
            .Where(s => s.Icon != 0 && !s.Name.IsEmpty)
            .ToList();
        buffs = sheet.Where(s => s.StatusCategory == 1).Select(s => s.RowId).DefaultIfEmpty(0u).ToArray();
        debuffs = sheet.Where(s => s.StatusCategory == 2).Select(s => s.RowId).DefaultIfEmpty(0u).ToArray();
    }
}
