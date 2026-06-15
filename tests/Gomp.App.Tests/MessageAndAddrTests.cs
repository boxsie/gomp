using Gomp.App.ViewModels;
using Gomp.Client;
using Xunit;

namespace Gomp.App.Tests;

public sealed class MessageViewModelTests
{
    [Fact]
    public void Verified_ShowsGreenCheckBadge()
    {
        var vm = MessageViewModel.FromPost(Posts.Post("Epeer", "hi", 3, PostTrust.Verified), "Eself");
        Assert.True(vm.IsVerified);
        Assert.True(vm.ShowTrustBadge);
        Assert.Equal("✓", vm.TrustGlyph);
        Assert.Equal("#2DC97E", vm.TrustColorHex);
        Assert.False(vm.IsSelf);
        Assert.Equal(3, vm.Seq);
        Assert.Equal("hi", vm.Content);
    }

    [Fact]
    public void Unverified_HidesBadge()
    {
        var vm = MessageViewModel.FromPost(Posts.Post("Epeer", "yo", trust: PostTrust.Unverified), "Eself");
        Assert.False(vm.ShowTrustBadge);
        Assert.Equal("", vm.TrustGlyph);
    }

    [Fact]
    public void Forged_ShowsRedWarning()
    {
        var vm = MessageViewModel.FromPost(Posts.Post("Epeer", "nope", trust: PostTrust.Forged), "Eself");
        Assert.True(vm.IsForged);
        Assert.True(vm.ShowTrustBadge);
        Assert.Equal("⚠", vm.TrustGlyph);
        Assert.Equal("#EB0400", vm.TrustColorHex);
    }

    [Fact]
    public void OwnPost_IsSelf_AndWearsAccentColour()
    {
        var vm = MessageViewModel.FromPost(Posts.Post("Eself", "mine"), "Eself");
        Assert.True(vm.IsSelf);
        Assert.Equal("#BF94FF", vm.NameColorHex);
    }

    [Fact]
    public void System_IsCentredLineWithNoBadge()
    {
        var vm = MessageViewModel.System("removed someone");
        Assert.True(vm.IsSystem);
        Assert.False(vm.ShowTrustBadge);
        Assert.Equal("removed someone", vm.Content);
    }
}

public sealed class AddrTests
{
    [Fact]
    public void Short_ElidesLongAddresses()
    {
        var s = Addr.Short("AbcdefGHIJKLmnopQRST");
        Assert.Contains("…", s);
        Assert.StartsWith("Abcdef", s);
        Assert.EndsWith("QRST", s);
    }

    [Fact]
    public void Short_PassesThroughShortOrEmpty()
    {
        Assert.Equal("anon", Addr.Short(""));
        Assert.Equal("short", Addr.Short("short"));
    }

    [Fact]
    public void NameColor_IsStableAndInPalette()
    {
        var a = Addr.NameColor("Epeer");
        var b = Addr.NameColor("Epeer");
        Assert.Equal(a, b);
        Assert.StartsWith("#", a);
        Assert.Equal(7, a.Length);
    }
}
