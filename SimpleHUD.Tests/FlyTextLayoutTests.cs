using System.Buffers.Binary;
using System.Numerics;
using Newtonsoft.Json;

namespace SimpleHUD.Tests;

public class FlyTextLayoutTests
{
    // Imite la mémoire de l'addon : un tableau de groupes valide, placé à arrayOffset.
    private static byte[] FakeAddon(int arrayOffset, int length = 0x2E50)
    {
        var memory = new byte[length];
        for (var i = 0; i < FlyTextLayout.GroupCount; i++)
        {
            var group = arrayOffset + (i * FlyTextLayout.GroupSize);
            BinaryPrimitives.WriteInt16LittleEndian(memory.AsSpan(group + FlyTextLayout.PriorityOffset), (short)(FlyTextLayout.GroupCount - 1 - i));
            BinaryPrimitives.WriteSingleLittleEndian(memory.AsSpan(group + FlyTextLayout.PositionOffset), 900f + i);
            BinaryPrimitives.WriteSingleLittleEndian(memory.AsSpan(group + FlyTextLayout.PositionOffset + 4), 540f);
        }

        return memory;
    }

    [Fact]
    public void FindsTheGroupArray()
    {
        var memory = FakeAddon(0x1A48);

        Assert.Equal(0x1A48, FlyTextLayout.FindGroupArray(memory, 0x238));
        Assert.Equal(new Vector2(901f, 540f), FlyTextLayout.ReadPosition(memory, 0x1A48, FlyTextGroup.StatusDamage));
    }

    [Fact]
    public void IgnoresMemoryBeforeTheStart()
    {
        var memory = FakeAddon(0x100);

        Assert.Equal(-1, FlyTextLayout.FindGroupArray(memory, 0x238));
    }

    [Fact]
    public void RejectsAWrongPriority()
    {
        var memory = FakeAddon(0x1A48);
        BinaryPrimitives.WriteInt16LittleEndian(memory.AsSpan(0x1A48 + (5 * FlyTextLayout.GroupSize) + FlyTextLayout.PriorityOffset), 7);

        Assert.False(FlyTextLayout.IsGroupArray(memory, 0x1A48));
        Assert.Equal(-1, FlyTextLayout.FindGroupArray(memory, 0));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(1e9f)]
    public void RejectsPositionsThatAreNotOnScreen(float x)
    {
        var memory = FakeAddon(0x1A48);
        BinaryPrimitives.WriteSingleLittleEndian(memory.AsSpan(FlyTextLayout.PositionAddress(0x1A48, FlyTextGroup.Healing)), x);

        Assert.False(FlyTextLayout.IsGroupArray(memory, 0x1A48));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0x2E50)]
    public void RejectsOffsetsOutsideTheAddon(int offset)
    {
        var memory = FakeAddon(0x1A48);

        Assert.False(FlyTextLayout.IsGroupArray(memory, offset));
    }

    [Fact]
    public void ConvertsBetweenPixelsAndScreenRatio()
    {
        var screen = new Vector2(2560, 1440);
        var pixels = new Vector2(1254.4f, 720f);

        var ratio = FlyTextLayout.ToRatio(pixels, screen);

        Assert.Equal(0.49f, ratio.X, 4);
        Assert.Equal(0.5f, ratio.Y, 4);
        var resized = FlyTextLayout.ToPixels(ratio, new Vector2(1920, 1080));
        Assert.Equal(940.8f, resized.X, 2);
        Assert.Equal(540f, resized.Y, 2);
    }

    [Fact]
    public void KeepsTheTextOnScreen()
    {
        var screen = new Vector2(1920, 1080);

        Assert.Equal(new Vector2(1f, 0f), FlyTextLayout.ToRatio(new Vector2(5000, -30), screen));
        Assert.Equal(new Vector2(0f, 1080f), FlyTextLayout.ToPixels(new Vector2(-0.2f, 1.5f), screen));
        Assert.Equal(Vector2.Zero, FlyTextLayout.ToRatio(new Vector2(100, 100), Vector2.Zero));
    }

    [Fact]
    public void HealingFrameExtendsLeftAndStatusFrameRight()
    {
        var anchor = new Vector2(1000, 500);
        var size = new Vector2(200, 60);

        Assert.Equal(new Vector2(800, 470), FlyTextLayout.FrameMin(FlyTextGroup.Healing, anchor, size));
        Assert.Equal(new Vector2(1000, 470), FlyTextLayout.FrameMin(FlyTextGroup.StatusDamage, anchor, size));
    }

    [Fact]
    public void TargetOffsetCanBeNegativeButStaysWithinTheScreen()
    {
        var screen = new Vector2(2560, 1440);

        Assert.Equal(new Vector2(-256f, 144f), FlyTextLayout.OffsetToPixels(new Vector2(-0.1f, 0.1f), screen));
        Assert.Equal(new Vector2(-1f, 1f), FlyTextLayout.ClampOffset(new Vector2(-3f, 2f)));
    }

    [Fact]
    public void ShowsWholePixels()
    {
        Assert.Equal((1254, 720), FlyTextLayout.ToWholePixels(new Vector2(0.49f, 0.5f), new Vector2(2560, 1440)));
        Assert.Equal((-120, 40), FlyTextLayout.ToWholePixels(new Vector2(-120f / 2560, 40f / 1440), new Vector2(2560, 1440)));
    }

    [Fact]
    public void FineDragIsSlowerThanTheMouse()
    {
        var mouse = new Vector2(8, -4);

        Assert.Equal(mouse, FlyTextLayout.DragDelta(mouse, fine: false));
        Assert.Equal(new Vector2(2, -1), FlyTextLayout.DragDelta(mouse, fine: true));
    }

    [Fact]
    public void TargetFrameIsCenteredOnItsAnchor()
    {
        Assert.Equal(new Vector2(900, 470), FlyTextLayout.CenteredFrameMin(new Vector2(1000, 500), new Vector2(200, 60)));
    }

    [Theory]
    [InlineData(12, FlyTextCategory.Status)] // Buff
    [InlineData(39, FlyTextCategory.Status)] // DebuffFading
    [InlineData(21, FlyTextCategory.Healing)] // Healing
    [InlineData(34, FlyTextCategory.Healing)] // HealingCrit
    [InlineData(0, FlyTextCategory.DamageTaken)] // AutoAttackOrDot
    [InlineData(7, FlyTextCategory.DamageTaken)] // DamageCritDh
    [InlineData(10, FlyTextCategory.DamageTaken)] // Dodge
    [InlineData(14, FlyTextCategory.Other)] // Exp
    [InlineData(22, FlyTextCategory.Healing)] // MpRegen : avec les soins
    [InlineData(50, FlyTextCategory.Other)] // LootedItem
    [InlineData(-1, FlyTextCategory.Other)]
    public void SortsTextsOnYouIntoFamilies(int kind, FlyTextCategory expected)
    {
        Assert.Equal(expected, FlyTextLayout.Categorize(kind, 1));
    }

    [Fact]
    public void DamageOnOthersIsDealt()
    {
        // Relevé en jeu : acteur 0 = soins sur toi, 1 = statuts et dégâts sur toi, 2 et plus = les autres.
        Assert.Equal(FlyTextCategory.DamageTaken, FlyTextLayout.Categorize(4, 0));
        Assert.Equal(FlyTextCategory.DamageDealt, FlyTextLayout.Categorize(4, 2));
        Assert.Equal(FlyTextCategory.DamageDealt, FlyTextLayout.Categorize(6, 9));
        Assert.Equal(FlyTextCategory.DamageDealt, FlyTextLayout.Categorize(4, null));
        Assert.Equal(FlyTextCategory.Status, FlyTextLayout.Categorize(13, 3));
        Assert.Equal(FlyTextCategory.Healing, FlyTextLayout.Categorize(21, 4));
    }

    [Fact]
    public void SizesStayBetweenHalfAndDouble()
    {
        var configuration = new Configuration();
        configuration.SetScale(FlyTextCategory.Status, 3f);
        configuration.SetScale(FlyTextCategory.Healing, 0.1f);
        configuration.SetScale(FlyTextCategory.DamageTaken, float.NaN);
        configuration.SetScale(FlyTextCategory.DamageDealt, 1.8f);
        configuration.SetScale(FlyTextCategory.Other, 1.5f);

        Assert.Equal(2f, configuration.GetScale(FlyTextCategory.Status));
        Assert.Equal(0.5f, configuration.GetScale(FlyTextCategory.Healing));
        Assert.Equal(1f, configuration.GetScale(FlyTextCategory.DamageTaken));
        Assert.Equal(1.8f, configuration.GetScale(FlyTextCategory.DamageDealt));
        Assert.Equal(1.5f, configuration.GetScale(FlyTextCategory.Other));
    }

    [Fact]
    public void HidesOnlyTheChosenFamilies()
    {
        var configuration = new Configuration();
        configuration.SetHidden(FlyTextCategory.DamageTaken, true);
        configuration.SetHidden(FlyTextCategory.Other, true);

        Assert.True(configuration.IsHidden(FlyTextLayout.Categorize(4, 1))); // Damage sur toi
        Assert.False(configuration.IsHidden(FlyTextLayout.Categorize(4, 2))); // Damage sur la cible
        Assert.True(configuration.IsHidden(FlyTextLayout.Categorize(14, 1))); // Exp
        Assert.False(configuration.IsHidden(FlyTextLayout.Categorize(12, 1))); // Buff
        Assert.False(configuration.IsHidden(FlyTextLayout.Categorize(21, 0))); // Healing
    }

    [Fact]
    public void OldDamageSettingsApplyToDamageTakenAndDealt()
    {
        // Fichier de réglages de la 1.2.0 : une seule taille et une seule case pour tous les dégâts.
        const string old = """{ "Version": 1, "Layout": 3, "DamageScale": 0.5, "HideDamage": true, "StatusScale": 1.15 }""";

        var configuration = JsonConvert.DeserializeObject<Configuration>(old)!;

        Assert.Equal(0.5f, configuration.DamageTakenScale);
        Assert.Equal(0.5f, configuration.DamageDealtScale);
        Assert.True(configuration.HideDamageTaken);
        Assert.True(configuration.HideDamageDealt);
        Assert.True(configuration.Migrate());
        Assert.Equal(PersonalLayout.AllSeparate, configuration.Layout);
        Assert.Equal(1.15f, configuration.StatusScale);

        // Les anciennes clés ne sont jamais réécrites.
        var saved = JsonConvert.SerializeObject(configuration);
        Assert.DoesNotContain("\"DamageScale\"", saved);
        Assert.DoesNotContain("\"HideDamage\"", saved);
        Assert.Contains("\"DamageDealtScale\":0.5", saved);
    }

    [Theory]
    [InlineData(PersonalLayout.Grouped, new[] { PersonalBlock.All })]
    [InlineData(PersonalLayout.HealingSeparate, new[] { PersonalBlock.Healing, PersonalBlock.StatusDamage })]
    [InlineData(PersonalLayout.StatusSeparate, new[] { PersonalBlock.Status, PersonalBlock.HealingDamage })]
    [InlineData(PersonalLayout.AllSeparate, new[] { PersonalBlock.Healing, PersonalBlock.Status, PersonalBlock.Damage })]
    public void EachLayoutHasItsFrames(PersonalLayout layout, PersonalBlock[] expected)
    {
        Assert.Equal(expected, FlyTextLayout.Blocks(layout));
        Assert.Equal(expected, FlyTextLayout.Blocks(layout, separateOther: false));
        Assert.Equal([.. expected, PersonalBlock.Other], FlyTextLayout.Blocks(layout, separateOther: true));
    }

    [Fact]
    public void StatusesAndOtherTextsGoToTheirOwnFrame()
    {
        Assert.Equal(PersonalBlock.Status, FlyTextLayout.SeparateBlock(FlyTextCategory.Status, FlyTextGroup.StatusDamage, PersonalLayout.AllSeparate, false));
        Assert.Null(FlyTextLayout.SeparateBlock(FlyTextCategory.Status, FlyTextGroup.StatusDamage, PersonalLayout.HealingSeparate, true));
        Assert.Null(FlyTextLayout.SeparateBlock(FlyTextCategory.DamageTaken, FlyTextGroup.StatusDamage, PersonalLayout.AllSeparate, true));

        // EXP et objets obtenus, rangés avec les dégâts subis, vont dans le cadre des autres ; la PM reste avec les soins.
        Assert.Equal(PersonalBlock.Other, FlyTextLayout.SeparateBlock(FlyTextCategory.Other, FlyTextGroup.StatusDamage, PersonalLayout.Grouped, true));
        Assert.Null(FlyTextLayout.SeparateBlock(FlyTextCategory.Other, FlyTextGroup.Healing, PersonalLayout.AllSeparate, true));
        Assert.Null(FlyTextLayout.SeparateBlock(FlyTextCategory.Other, FlyTextGroup.StatusDamage, PersonalLayout.AllSeparate, false));
        Assert.Null(FlyTextLayout.SeparateBlock(FlyTextCategory.Healing, FlyTextGroup.Healing, PersonalLayout.AllSeparate, true));

        // Seul le bloc des statuts et dégâts subis a des cadres à part, à empiler chacun de son côté.
        Assert.True(FlyTextLayout.HasSeparateBlocks(FlyTextGroup.StatusDamage, PersonalLayout.Grouped, true));
        Assert.True(FlyTextLayout.HasSeparateBlocks(FlyTextGroup.StatusDamage, PersonalLayout.StatusSeparate, false));
        Assert.False(FlyTextLayout.HasSeparateBlocks(FlyTextGroup.StatusDamage, PersonalLayout.HealingSeparate, false));
        Assert.False(FlyTextLayout.HasSeparateBlocks(FlyTextGroup.Healing, PersonalLayout.AllSeparate, true));
    }

    [Fact]
    public void OnlyTheFirstTwoActorsAreOnYou()
    {
        Assert.Equal(FlyTextGroup.Healing, FlyTextLayout.PlayerGroup(0));
        Assert.Equal(FlyTextGroup.StatusDamage, FlyTextLayout.PlayerGroup(1));
        Assert.Null(FlyTextLayout.PlayerGroup(2));
        Assert.Null(FlyTextLayout.PlayerGroup(null));
    }

    [Fact]
    public void OtherFrameStartsBelowTheDamage()
    {
        var other = FlyTextLayout.DefaultOtherPosition(new Vector2(0.55f, 0.5f));

        Assert.Equal(0.55f, other.X, 4);
        Assert.Equal(0.6f, other.Y, 4);
    }

    [Fact]
    public void GapsAreMeasuredOnTheGameStack()
    {
        // Relevé en jeu : EXP et statut arrivés ensemble ; l'EXP (plus récente) en haut à 908,3, le statut dessous à 952,3.
        Assert.Equal([44f, null], FlyTextLayout.MeasuredGaps([908.3f, 952.3f], null).Select(gap => gap is { } g ? MathF.Round(g, 1) : (float?)null));

        // Relevé en jeu : un dégât arrivé seul à 908,3 a poussé les plus anciens textes juste dessous, à 928,3.
        Assert.Equal(20f, FlyTextLayout.MeasuredGaps([908.3f], 928.3f)[0]!.Value, 3);
        Assert.Null(FlyTextLayout.MeasuredGaps([908.3f], null)[0]);
    }

    [Fact]
    public void EachFrameStacksItsOwnTexts()
    {
        var places = new float[2];

        // Deux statuts arrivés ensemble : l'un au départ, l'autre dessous ; le plus récent des anciens est assez bas.
        var push = FlyTextLayout.StackArrivals(916.4f, [23f, 23f], 980f, places);
        Assert.Equal([916.4f, 939.4f], places.Select(place => MathF.Round(place, 1)));
        Assert.Equal(0f, push);

        // Un statut arrivé seul, le plus récent des anciens statuts 10 px sous le départ : il descend de 13 px.
        push = FlyTextLayout.StackArrivals(916.4f, [23f], 926.4f, places);
        Assert.Equal(13f, push, 3);
        Assert.Equal(0f, FlyTextLayout.StackArrivals(916.4f, [23f], null, places));
    }

    [Fact]
    public void ShieldGainIsApproximatedFromThePercentage()
    {
        // Le jeu donne le bouclier en pourcentage des PV max : 18 % de 102 431 PV ≈ 18 400.
        Assert.Equal(18400, FlyTextLayout.ShieldGain(0, 18, 102431));
        Assert.Equal(5100, FlyTextLayout.ShieldGain(10, 15, 102431));

        // Petits PV : un pour cent fait moins d'une centaine, on garde la valeur.
        Assert.Equal(40, FlyTextLayout.ShieldGain(0, 1, 4000));

        // Bouclier qui baisse (dégâts absorbés) ou PV inconnus : rien à afficher.
        Assert.Null(FlyTextLayout.ShieldGain(18, 12, 102431));
        Assert.Null(FlyTextLayout.ShieldGain(12, 12, 102431));
        Assert.Null(FlyTextLayout.ShieldGain(0, 10, 0));
    }

    [Fact]
    public void ShieldIsNamedAfterTheSpellThatGaveIt()
    {
        // Numéros et noms relevés en jeu (Haima, Panhaima, Holos) ; Galvanisation et Catalyse pour l'exemple.
        var statusNames = new Dictionary<uint, string>
        {
            [2612] = "Haima", [2642] = "Haimatinon", [2613] = "Panhaima", [2643] = "Panhaimatinon",
            [3365] = "Holos", [3003] = "Holos", [297] = "Galvanisation", [1918] = "Catalyse",
        };
        var spellNames = new HashSet<string> { "Haima", "Panhaima", "Holos", "Traité du réconfort" };
        var learned = new Dictionary<uint, string> { [297] = "Traité du réconfort" };
        var none = new Dictionary<uint, string>();

        // Sort lancé sur toi : son nom, même si l'effet porte un autre nom (Traité du réconfort → Galvanisation).
        Assert.Equal("Traité du réconfort", FlyTextLayout.ShieldName("Traité du réconfort", [297, 1918], none, statusNames, spellNames));

        // Effet déjà vu venir d'un sort : le nom de ce sort.
        Assert.Equal("Traité du réconfort", FlyTextLayout.ShieldName(null, [297], learned, statusNames, spellNames));

        // Relevé en jeu : Haima et Panhaima lancés sur soi ne sont pas gardés par le jeu, et donnent deux effets à la fois.
        Assert.Equal("Haima", FlyTextLayout.ShieldName(null, [2642, 2612], none, statusNames, spellNames));
        Assert.Equal("Panhaima", FlyTextLayout.ShieldName(null, [2643, 2613], none, statusNames, spellNames));

        // Deux effets du même nom (Holos) : ce nom ; un seul effet : son nom.
        Assert.Equal("Holos", FlyTextLayout.ShieldName(null, [3365, 3003], none, statusNames, spellNames));
        Assert.Equal("Galvanisation", FlyTextLayout.ShieldName(null, [297], none, statusNames, spellNames));

        // Plusieurs effets dont aucun n'est un sort, ou aucun effet : pas de nom.
        Assert.Null(FlyTextLayout.ShieldName(null, [297, 1918], none, statusNames, spellNames));
        Assert.Null(FlyTextLayout.ShieldName("", [], none, statusNames, spellNames));
    }

    [Fact]
    public void APushIsWhatExceedsTheUsualScroll()
    {
        // Relevé en jeu : un statut qui défilait de 1,35 px par image saute de 755,7 à 928,3 à l'arrivée d'un dégât.
        Assert.Equal(171.25f, FlyTextLayout.GamePush(928.3f - 755.7f, 1.35f), 2);
        Assert.Equal(0f, FlyTextLayout.GamePush(2.5f, 1.35f));
        Assert.Equal(0f, FlyTextLayout.GamePush(-3f, 1.35f));
    }

    [Fact]
    public void FramesMoveTheRightGameBlocks()
    {
        Assert.Equal([FlyTextGroup.Healing, FlyTextGroup.StatusDamage], FlyTextLayout.GroupsOf(PersonalBlock.All));
        Assert.Equal([FlyTextGroup.Healing, FlyTextGroup.StatusDamage], FlyTextLayout.GroupsOf(PersonalBlock.HealingDamage));
        Assert.Equal([FlyTextGroup.StatusDamage], FlyTextLayout.GroupsOf(PersonalBlock.Damage));
        Assert.Empty(FlyTextLayout.GroupsOf(PersonalBlock.Status));
        Assert.True(FlyTextLayout.SeparatesStatuses(PersonalLayout.AllSeparate));
        Assert.False(FlyTextLayout.SeparatesStatuses(PersonalLayout.HealingSeparate));
        Assert.True(FlyTextLayout.LinksGroups(PersonalLayout.StatusSeparate));
        Assert.False(FlyTextLayout.LinksGroups(PersonalLayout.AllSeparate));
    }

    [Fact]
    public void RegroupingKeepsTheGameSpacing()
    {
        var healing = FlyTextLayout.RegroupedHealing(new Vector2(0.7f, 0.3f), new Vector2(0.49f, 0.5f), new Vector2(0.55f, 0.5f));

        Assert.Equal(0.64f, healing.X, 4);
        Assert.Equal(0.3f, healing.Y, 4);
        var status = FlyTextLayout.DefaultStatusPosition(new Vector2(0.7f, 0.3f));
        Assert.Equal(0.7f, status.X, 4);
        Assert.Equal(0.2f, status.Y, 4);
    }

    [Fact]
    public void LinkedFrameCoversBothBlocks()
    {
        var size = new Vector2(200, 60);
        var (min, frameSize) = FlyTextLayout.LinkedFrame(new Vector2(1000, 500), new Vector2(1100, 500), size);

        Assert.Equal(new Vector2(800, 470), min);
        Assert.Equal(new Vector2(500, 60), frameSize);
    }

    [Fact]
    public void OldSettingsKeepTheirTwoBlocks()
    {
        var moved = new Configuration { Version = 0 };
        moved.Positions[FlyTextGroup.Healing] = new Vector2(0.7f, 0.4f);
        var untouched = new Configuration { Version = 0 };

        Assert.True(moved.Migrate());
        Assert.Equal(PersonalLayout.HealingSeparate, moved.Layout);
        Assert.True(untouched.Migrate());
        Assert.Equal(PersonalLayout.Grouped, untouched.Layout);
        Assert.False(new Configuration().Migrate());
    }

    [Fact]
    public void SavedPositionsSurviveTheConfigurationFile()
    {
        var configuration = new Configuration { Language = PluginLanguage.French, TargetOffset = new Vector2(-0.05f, 0.02f) };
        configuration.Positions[FlyTextGroup.StatusDamage] = new Vector2(0.7f, 0.35f);

        // Dalamud enregistre la configuration avec Newtonsoft.Json, types compris.
        var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Objects };
        var reloaded = JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(configuration, settings), settings)!;

        Assert.Equal(PluginLanguage.French, reloaded.Language);
        Assert.Equal(new Vector2(0.7f, 0.35f), reloaded.Positions[FlyTextGroup.StatusDamage]);
        Assert.False(reloaded.Positions.ContainsKey(FlyTextGroup.Healing));
        Assert.Equal(new Vector2(-0.05f, 0.02f), reloaded.TargetOffset);
    }
}
