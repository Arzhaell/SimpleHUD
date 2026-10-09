using System;

namespace FlyingTextModifier;

public enum PluginLanguage
{
    /// <summary>Suit la langue de l'interface Dalamud.</summary>
    Auto,
    English,
    French,
}

/// <summary>Traductions : chaque texte est écrit en anglais et en français à l'endroit où il est utilisé.</summary>
internal static class Loc
{
    public static bool IsFrench { get; private set; }

    public static void Update(PluginLanguage language) => IsFrench = language switch
    {
        PluginLanguage.French => true,
        PluginLanguage.English => false,
        _ => Plugin.PluginInterface.UiLanguage.StartsWith("fr", StringComparison.OrdinalIgnoreCase),
    };

    public static string T(string english, string french) => IsFrench ? french : english;
}
