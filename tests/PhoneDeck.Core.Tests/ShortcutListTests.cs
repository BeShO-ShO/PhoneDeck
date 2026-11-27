using PhoneDeck.Core;
using Xunit;

namespace PhoneDeck.Core.Tests;

public class ShortcutListTests
{
    [Fact]
    public void ShowsTheChosenHotkeyFirst()
    {
        var list = ShortcutList.Build("Ctrl+Alt+P", true, true, true, true, true);
        Assert.Equal("Ctrl+Alt+P", list[0].Keys);
        Assert.Contains(list, s => s.Keys == "Copilot key");
        Assert.Contains(list, s => s.Keys.StartsWith("F11"));
    }

    [Fact]
    public void LeavesOutWaysThatAreOff()
    {
        var list = ShortcutList.Build("Ctrl+Alt+Q", false, false, false, false, false);
        Assert.DoesNotContain(list, s => s.Keys.Contains("picture"));
        Assert.DoesNotContain(list, s => s.Does == "Back to this PC");
        Assert.Equal(["Ctrl+Alt+Q", "The bar's full-screen button"], list.Select(s => s.Keys));
    }

    [Fact]
    public void KeyboardOnlyHasNoPictureShortcuts()
    {
        var list = ShortcutList.Build("Ctrl+Alt+Q", true, true, true, true, true, hasPicture: false);
        Assert.Equal(["Ctrl+Alt+Q", "Tap Ctrl+Alt on their own", "Copilot key"], list.Select(s => s.Keys));
    }

    [Fact]
    public void NeverListsTargetCycling()
    {
        var list = ShortcutList.Build("Ctrl+Alt+Q", true, true, true, true, true);
        Assert.DoesNotContain(list, s => s.Keys.Contains("CapsLock") || s.Keys.Contains("Ctrl+D+C"));
    }
}
