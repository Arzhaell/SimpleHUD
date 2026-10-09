using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Gui.FlyText;

namespace FlyingTextModifier;

/// <summary>Faux textes affichés sur le personnage pour voir où ils apparaissent, avec de vrais statuts du jeu.</summary>
internal sealed class TestTexts
{
    // Acteur 1 = le personnage du joueur.
    private const uint LocalPlayer = 1;

    // Blanc opaque, quel que soit l'ordre des composantes attendu par le jeu.
    private const uint Color = 0xFFFFFFFF;

    // Les textes partent les uns après les autres pour ne pas se chevaucher.
    private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(300);

    private readonly Random random = new();
    private List<Status>? buffs;
    private List<Status>? debuffs;

    private readonly record struct Status(string Name, uint Icon);

    private readonly record struct Sample(FlyTextKind Kind, uint Value, string Text, uint Icon);

    public void Show(IEnumerable<FlyTextGroup> groups)
    {
        var samples = groups.SelectMany(Samples).ToList();
        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            Plugin.Framework.RunOnTick(
                () => Plugin.FlyTextGui.AddFlyText(sample.Kind, LocalPlayer, sample.Value, 0, sample.Text, string.Empty, Color, sample.Icon, 0),
                Spacing * i);
        }
    }

    private IEnumerable<Sample> Samples(FlyTextGroup group)
    {
        if (group == FlyTextGroup.Healing)
        {
            var heal = Loc.T("Healing (test)", "Soin (test)");
            return [new(FlyTextKind.Healing, 1234, heal, 0), new(FlyTextKind.HealingCrit, 2468, heal, 0)];
        }

        LoadStatuses();
        var gained = Pick(buffs!, 3);
        var inflicted = Pick(debuffs!, 2);
        var hit = Loc.T("Attack (test)", "Attaque (test)");

        // Statuts gagnés puis perdus, puis toutes les sortes de dégâts subis.
        return gained.Select(s => new Sample(FlyTextKind.Buff, 0, s.Name, s.Icon))
            .Append(new Sample(FlyTextKind.BuffFading, 0, gained[0].Name, gained[0].Icon))
            .Concat(inflicted.Select(s => new Sample(FlyTextKind.Debuff, 0, s.Name, s.Icon)))
            .Append(new Sample(FlyTextKind.DebuffFading, 0, inflicted[0].Name, inflicted[0].Icon))
            .Concat(
            [
                new(FlyTextKind.Damage, 1234, hit, 0),
                new(FlyTextKind.DamageCrit, 2345, hit, 0),
                new(FlyTextKind.DamageDh, 1456, hit, 0),
                new(FlyTextKind.DamageCritDh, 2789, hit, 0),
                new(FlyTextKind.AutoAttackOrDot, 321, string.Empty, 0),
                new(FlyTextKind.Miss, 0, hit, 0),
                new(FlyTextKind.Dodge, 0, hit, 0),
            ]);
    }

    // Tirés au hasard à chaque test, pour voir passer toutes sortes de statuts au fil des essais.
    private Status[] Pick(List<Status> statuses, int count) =>
        Enumerable.Range(0, count).Select(_ => statuses[random.Next(statuses.Count)]).ToArray();

    // Statuts du jeu (dans sa langue) qui ont un nom et une icône : catégorie 1 = bénéfique, 2 = néfaste.
    private void LoadStatuses()
    {
        if (buffs != null && debuffs != null)
            return;

        buffs = [];
        debuffs = [];
        foreach (var status in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>())
        {
            var name = status.Name.ExtractText();
            if (status.Icon == 0 || string.IsNullOrWhiteSpace(name))
                continue;

            if (status.StatusCategory == 1)
                buffs.Add(new Status(name, status.Icon));
            else if (status.StatusCategory == 2)
                debuffs.Add(new Status(name, status.Icon));
        }

        // Données illisibles : un texte générique suffit pour voir l'emplacement.
        if (buffs.Count == 0)
            buffs.Add(new Status(Loc.T("Status effect (test)", "Effet de statut (test)"), 0));
        if (debuffs.Count == 0)
            debuffs.Add(new Status(Loc.T("Status effect (test)", "Effet de statut (test)"), 0));
    }
}
