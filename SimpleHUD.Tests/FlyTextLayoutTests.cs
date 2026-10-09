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
    [InlineData(0, FlyTextCategory.Damage)] // AutoAttackOrDot
    [InlineData(7, FlyTextCategory.Damage)] // DamageCritDh
    [InlineData(10, FlyTextCategory.Damage)] // Dodge
    [InlineData(14, FlyTextCategory.Other)] // Exp
    [InlineData(22, FlyTextCategory.Other)] // MpRegen
    [InlineData(-1, FlyTextCategory.Other)]
    public void SortsTextsIntoFamilies(int kind, FlyTextCategory expected)
    {
        Assert.Equal(expected, FlyTextLayout.Categorize(kind));
    }

    [Fact]
    public void SizesStayBetweenHalfAndDouble()
    {
        var configuration = new Configuration();
        configuration.SetScale(FlyTextCategory.Status, 3f);
        configuration.SetScale(FlyTextCategory.Healing, 0.1f);
        configuration.SetScale(FlyTextCategory.Damage, float.NaN);
        configuration.SetScale(FlyTextCategory.Other, 1.5f);

        Assert.Equal(2f, configuration.GetScale(FlyTextCategory.Status));
        Assert.Equal(0.5f, configuration.GetScale(FlyTextCategory.Healing));
        Assert.Equal(1f, configuration.GetScale(FlyTextCategory.Damage));
        Assert.Equal(1.5f, configuration.GetScale(FlyTextCategory.Other));
    }

    [Fact]
    public void HidesOnlyTheChosenFamilies()
    {
        var configuration = new Configuration();
        configuration.SetHidden(FlyTextCategory.Damage, true);
        configuration.SetHidden(FlyTextCategory.Other, true);

        Assert.True(configuration.IsHidden(FlyTextLayout.Categorize(4))); // Damage
        Assert.True(configuration.IsHidden(FlyTextLayout.Categorize(14))); // Exp
        Assert.False(configuration.IsHidden(FlyTextLayout.Categorize(12))); // Buff
        Assert.False(configuration.IsHidden(FlyTextLayout.Categorize(21))); // Healing
    }

    [Theory]
    [InlineData(PersonalLayout.Grouped, new[] { PersonalBlock.All })]
    [InlineData(PersonalLayout.HealingSeparate, new[] { PersonalBlock.Healing, PersonalBlock.StatusDamage })]
    [InlineData(PersonalLayout.StatusSeparate, new[] { PersonalBlock.Status, PersonalBlock.HealingDamage })]
    [InlineData(PersonalLayout.AllSeparate, new[] { PersonalBlock.Healing, PersonalBlock.Status, PersonalBlock.Damage })]
    public void EachLayoutHasItsFrames(PersonalLayout layout, PersonalBlock[] expected)
    {
        Assert.Equal(expected, FlyTextLayout.Blocks(layout));
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
    public void ScrollingKeepsTheShift()
    {
        // Relevé en jeu : le texte décalé à (1620, 627) a défilé de 2 vers le bas, sans changer de X.
        Assert.False(FlyTextLayout.GameReplacedText(new Vector2(1620, 629), new Vector2(1620, 627), new Vector2(337, -143)));
    }

    [Fact]
    public void ReplacingPutsTheTextBackInTheGameColumn()
    {
        // Relevé en jeu : réempilé d'un coup, le texte revient dans la colonne du jeu (X = 1283).
        Assert.True(FlyTextLayout.GameReplacedText(new Vector2(1283, 1152), new Vector2(1620, 1010), new Vector2(337, -143)));
    }

    [Fact]
    public void PurelyVerticalShiftUsesTheJump()
    {
        var shift = new Vector2(0, -150);

        Assert.False(FlyTextLayout.GameReplacedText(new Vector2(1283, 452), new Vector2(1283, 450), shift));
        Assert.True(FlyTextLayout.GameReplacedText(new Vector2(1283, 600), new Vector2(1283, 450), shift));
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
