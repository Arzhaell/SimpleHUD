using System;
using Dalamud.Game.Gui.FlyText;
using Dalamud.Game.Text.SeStringHandling;

namespace SimpleHUD;

/// <summary>Empêche d'apparaître les familles de textes masquées (outil prévu par Dalamud pour ça).</summary>
internal sealed class FlyTextHider : IDisposable
{
    private readonly Configuration configuration;
    private readonly FlyTextNodes nodes;

    public FlyTextHider(Configuration configuration, FlyTextNodes nodes)
    {
        this.configuration = configuration;
        this.nodes = nodes;
        Plugin.FlyTextGui.FlyTextCreated += OnFlyTextCreated;
    }

    public void Dispose() => Plugin.FlyTextGui.FlyTextCreated -= OnFlyTextCreated;

    // Appelé pendant la création du texte : l'acteur qui le reçoit (toi ou un autre) est alors connu.
    private void OnFlyTextCreated(
        ref FlyTextKind kind, ref int val1, ref int val2, ref SeString text1, ref SeString text2,
        ref uint color, ref uint icon, ref uint damageTypeIcon, ref float yOffset, ref bool handled)
    {
        if (configuration.IsHidden(FlyTextLayout.Categorize((int)kind, nodes.CurrentActor)))
            handled = true;
    }
}
