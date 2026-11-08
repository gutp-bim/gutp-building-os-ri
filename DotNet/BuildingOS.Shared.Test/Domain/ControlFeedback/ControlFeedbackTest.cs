using BuildingOS.Shared.Domain.ControlFeedback;

namespace BuildingOS.Shared.Test.Domain.ControlFeedback;

/// <summary>#637: what a control should produce, and how one confirmation is decided.</summary>
public class ControlExpectedValueResolverTest
{
    private static ControlSchema Schema(string type) => new() { DataType = type };

    [Fact]
    public void Number_UsesTheTolerance()
    {
        var e = ControlExpectedValueResolver.Resolve(Schema("number"), 24.0, 0.5, isReleasePoint: false)!;

        Assert.True(e.Matches(24.4));
        Assert.True(e.Matches(23.5));
        Assert.False(e.Matches(24.6));
    }

    [Fact]
    public void BooleanAndEnum_AreExact_EvenWithATolerance()
    {
        Assert.False(ControlExpectedValueResolver.Resolve(Schema("boolean"), 1, 0.5, false)!.Matches(0.6));
        Assert.True(ControlExpectedValueResolver.Resolve(Schema("enum"), 3, 0.5, false)!.Matches(3));
        Assert.False(ControlExpectedValueResolver.Resolve(Schema("enum"), 3, 0.5, false)!.Matches(3.4));
    }

    [Fact]
    public void ReleasePoint_UnknownType_AndBadBoolean_HaveNoExpectedValue()
    {
        Assert.Null(ControlExpectedValueResolver.Resolve(Schema("enum"), 1, 0, isReleasePoint: true));
        Assert.Null(ControlExpectedValueResolver.Resolve(Schema("weird"), 1, 0, false));
        Assert.Null(ControlExpectedValueResolver.Resolve(Schema("boolean"), 2, 0, false));
    }

    [Fact]
    public void NoSchema_ComparesTheRequestedValueExactly()
    {
        var e = ControlExpectedValueResolver.Resolve(null, 5, 0, false)!;
        Assert.True(e.Matches(5));
        Assert.False(e.Matches(5.1));
    }

    [Fact]
    public void FloatingPointNoise_StillMatches()
    {
        Assert.True(ControlExpectedValueResolver.Resolve(Schema("number"), 0.3, 0, false)!.Matches(0.1 + 0.2));
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData(-5d, 60)]
    [InlineData(30d, 30)]
    [InlineData(100000d, 300)]
    public void Window_DefaultsAndIsCapped(double? seconds, double expected)
    {
        Assert.Equal(TimeSpan.FromSeconds(expected), ControlFeedbackConfig.ClampWindow(seconds));
    }
}

public class ControlFeedbackCorrelationTest
{
    private static readonly DateTimeOffset Sent = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly ControlExpectedValue Expect24 = new(24, 0);

    [Fact]
    public void AMatchingReading_DecidesMatchedAtOnce()
    {
        var c = new ControlFeedbackCorrelation(Expect24, Sent);

        var r = c.Observe(new FeedbackReading(24, Sent.AddSeconds(3)));

        Assert.Equal(new ControlFeedbackResult(ControlFeedbackOutcome.Matched, 24), r);
    }

    [Fact]
    public void AReadingFromBeforeTheControl_IsIgnored_EvenIfItMatches()
    {
        // The old value arriving late is not the answer to this control.
        var c = new ControlFeedbackCorrelation(Expect24, Sent);

        Assert.Null(c.Observe(new FeedbackReading(24, Sent.AddSeconds(-1))));
        Assert.Equal(ControlFeedbackOutcome.Timeout, c.Finish().Outcome);
    }

    [Fact]
    public void AMismatch_IsDecidedOnlyAtTheEnd_OnTheLastReading()
    {
        var c = new ControlFeedbackCorrelation(Expect24, Sent);

        Assert.Null(c.Observe(new FeedbackReading(20, Sent.AddSeconds(1)))); // ramping, not decided
        Assert.Null(c.Observe(new FeedbackReading(22, Sent.AddSeconds(2))));

        Assert.Equal(new ControlFeedbackResult(ControlFeedbackOutcome.Mismatch, 22), c.Finish());
    }

    [Fact]
    public void RampingThroughOtherValues_ThenMatching_IsMatched()
    {
        var c = new ControlFeedbackCorrelation(Expect24, Sent);
        c.Observe(new FeedbackReading(20, Sent.AddSeconds(1)));

        Assert.Equal(ControlFeedbackOutcome.Matched, c.Observe(new FeedbackReading(24, Sent.AddSeconds(5)))!.Outcome);
        Assert.Equal(ControlFeedbackOutcome.Matched, c.Finish().Outcome); // stays decided
    }

    [Fact]
    public void NonNumericReadings_DoNotCount()
    {
        var c = new ControlFeedbackCorrelation(Expect24, Sent);
        c.Observe(new FeedbackReading(null, Sent.AddSeconds(1)));
        c.Observe(new FeedbackReading(double.NaN, Sent.AddSeconds(2)));

        Assert.Equal(ControlFeedbackOutcome.Timeout, c.Finish().Outcome);
    }
}
