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

    /// <summary>
    /// Décalage des textes affichés sur la cible, en fraction de l'écran (peut être négatif).
    /// Ces textes suivent toujours la cible : seul l'écart avec elle change.
    /// </summary>
    public Vector2 TargetOffset { get; set; } = Vector2.Zero;

    // Taille de chaque famille de textes, par rapport à celle du jeu (1 = inchangée).
    public float StatusScale { get; set; } = 1f;
    public float HealingScale { get; set; } = 1f;
    public float DamageScale { get; set; } = 1f;
    public float OtherScale { get; set; } = 1f;

    // Familles de textes masquées.
    public bool HideStatus { get; set; }
    public bool HideHealing { get; set; }
    public bool HideDamage { get; set; }
    public bool HideOther { get; set; }

    public float GetScale(FlyTextCategory category) => category switch
    {
        FlyTextCategory.Status => StatusScale,
        FlyTextCategory.Healing => HealingScale,
        FlyTextCategory.Damage => DamageScale,
        _ => OtherScale,
    };

    public bool IsHidden(FlyTextCategory category) => category switch
    {
        FlyTextCategory.Status => HideStatus,
        FlyTextCategory.Healing => HideHealing,
        FlyTextCategory.Damage => HideDamage,
        _ => HideOther,
    };

    public void SetHidden(FlyTextCategory category, bool hidden)
    {
        switch (category)
        {
            case FlyTextCategory.Status:
                HideStatus = hidden;
                break;
            case FlyTextCategory.Healing:
                HideHealing = hidden;
                break;
            case FlyTextCategory.Damage:
                HideDamage = hidden;
                break;
            default:
                HideOther = hidden;
                break;
        }
    }

    public void SetScale(FlyTextCategory category, float scale)
    {
        scale = FlyTextLayout.ClampScale(scale);
        switch (category)
        {
            case FlyTextCategory.Status:
                StatusScale = scale;
                break;
            case FlyTextCategory.Healing:
                HealingScale = scale;
                break;
            case FlyTextCategory.Damage:
                DamageScale = scale;
                break;
            default:
                OtherScale = scale;
                break;
        }
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
