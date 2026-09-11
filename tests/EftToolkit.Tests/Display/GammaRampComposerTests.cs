using EftToolkit.Core.Configuration;
using EftToolkit.Display.Gamma;

namespace EftToolkit.Tests.Display;

public class GammaRampComposerTests
{
    private static readonly DisplayPresetOptions Medium = new(1.35, 0.01, 1.00);

    [Fact]
    public void Compose_uses_transformed_index_against_original_ramp()
    {
        GammaRamp original = GammaRamp.CreateIdentity();

        GammaRamp result = GammaRampComposer.Compose(original, Medium);

        Assert.True(result.Red[32] > original.Red[32]);
        Assert.True(result.Red.Zip(result.Red.Skip(1)).All(pair => pair.First <= pair.Second));
        Assert.Equal(result.Red, result.Green);
        Assert.Equal(result.Green, result.Blue);
    }

    [Fact]
    public void Constructor_copies_input_channels()
    {
        ushort[] channel = Enumerable.Range(0, 256).Select(index => (ushort)(index * 257)).ToArray();

        GammaRamp ramp = new(channel, channel, channel);
        channel[100] = 0;

        Assert.NotEqual((ushort)0, ramp.Red[100]);
    }

    [Fact]
    public void CreateIdentity_maps_each_index_to_two_hundred_fifty_seven_times_itself()
    {
        GammaRamp identity = GammaRamp.CreateIdentity();

        for (int index = 0; index < GammaRamp.ChannelLength; index++)
        {
            Assert.Equal((ushort)(index * 257), identity.Red[index]);
        }

        Assert.Equal((ushort)0, identity.Red[0]);
        Assert.Equal(ushort.MaxValue, identity.Red[255]);
    }

    [Fact]
    public void Compose_with_identity_parameters_reproduces_a_monotonic_original_exactly()
    {
        GammaRamp original = GammaRampComposer.Compose(GammaRamp.CreateIdentity(), new DisplayPresetOptions(2.2, 0.0, 1.0));

        GammaRamp result = GammaRampComposer.Compose(original, new DisplayPresetOptions(1.0, 0.0, 1.0));

        Assert.Equal(original, result);
    }

    [Fact]
    public void Compose_composes_against_the_original_rather_than_replacing_it()
    {
        // A ramp that is deliberately not the identity: the preset must be applied on top of it.
        ushort[] calibrated = Enumerable.Range(0, 256).Select(index => (ushort)Math.Min(ushort.MaxValue, index * 200)).ToArray();
        GammaRamp original = new(calibrated, calibrated, calibrated);

        GammaRamp result = GammaRampComposer.Compose(original, Medium);

        // Identity parameters would leave channel[10] at 2000; a composed ramp must not jump to 10*257.
        Assert.NotEqual((ushort)(10 * 257), result.Red[10]);
        Assert.True(result.Red[10] >= original.Red[10]);
    }

    [Fact]
    public void Compose_output_is_always_monotonic_even_for_a_non_monotonic_original()
    {
        ushort[] nonMonotonic = Enumerable.Range(0, 256).Select(index => (ushort)(index * 257)).ToArray();
        nonMonotonic[128] = 0;
        nonMonotonic[200] = 5;
        GammaRamp original = new(nonMonotonic, nonMonotonic, nonMonotonic);

        GammaRamp result = GammaRampComposer.Compose(original, Medium);

        AssertNonDecreasing(result.Red);
        AssertNonDecreasing(result.Green);
        AssertNonDecreasing(result.Blue);
    }

    [Theory]
    [InlineData(0.50, 0.00, 0.50)]
    [InlineData(1.00, 0.00, 1.00)]
    [InlineData(1.55, 0.02, 1.00)]
    [InlineData(3.00, 0.20, 1.00)]
    [InlineData(3.00, 0.20, 0.50)]
    [InlineData(0.50, 0.20, 0.75)]
    public void Compose_stays_within_range_and_monotonic_at_every_extreme(double gamma, double shadowLift, double ceiling)
    {
        GammaRamp result = GammaRampComposer.Compose(GammaRamp.CreateIdentity(), new DisplayPresetOptions(gamma, shadowLift, ceiling));

        AssertNonDecreasing(result.Red);
        AssertNonDecreasing(result.Green);
        AssertNonDecreasing(result.Blue);

        // The ceiling is a position in the source ramp, so the highest reachable entry is the
        // interpolated value at that position rounded to the nearest integer.
        ushort highest = (ushort)Math.Ceiling(ceiling * ushort.MaxValue);
        Assert.All(result.Red, value => Assert.InRange(value, ushort.MinValue, highest));
        Assert.All(result.Green, value => Assert.InRange(value, ushort.MinValue, highest));
        Assert.All(result.Blue, value => Assert.InRange(value, ushort.MinValue, highest));
    }

    [Fact]
    public void Compose_honours_a_reduced_output_ceiling()
    {
        GammaRamp result = GammaRampComposer.Compose(GammaRamp.CreateIdentity(), new DisplayPresetOptions(1.0, 0.0, 0.5));

        Assert.Equal(32768, result.Red[255]);
        Assert.Equal((ushort)0, result.Red[0]);
    }

    [Fact]
    public void Compose_applies_shadow_lift_to_the_darkest_index()
    {
        GammaRamp lifted = GammaRampComposer.Compose(GammaRamp.CreateIdentity(), new DisplayPresetOptions(1.0, 0.20, 1.0));
        GammaRamp unlifted = GammaRampComposer.Compose(GammaRamp.CreateIdentity(), new DisplayPresetOptions(1.0, 0.0, 1.0));

        Assert.True(lifted.Red[0] > unlifted.Red[0]);
        Assert.Equal((ushort)0, unlifted.Red[0]);
    }

    [Fact]
    public void Compose_composes_each_channel_independently()
    {
        ushort[] red = Enumerable.Repeat((ushort)1000, 256).ToArray();
        ushort[] green = Enumerable.Repeat((ushort)2000, 256).ToArray();
        ushort[] blue = Enumerable.Repeat((ushort)3000, 256).ToArray();

        GammaRamp result = GammaRampComposer.Compose(new GammaRamp(red, green, blue), Medium);

        Assert.Equal((ushort)1000, result.Red[128]);
        Assert.Equal((ushort)2000, result.Green[128]);
        Assert.Equal((ushort)3000, result.Blue[128]);
    }

    [Fact]
    public void Compose_never_overflows_the_16_bit_output_range()
    {
        ushort[] maximum = Enumerable.Repeat(ushort.MaxValue, 256).ToArray();

        GammaRamp result = GammaRampComposer.Compose(new GammaRamp(maximum, maximum, maximum), new DisplayPresetOptions(3.0, 0.20, 1.00));

        Assert.All(result.Red, value => Assert.Equal(ushort.MaxValue, value));
    }

    [Fact]
    public void Constructor_rejects_channels_that_are_not_exactly_256_entries()
    {
        ushort[] valid = new ushort[256];
        ushort[] tooShort = new ushort[255];
        ushort[] tooLong = new ushort[257];

        Assert.ThrowsAny<ArgumentException>(() => new GammaRamp(tooShort, valid, valid));
        Assert.ThrowsAny<ArgumentException>(() => new GammaRamp(valid, tooLong, valid));
        Assert.ThrowsAny<ArgumentException>(() => new GammaRamp(valid, valid, tooShort));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Compose_rejects_a_gamma_it_cannot_evaluate(double gamma)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GammaRampComposer.Compose(GammaRamp.CreateIdentity(), new DisplayPresetOptions(gamma, 0.0, 1.0)));
    }

    [Fact]
    public void Compose_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => GammaRampComposer.Compose(null!, Medium));
        Assert.Throws<ArgumentNullException>(() => GammaRampComposer.Compose(GammaRamp.CreateIdentity(), null!));
    }

    [Theory]
    [InlineData(0.50, 0.00, 1.00)]
    [InlineData(1.55, 0.02, 1.00)]
    [InlineData(3.00, 0.20, 1.00)]
    [InlineData(3.00, 0.20, 0.50)]
    public void Compose_never_exceeds_the_highest_entry_of_the_original_ramp(double gamma, double shadowLift, double ceiling)
    {
        // A calibrated ramp that tops out well below full scale: interpolation can only ever
        // return a value between two existing entries, so the ceiling of the original must hold.
        ushort[] calibrated = Enumerable.Range(0, 256).Select(index => (ushort)(index * 200)).ToArray();
        GammaRamp original = new(calibrated, calibrated, calibrated);

        GammaRamp result = GammaRampComposer.Compose(original, new DisplayPresetOptions(gamma, shadowLift, ceiling));

        ushort highest = calibrated.Max();
        Assert.All(result.Red, value => Assert.InRange(value, ushort.MinValue, highest));
        Assert.All(result.Green, value => Assert.InRange(value, ushort.MinValue, highest));
        Assert.All(result.Blue, value => Assert.InRange(value, ushort.MinValue, highest));
    }

    [Fact]
    public void Compose_does_not_mutate_the_original_ramp()
    {
        GammaRamp original = GammaRamp.CreateIdentity();
        ushort[] before = original.Red.ToArray();

        GammaRampComposer.Compose(original, new DisplayPresetOptions(3.0, 0.20, 1.0));

        Assert.Equal(before, original.Red);
    }

    [Fact]
    public void Compose_keeps_the_final_index_at_the_ceiling_when_one_is_configured()
    {
        GammaRamp result = GammaRampComposer.Compose(GammaRamp.CreateIdentity(), new DisplayPresetOptions(1.55, 0.02, 1.0));

        Assert.Equal(ushort.MaxValue, result.Red[255]);
    }

    private static void AssertNonDecreasing(IReadOnlyList<ushort> channel)
    {
        for (int index = 1; index < channel.Count; index++)
        {
            Assert.True(
                channel[index] >= channel[index - 1],
                $"index {index} ({channel[index]}) is below index {index - 1} ({channel[index - 1]})");
        }
    }
}
