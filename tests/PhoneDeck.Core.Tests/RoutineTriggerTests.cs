using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

public class RoutineTriggerTests
{
    [Fact]
    public void FiresOnceWhenAPhoneConnects()
    {
        var trigger = new RoutineTrigger();
        Assert.Equal(["phone"], trigger.Update(["phone"], 1_000));
        Assert.Empty(trigger.Update(["phone"], 2_000));   // still connected
        Assert.Empty(trigger.Update(["phone"], 100_000));
    }

    [Fact]
    public void AQuickReconnectDoesNotFireAgain()
    {
        var trigger = new RoutineTrigger();
        trigger.Update(["phone"], 1_000);
        trigger.Update([], 5_000);
        Assert.Empty(trigger.Update(["phone"], 30_000));  // 29 s after firing
    }

    [Fact]
    public void ALaterReconnectFiresAgain()
    {
        var trigger = new RoutineTrigger();
        trigger.Update(["phone"], 1_000);
        trigger.Update([], 5_000);
        Assert.Equal(["phone"], trigger.Update(["phone"], 61_000));
    }

    [Fact]
    public void EachPhoneIsTrackedOnItsOwn()
    {
        var trigger = new RoutineTrigger();
        trigger.Update(["a"], 1_000);
        Assert.Equal(["b"], trigger.Update(["a", "b"], 2_000));
    }

    [Fact]
    public void AConnectRightAfterAnAskedForTriggerDoesNotFireAgain()
    {
        var trigger = new RoutineTrigger();
        trigger.NoteFired("phone", 1_000);
        Assert.Empty(trigger.Update(["phone"], 20_000));
    }

    [Fact]
    public void ForgetLetsAQuickReconnectFire()
    {
        var trigger = new RoutineTrigger();
        trigger.Update(["phone"], 1_000);
        trigger.Update([], 5_000);
        trigger.Forget("phone");
        Assert.Equal(["phone"], trigger.Update(["phone"], 10_000));
    }

    [Fact]
    public void ResetCountsConnectedPhonesAsNew()
    {
        var trigger = new RoutineTrigger();
        trigger.Update(["phone"], 1_000);
        trigger.Reset();
        Assert.Equal(["phone"], trigger.Update(["phone"], 70_000));
    }

    private static readonly (string, string)[] Devices =
    [
        (@"\\?\BTHENUM#{0000110a}_VID&00010075#5&1&0&112233445566_C00000000#{6994}\SNK", "Phone A"),
        (@"\\?\BTHENUM#{0000110a}_VID&00010075#5&1&0&AABBCCDDEEFF_C00000000#{6994}\SNK", "Phone B"),
    ];

    [Fact]
    public void FindsTheAudioDeviceByAddress() =>
        Assert.Equal(Devices[1].Item1, RoutineTrigger.FindAudioDevice("aa:bb:cc:dd:ee:ff", "Other name", Devices));

    [Fact]
    public void FallsBackToTheName() =>
        Assert.Equal(Devices[0].Item1, RoutineTrigger.FindAudioDevice("01:02:03:04:05:06", "phone a", Devices));

    [Fact]
    public void FindsNothingWhenNeitherFits() =>
        Assert.Null(RoutineTrigger.FindAudioDevice("01:02:03:04:05:06", null, Devices));

    [Fact]
    public void AnAmbiguousNameFindsNothing() =>
        Assert.Null(RoutineTrigger.FindAudioDevice("01:02:03:04:05:06", "Same",
            [("id1", "Same"), ("id2", "Same")]));
}
