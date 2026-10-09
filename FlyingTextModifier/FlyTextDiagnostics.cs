#if DEBUG
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FlyingTextModifier;

/// <summary>
/// Version de développement seulement : note dans le journal de Dalamud l'état des 10 groupes de textes
/// (dont ceux au-dessus des cibles) et la position à l'écran de la cible, pour étudier comment le jeu les place.
/// </summary>
internal sealed unsafe class FlyTextDiagnostics : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private readonly Stopwatch clock = Stopwatch.StartNew();
    private TimeSpan lastCheck;
    private string lastLine = string.Empty;

    public FlyTextDiagnostics() => Plugin.Framework.Update += OnUpdate;

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    private void OnUpdate(IFramework framework)
    {
        if (clock.Elapsed - lastCheck < Interval)
            return;
        lastCheck = clock.Elapsed;

        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("_FlyText").Address;
        if (addon == null)
            return;

        var memory = new ReadOnlySpan<byte>(addon, sizeof(AddonFlyText));
        var offset = FlyTextLayout.FindGroupArray(memory, sizeof(AtkUnitBase));
        if (offset < 0)
            return;

        // Par groupe : nombre de textes (0x08), position (0x10), déplacement (0x18) et décalage vertical (0x20).
        var groups = new StringBuilder();
        for (var i = 0; i < FlyTextLayout.GroupCount; i++)
        {
            var group = memory[(offset + (i * FlyTextLayout.GroupSize))..];
            groups.Append($" [{i}] n={BinaryPrimitives.ReadInt64LittleEndian(group[0x08..])}"
                + $" pos=({BinaryPrimitives.ReadSingleLittleEndian(group[0x10..]):F0},{BinaryPrimitives.ReadSingleLittleEndian(group[0x14..]):F0})"
                + $" move=({BinaryPrimitives.ReadSingleLittleEndian(group[0x18..]):F1},{BinaryPrimitives.ReadSingleLittleEndian(group[0x1C..]):F1})"
                + $" off={BinaryPrimitives.ReadSingleLittleEndian(group[0x20..]):F1}");
        }

        // Ne note que les changements, pour ne pas remplir le journal quand rien ne bouge.
        var line = groups.ToString();
        if (line == lastLine)
            return;
        lastLine = line;

        var target = Plugin.TargetManager.Target;
        var targetOnScreen = target != null && Plugin.GameGui.WorldToScreen(target.Position, out var screen)
            ? $"({screen.X:F0},{screen.Y:F0})"
            : "none";
        // Position du calque (décalé pour les textes sur la cible) et de l'addon.
        var root = addon->RootNode;
        var layer = root == null ? "none" : $"({root->X:F0},{root->Y:F0}) addon=({addon->X},{addon->Y}) scale={addon->Scale:F2}";
        Plugin.Log.Information("[diag] target={Target} layer={Layer}{Groups}", targetOnScreen, layer, line);
    }
}
#endif
