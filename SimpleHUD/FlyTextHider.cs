using System;
using Dalamud.Game.Gui.FlyText;
using Dalamud.Game.Text.SeStringHandling;

namespace SimpleHUD;

/// <summary>Empêche d'apparaître les familles de textes masquées (outil prévu par Dalamud pour ça).</summary>
internal sealed class FlyTextHider : IDisposable
{
    private readonly Configuration configuration;

    public FlyTextHider(Configuration configuration)
    {
        this.configuration = configuration;
        Plugin.FlyTextGui.FlyTextCreated += OnFlyTextCreated;
    }

    public void Dispose() => Plugin.FlyTextGui.FlyTextCreated -= OnFlyTextCreated;

    private void OnFlyTextCreated(
        ref FlyTextKind kind, ref int val1, ref int val2, ref SeString text1, ref SeString text2,
        ref uint color, ref uint icon, ref uint damageTypeIcon, ref float yOffset, ref bool handled)
    {
        if (configuration.IsHidden(FlyTextLayout.Categorize((int)kind)))
            handled = true;
    }
}
