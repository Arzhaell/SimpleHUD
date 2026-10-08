using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;

namespace FlyingTextModifier;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public PluginLanguage Language { get; set; } = PluginLanguage.Auto;

    /// <summary>
    /// Position choisie pour chaque groupe de textes, en fraction de l'écran (0 à 1) pour suivre les changements de résolution.
    /// Un groupe absent garde la position d'origine du jeu.
    /// </summary>
    public Dictionary<FlyTextGroup, Vector2> Positions { get; set; } = new();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
