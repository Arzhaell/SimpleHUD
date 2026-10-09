using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;
using Newtonsoft.Json;

namespace SimpleHUD;

[Serializable]
public class Configuration : IPluginConfiguration
{
    private const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

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

    /// <summary>Disposition des textes sur le personnage (un bloc, deux ou trois).</summary>
    public PersonalLayout Layout { get; set; } = PersonalLayout.Grouped;

    /// <summary>Position du bloc des statuts quand il est séparé, en fraction de l'écran (null tant qu'il ne l'a jamais été).</summary>
    public Vector2? StatusPosition { get; set; }

    /// <summary>Les autres textes du personnage (EXP, PM, objets obtenus…) ont leur propre cadre.</summary>
    public bool SeparateOther { get; set; }

    /// <summary>Position du cadre des autres textes, en fraction de l'écran (null tant qu'il n'a jamais été à part).</summary>
    public Vector2? OtherPosition { get; set; }

    // Taille de chaque famille de textes, par rapport à celle du jeu (1 = inchangée).
    public float StatusScale { get; set; } = 1f;
    public float HealingScale { get; set; } = 1f;
    public float DamageTakenScale { get; set; } = 1f;
    public float DamageDealtScale { get; set; } = 1f;
    public float OtherScale { get; set; } = 1f;

    /// <summary>Dans l'éditeur d'ATH, position de tous les éléments, et pas seulement de celui sous la souris.</summary>
    public bool ShowAllHudPositions { get; set; }

    // Familles de textes masquées.
    public bool HideStatus { get; set; }
    public bool HideHealing { get; set; }
    public bool HideDamageTaken { get; set; }
    public bool HideDamageDealt { get; set; }
    public bool HideOther { get; set; }

    // Jusqu'à la 1.2.0, une seule taille et une seule case pour tous les dégâts. Lues dans les anciens fichiers pour
    // régler les dégâts subis et infligés, jamais réécrites (pas de getter).
    [JsonProperty("DamageScale")]
    private float LegacyDamageScale
    {
        set => DamageTakenScale = DamageDealtScale = value;
    }

    [JsonProperty("HideDamage")]
    private bool LegacyHideDamage
    {
        set => HideDamageTaken = HideDamageDealt = value;
    }

    public float GetScale(FlyTextCategory category) => category switch
    {
        FlyTextCategory.Status => StatusScale,
        FlyTextCategory.Healing => HealingScale,
        FlyTextCategory.DamageTaken => DamageTakenScale,
        FlyTextCategory.DamageDealt => DamageDealtScale,
        _ => OtherScale,
    };

    public bool IsHidden(FlyTextCategory category) => category switch
    {
        FlyTextCategory.Status => HideStatus,
        FlyTextCategory.Healing => HideHealing,
        FlyTextCategory.DamageTaken => HideDamageTaken,
        FlyTextCategory.DamageDealt => HideDamageDealt,
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
            case FlyTextCategory.DamageTaken:
                HideDamageTaken = hidden;
                break;
            case FlyTextCategory.DamageDealt:
                HideDamageDealt = hidden;
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
            case FlyTextCategory.DamageTaken:
                DamageTakenScale = scale;
                break;
            case FlyTextCategory.DamageDealt:
                DamageDealtScale = scale;
                break;
            default:
                OtherScale = scale;
                break;
        }
    }

    /// <summary>Met à jour un ancien fichier de réglages. Renvoie vrai s'il a changé.</summary>
    public bool Migrate()
    {
        if (Version >= CurrentVersion)
            return false;

        // Avant la version 1, les deux blocs du personnage se déplaçaient séparément : on garde cette disposition
        // si l'un d'eux a été déplacé, plutôt que de les réunir dans un seul cadre.
        if (Version < 1 && Positions.Count > 0)
            Layout = PersonalLayout.HealingSeparate;

        // Version 2 : dégâts subis et infligés séparés, repris de l'ancien réglage commun dès la lecture du fichier.
        Version = CurrentVersion;
        return true;
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
