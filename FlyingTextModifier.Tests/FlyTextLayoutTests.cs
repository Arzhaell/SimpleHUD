using System.Buffers.Binary;
using System.Numerics;
using Newtonsoft.Json;

namespace FlyingTextModifier.Tests;

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
    public void SavedPositionsSurviveTheConfigurationFile()
    {
        var configuration = new Configuration { Language = PluginLanguage.French };
        configuration.Positions[FlyTextGroup.StatusDamage] = new Vector2(0.7f, 0.35f);

        // Dalamud enregistre la configuration avec Newtonsoft.Json, types compris.
        var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Objects };
        var reloaded = JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(configuration, settings), settings)!;

        Assert.Equal(PluginLanguage.French, reloaded.Language);
        Assert.Equal(new Vector2(0.7f, 0.35f), reloaded.Positions[FlyTextGroup.StatusDamage]);
        Assert.False(reloaded.Positions.ContainsKey(FlyTextGroup.Healing));
    }
}
