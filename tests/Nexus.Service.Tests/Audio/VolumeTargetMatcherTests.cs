using System.Collections.Generic;
using Nexus.Service.Activity;
using Nexus.Service.Models.Activity;
using Xunit;

namespace Nexus.Service.Tests.Audio;

public class VolumeTargetMatcherTests
{
    [Fact]
    public void AliasMatchesEdgeToMsedge()
    {
        var strips = new[] { Strip("msedge", "Microsoft Edge") };

        var hit = VolumeTargetMatcher.Match("Edge", strips);

        Assert.Same(strips[0], hit);
    }

    [Theory]
    [InlineData("Music")]
    [InlineData("Media Player")]
    public void AliasMatchesMusicAndMediaPlayerToEitherUwpProcessName(string source)
    {
        var strips = new[] { Strip("microsoft.media.player", "Media Player") };
        Assert.Same(strips[0], VolumeTargetMatcher.Match(source, strips));

        var uiStrips = new[] { Strip("music.ui", "Music") };
        Assert.Same(uiStrips[0], VolumeTargetMatcher.Match(source, uiStrips));
    }

    [Fact]
    public void AliasMatchesMoviesAndTvToVideoUi()
    {
        var strips = new[] { Strip("video.ui", "Movies & TV") };

        Assert.Same(strips[0], VolumeTargetMatcher.Match("Movies & TV", strips));
    }

    [Fact]
    public void FallsBackToNormalizedIdWhenNoAliasApplies()
    {
        var strips = new[] { Strip("spotify", "Spotify") };

        Assert.Same(strips[0], VolumeTargetMatcher.Match("Spotify", strips));
    }

    [Fact]
    public void StripsTrailingDedupeSuffixBeforeMatching()
    {
        var strips = new[] { Strip("chrome", "Google Chrome") };

        Assert.Same(strips[0], VolumeTargetMatcher.Match("Chrome 2", strips));
    }

    [Fact]
    public void FallsBackToNormalizedNameWhenIdDiffers()
    {
        var strips = new[] { Strip("proc123", "Firefox") };

        Assert.Same(strips[0], VolumeTargetMatcher.Match("Firefox", strips));
    }

    [Fact]
    public void PrefixMatchRequiresAtLeastFourNormalizedIdCharacters()
    {
        var strips = new[] { Strip("applemusic", "Apple Music") };

        Assert.Same(strips[0], VolumeTargetMatcher.Match("AppleMusicWin", strips));
    }

    [Fact]
    public void PrefixMatchRejectsAShortId()
    {
        // "vlc" normalizes to 3 chars, under the 4-char floor, so a source
        // that merely starts with it must not match by that tier alone.
        var strips = new[] { Strip("vlc", "VLC") };

        Assert.Null(VolumeTargetMatcher.Match("vlcstreamerapp", strips));
    }

    [Fact]
    public void SkipsTheSystemSoundsStrip()
    {
        var strips = new[] { Strip(AudioMixerIds.SystemSounds, "System Sounds") };

        Assert.Null(VolumeTargetMatcher.Match("System Sounds", strips));
    }

    [Fact]
    public void NoMatchReturnsNull()
    {
        var strips = new[] { Strip("spotify", "Spotify") };

        Assert.Null(VolumeTargetMatcher.Match("Discord", strips));
    }

    [Fact]
    public void EmptyOrBlankSourceReturnsNull()
    {
        var strips = new[] { Strip("spotify", "Spotify") };

        Assert.Null(VolumeTargetMatcher.Match("", strips));
        Assert.Null(VolumeTargetMatcher.Match("   ", strips));
    }

    private static AudioSessionDto Strip(string id, string name) => new() { Id = id, Name = name };
}
