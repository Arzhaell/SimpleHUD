using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace FlyingTextModifier;

/// <summary>Élément de l'ATH affiché à l'écran : son nom dans le jeu et la position de son coin haut-gauche, en pixels.</summary>
internal readonly record struct HudElement(int Index, string Name, int X, int Y, bool Selected);

/// <summary>
/// Lit à chaque image la position des éléments de l'ATH de la disposition en cours. Lecture seule : rien n'est écrit
/// dans le jeu. Ne lit que quand c'est utile (fenêtre du plugin ou éditeur d'ATH ouverts).
/// </summary>
internal sealed unsafe class HudElements : IDisposable
{
    private readonly Func<bool> isNeeded;
    private readonly object sync = new();
    private HudElement[] elements = [];

    // Noms des éléments (feuille « Hud » du jeu, un par emplacement de disposition), dans la langue du plugin.
    private string[] names = [];
    private bool namesInFrench;

    public HudElements(Func<bool> isNeeded)
    {
        this.isNeeded = isNeeded;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    /// <summary>Éléments affichés à l'écran, dans l'ordre du jeu.</summary>
    public HudElement[] Elements
    {
        get
        {
            lock (sync)
                return elements;
        }
    }

    public void Dispose() => Plugin.Framework.Update -= OnFrameworkUpdate;

    /// <summary>Élément sélectionné dans l'éditeur d'ATH (null si l'éditeur est fermé ou si rien n'est sélectionné).</summary>
    public static AtkUnitBase* SelectedUnit()
    {
        var screen = (AddonHudLayoutScreen*)Plugin.GameGui.GetAddonByName(Plugin.HudLayoutAddonName).Address;
        if (screen == null || !screen->IsVisible || screen->SelectedAddon == null)
            return null;

        return screen->SelectedAddon->SelectedAtkUnit;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var read = isNeeded() ? Read() : [];
        lock (sync)
            elements = read;
    }

    private HudElement[] Read()
    {
        var config = AddonConfig.Instance();
        var unitManager = RaptureAtkUnitManager.Instance();
        if (config == null || config->ActiveDataSet == null || unitManager == null)
            return [];

        var dataSet = config->ActiveDataSet;
        var entries = dataSet->HudLayoutConfigEntries;
        var layoutCount = dataSet->HudLayoutNames.Length;
        if (layoutCount == 0)
            return [];

        var perLayout = entries.Length / layoutCount;
        var selected = SelectedUnit();
        var elementNames = Names();
        var read = new List<HudElement>();
        for (var i = 0; i < elementNames.Length; i++)
        {
            var index = HudLayout.EntryIndex(dataSet->CurrentHudLayout, i, perLayout, entries.Length);
            if (index < 0)
                break;

            // Emplacements vides du jeu (sans nom) ou jamais remplis.
            var hash = entries[index].AddonNameHash;
            if (hash == 0 || elementNames[i].Length == 0)
                continue;

            var unit = unitManager->GetAddonByNameHash(hash);
            if (unit == null || !unit->IsVisible)
                continue;

            read.Add(new HudElement(i, elementNames[i], unit->X, unit->Y, unit == selected));
        }

        return read.ToArray();
    }

    private string[] Names()
    {
        if (names.Length > 0 && namesInFrench == Loc.IsFrench)
            return names;

        namesInFrench = Loc.IsFrench;
        var sheet = Plugin.DataManager.GetExcelSheet<Hud>(Loc.IsFrench ? ClientLanguage.French : ClientLanguage.English);
        var read = new string[sheet.Count == 0 ? 0 : (int)sheet.Max(row => row.RowId) + 1];
        Array.Fill(read, string.Empty);
        foreach (var row in sheet)
            read[row.RowId] = row.Unknown0.ExtractText();

        names = read;
        return names;
    }
}
