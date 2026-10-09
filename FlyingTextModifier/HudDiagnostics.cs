using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FlyingTextModifier;

/// <summary>
/// Branche d'essai : note dans le journal de Dalamud (lignes « [hud] ») ce que fait l'éditeur d'ATH du jeu, pour
/// préparer le réglage au pixel. À l'ouverture de l'éditeur, l'état de chaque élément de la disposition ; pendant
/// l'édition, l'élément sélectionné ; à la fermeture, les éléments qui ont changé. Lecture seule, et seulement quand
/// l'éditeur est ouvert et que quelque chose change (les relevés des textes défilants restent réservés au Debug).
/// </summary>
internal sealed unsafe class HudDiagnostics : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private readonly Stopwatch clock = Stopwatch.StartNew();
    private TimeSpan lastCheck;
    private bool wasOpen;
    private string lastSelected = string.Empty;
    private Dictionary<int, string> atOpening = new();

    public HudDiagnostics() => Plugin.Framework.Update += OnUpdate;

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    private void OnUpdate(IFramework framework)
    {
        if (clock.Elapsed - lastCheck < Interval)
            return;
        lastCheck = clock.Elapsed;

        var screen = (AddonHudLayoutScreen*)Plugin.GameGui.GetAddonByName(Plugin.HudLayoutAddonName).Address;
        var open = screen != null && screen->IsVisible;
        if (open != wasOpen)
        {
            wasOpen = open;
            var snapshot = Snapshot(out var header);
            Plugin.Log.Information("[hud] editor {State} {Header}", open ? "opened" : "closed", header);
            foreach (var (index, line) in snapshot)
            {
                // À la fermeture, seulement ce qui a changé depuis l'ouverture.
                if (open || !atOpening.TryGetValue(index, out var before) || before != line)
                    Plugin.Log.Information("[hud] {Line}", line);
            }

            atOpening = snapshot;
            lastSelected = string.Empty;
        }

        if (open)
            LogSelected(screen);
    }

    // Par élément de la disposition en cours : ce que le jeu enregistre, et l'addon affiché.
    private static Dictionary<int, string> Snapshot(out string header)
    {
        var lines = new Dictionary<int, string>();
        var config = AddonConfig.Instance();
        var unitManager = RaptureAtkUnitManager.Instance();
        var device = Device.Instance();
        if (config == null || config->ActiveDataSet == null || unitManager == null)
        {
            header = "no layout";
            return lines;
        }

        var dataSet = config->ActiveDataSet;
        var entries = dataSet->HudLayoutConfigEntries;
        var layoutCount = dataSet->HudLayoutNames.Length;
        var perLayout = layoutCount == 0 ? 0 : entries.Length / layoutCount;
        header = $"layout={dataSet->CurrentHudLayout} entries={entries.Length} layouts={layoutCount}"
            + $" screen={(device == null ? "?" : $"{device->Width}x{device->Height}")}";

        var anchors = unitManager->AtkUnitManager.HudAnchoringTable;
        for (var i = 0; i < perLayout; i++)
        {
            var index = HudLayout.EntryIndex(dataSet->CurrentHudLayout, i, perLayout, entries.Length);
            if (index < 0)
                break;

            var entry = entries[index];
            if (entry.AddonNameHash == 0)
                continue;

            var line = $"[{i}] hash={entry.AddonNameHash:X8} entry=({entry.X:F2},{entry.Y:F2}) scale={entry.Scale:F2}"
                + $" size={entry.Width}x{entry.Height} flags={entry.ElementFlags:X} bytes={entry.ByteValue1},{entry.ByteValue2},{entry.ByteValue3}"
                + $" alpha={entry.Alpha} has={entry.HasValue} open={entry.IsOpen}";

            var unit = unitManager->GetAddonByNameHash(entry.AddonNameHash);
            if (unit != null)
            {
                var root = unit->RootNode;
                var anchorIndex = unit->HudAnchoringInfoIndex;
                var anchor = anchorIndex >= 0 && anchorIndex < anchors.Length
                    ? $"{anchorIndex}:{anchors[anchorIndex].AlignmentType}@{anchors[anchorIndex].NormalizedCoordinate:F3}"
                    : $"{anchorIndex}";
                line += $" unit={unit->NameString} pos=({unit->X},{unit->Y}) scale={unit->Scale:F2} visible={unit->IsVisible}"
                    + $" root={(root == null ? "none" : $"{root->Width}x{root->Height}")} anchor={anchor}";
            }

            lines[i] = line;
        }

        return lines;
    }

    // Élément sélectionné : addon, cadre de l'éditeur, valeur enregistrée et état « à enregistrer ».
    private void LogSelected(AddonHudLayoutScreen* screen)
    {
        var info = screen->SelectedAddon;
        var line = "selected=none";
        if (info != null && info->SelectedAtkUnit != null)
        {
            var unit = info->SelectedAtkUnit;
            var overlay = screen->SelectedOverlayNode;
            var agent = AgentHUDLayout.Instance();
            line = $"selected={unit->NameString} pos=({unit->X},{unit->Y}) scale={unit->Scale:F2}"
                + $" offset=({info->XOffset},{info->YOffset}) overlaySize={info->OverlayWidth}x{info->OverlayHeight}"
                + $" slot={info->Slot} changed={info->PositionHasChanged} flags={info->Flags:X}"
                + $" overlay={(overlay == null ? "none" : $"({overlay->AtkResNode.X:F1},{overlay->AtkResNode.Y:F1}) {overlay->AtkResNode.Width}x{overlay->AtkResNode.Height}")}"
                + $" entry={SelectedEntry(unit)} needToSave={(agent == null ? "?" : agent->NeedToSave.ToString())}";
        }

        if (line == lastSelected)
            return;
        lastSelected = line;
        Plugin.Log.Information("[hud] {Line}", line);
    }

    // Valeur enregistrée dans la disposition en cours pour cet addon.
    private static string SelectedEntry(AtkUnitBase* unit)
    {
        var config = AddonConfig.Instance();
        var unitManager = RaptureAtkUnitManager.Instance();
        if (config == null || config->ActiveDataSet == null || unitManager == null)
            return "?";

        var dataSet = config->ActiveDataSet;
        var entries = dataSet->HudLayoutConfigEntries;
        var layoutCount = dataSet->HudLayoutNames.Length;
        var perLayout = layoutCount == 0 ? 0 : entries.Length / layoutCount;
        for (var i = 0; i < perLayout; i++)
        {
            var index = HudLayout.EntryIndex(dataSet->CurrentHudLayout, i, perLayout, entries.Length);
            if (index < 0)
                break;

            var entry = entries[index];
            if (entry.AddonNameHash != 0 && unitManager->GetAddonByNameHash(entry.AddonNameHash) == unit)
                return $"[{i}]({entry.X:F2},{entry.Y:F2}) scale={entry.Scale:F2}";
        }

        return "none";
    }
}
