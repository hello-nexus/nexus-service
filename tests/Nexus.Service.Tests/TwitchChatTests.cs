using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Service.Sockets;
using Nexus.Service.Twitch;
using Xunit;

namespace Nexus.Service.Tests;

public class TwitchIrcParserTests
{
    [Fact]
    public void ParsesPrivmsgWithTags()
    {
        var line = "@badge-info=;badges=broadcaster/1;color=#0000FF;display-name=NovaStreams;emotes=;id=abc-123;mod=0 " +
                   ":novastreams!novastreams@novastreams.tmi.twitch.tv PRIVMSG #novastreams :hello there";

        var parsed = TwitchIrcParser.Parse(line);

        Assert.NotNull(parsed);
        Assert.Equal("PRIVMSG", parsed!.Command);
        Assert.Equal("novastreams", parsed.Channel);
        Assert.Equal("novastreams", parsed.Nick);
        Assert.Equal("hello there", parsed.Parameters);
        Assert.Equal("NovaStreams", parsed.Tags["display-name"]);
        Assert.Equal("#0000FF", parsed.Tags["color"]);
        Assert.Equal("", parsed.Tags["emotes"]);
    }

    [Fact]
    public void KeepsColonsInsideTheMessageBody()
    {
        var line = ":a!a@a.tmi.twitch.tv PRIVMSG #chan :check this out: https://example.com/x";

        var parsed = TwitchIrcParser.Parse(line);

        Assert.Equal("check this out: https://example.com/x", parsed!.Parameters);
    }

    [Fact]
    public void ParsesPingWithNoSource()
    {
        var parsed = TwitchIrcParser.Parse("PING :tmi.twitch.tv");

        Assert.Equal("PING", parsed!.Command);
        Assert.Equal("", parsed.Channel);
        Assert.Equal("tmi.twitch.tv", parsed.Parameters);
    }

    [Fact]
    public void ParsesJoinEcho()
    {
        var parsed = TwitchIrcParser.Parse(":justinfan123!justinfan123@justinfan123.tmi.twitch.tv JOIN #novastreams");

        Assert.Equal("JOIN", parsed!.Command);
        Assert.Equal("novastreams", parsed.Channel);
    }

    [Fact]
    public void ParsesRoomstate()
    {
        // The reply that proves a channel is real; it is identical whether or
        // not the stream is live, so it carries no live signal.
        var parsed = TwitchIrcParser.Parse(
            "@emote-only=0;followers-only=10;r9k=0;room-id=83232866;slow=0;subs-only=0 :tmi.twitch.tv ROOMSTATE #ibai");

        Assert.Equal("ROOMSTATE", parsed!.Command);
        Assert.Equal("ibai", parsed.Channel);
        Assert.Equal("83232866", parsed.Tags["room-id"]);
    }

    [Fact]
    public void UnescapesTagValues()
    {
        var parsed = TwitchIrcParser.Parse(@"@system-msg=hi\sthere\sfriend;flags= PRIVMSG #c :x");

        Assert.Equal("hi there friend", parsed!.Tags["system-msg"]);
        Assert.Equal("", parsed.Tags["flags"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsEmptyLines(string line) => Assert.Null(TwitchIrcParser.Parse(line));
}

public class TwitchChatMessageFactoryTests
{
    private static TwitchIrcLine Privmsg(string body, string emotes = "", string display = "Viewer", string color = "#FF0000")
    {
        var line = $"@color={color};display-name={display};emotes={emotes} " +
                   $":viewer!viewer@viewer.tmi.twitch.tv PRIVMSG #chan :{body}";
        return TwitchIrcParser.Parse(line)!;
    }

    [Fact]
    public void PlainMessageBecomesOneTextFragment()
    {
        var message = TwitchChatMessageFactory.FromPrivmsg(Privmsg("good luck have fun"), 7);

        Assert.NotNull(message);
        Assert.Equal(7, message!.Seq);
        Assert.Equal("Viewer", message.User);
        Assert.Equal("#FF0000", message.Color);
        var fragment = Assert.Single(message.Fragments);
        Assert.Equal("good luck have fun", fragment.Text);
        Assert.Equal("", fragment.EmoteId);
    }

    [Fact]
    public void SplitsEmoteRangesIntoFragments()
    {
        // "Kappa hi" - Kappa occupies code points 0-4.
        var message = TwitchChatMessageFactory.FromPrivmsg(Privmsg("Kappa hi", emotes: "25:0-4"), 1);

        Assert.Collection(
            message!.Fragments,
            f => { Assert.Equal("25", f.EmoteId); Assert.Equal("Kappa", f.Text); },
            f => { Assert.Equal("", f.EmoteId); Assert.Equal(" hi", f.Text); });
    }

    [Fact]
    public void EmoteRangesIndexCodePointsNotUtf16Units()
    {
        // A leading astral emoji is ONE code point but TWO UTF-16 units, so a
        // UTF-16 walk would slice "Kapp" here instead of "Kappa".
        var message = TwitchChatMessageFactory.FromPrivmsg(Privmsg("\U0001F600 Kappa", emotes: "25:2-6"), 1);

        var emote = message!.Fragments.Single(f => f.EmoteId.Length > 0);
        Assert.Equal("Kappa", emote.Text);
    }

    [Fact]
    public void MergesConsecutiveTextRuns()
    {
        var message = TwitchChatMessageFactory.FromPrivmsg(Privmsg("a Kappa b Kappa c", emotes: "25:2-6,10-14"), 1);

        Assert.Collection(
            message!.Fragments,
            f => Assert.Equal("a ", f.Text),
            f => Assert.Equal("25", f.EmoteId),
            f => Assert.Equal(" b ", f.Text),
            f => Assert.Equal("25", f.EmoteId),
            f => Assert.Equal(" c", f.Text));
    }

    [Fact]
    public void DropsTheActionWrapperFromMeMessages()
    {
        var message = TwitchChatMessageFactory.FromPrivmsg(Privmsg("ACTION waves"), 1);

        Assert.Equal("waves", Assert.Single(message!.Fragments).Text);
    }

    [Fact]
    public void KeepsActionEmoteRangesAlignedToTheRawBody()
    {
        // Ranges index the body INCLUDING the wrapper: "ACTION " is 8 code
        // points, so Kappa sits at 8-12.
        var message = TwitchChatMessageFactory.FromPrivmsg(Privmsg("ACTION Kappa", emotes: "25:8-12"), 1);

        var emote = Assert.Single(message!.Fragments);
        Assert.Equal("25", emote.EmoteId);
        Assert.Equal("Kappa", emote.Text);
    }

    [Fact]
    public void FallsBackToTheNickWhenDisplayNameIsBlank()
    {
        var line = TwitchIrcParser.Parse(":viewer!viewer@viewer.tmi.twitch.tv PRIVMSG #chan :hi")!;

        Assert.Equal("viewer", TwitchChatMessageFactory.FromPrivmsg(line, 1)!.User);
    }

    [Theory]
    [InlineData("not-a-colour")]
    [InlineData("#GGGGGG")]
    [InlineData("")]
    public void DropsMalformedColours(string color)
    {
        var message = TwitchChatMessageFactory.FromPrivmsg(Privmsg("hi", color: color), 1);

        Assert.Equal("", message!.Color);
    }

    [Theory]
    [InlineData("25:99-200")]   // out of bounds
    [InlineData("25:5-2")]      // inverted
    [InlineData("25:x-y")]      // non-numeric
    [InlineData("b@d:0-4")]    // id outside the allowed charset
    public void IgnoresMalformedEmoteTagsAndKeepsTheText(string emotes)
    {
        var message = TwitchChatMessageFactory.FromPrivmsg(Privmsg("Kappa hi", emotes: emotes), 1);

        Assert.Equal("Kappa hi", Assert.Single(message!.Fragments).Text);
    }

    [Fact]
    public void RejectsNonPrivmsgLines()
    {
        var line = TwitchIrcParser.Parse("PING :tmi.twitch.tv")!;

        Assert.Null(TwitchChatMessageFactory.FromPrivmsg(line, 1));
    }

    [Theory]
    [InlineData("25", true)]
    [InlineData("emotesv2_abc123", true)]
    [InlineData("a-b_C9", true)]
    [InlineData("../secret", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    public void PinsTheEmoteIdCharset(string id, bool valid) =>
        Assert.Equal(valid, TwitchChatMessageFactory.IsValidEmoteId(id));
}

public class TwitchChannelValidationTests
{
    [Theory]
    [InlineData("novastreams", true)]
    [InlineData("Nova_Streams99", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("newline\r\nJOIN #evil", false)]
    [InlineData("way_too_long_a_channel_name_for_twitch", false)]
    public void PinsTheChannelCharset(string channel, bool valid) =>
        Assert.Equal(valid, TwitchChatHub.IsValidChannel(channel));

    [Fact]
    public void TopicIsLowercased() => Assert.Equal("twitch/chat/novastreams", TwitchChatHub.TopicFor("NovaStreams"));
}

public class TwitchChatHubClearTests
{
    private const string Topic = "twitch/chat/chan";

    private static TwitchIrcLine Privmsg(string body) =>
        TwitchIrcParser.Parse($"@display-name=Viewer :viewer!viewer@viewer.tmi.twitch.tv PRIVMSG #chan :{body}")!;

    private static string Snapshot(MultiplexHub hub)
    {
        Assert.True(hub.TryGetTopicSnapshot(Topic, out var envelope));
        return Encoding.UTF8.GetString(envelope.ToArray());
    }

    [Fact]
    public void ClearEmptiesTheReplayedBufferAndTellsViewers()
    {
        var hub = new MultiplexHub();
        var frames = new List<string>();
        hub.OnBroadcastForTest += (topic, payload) =>
        {
            if (topic == Topic) frames.Add(Encoding.UTF8.GetString(payload.ToArray()));
        };
        using var chat = new TwitchChatHub(hub, NullLogger<TwitchChatHub>.Instance);
        using var sub = hub.AddTestSubscription(Topic);
        chat.Append(Privmsg("days-old message"));
        Assert.Contains("days-old message", Snapshot(hub));

        chat.Clear("chan");

        Assert.DoesNotContain("days-old message", Snapshot(hub));
        var frame = Assert.Single(frames);
        Assert.Matches("\"clearedThrough\":[1-9]", frame);
    }

    [Fact]
    public void ClearOfAnUnwatchedChannelIsANoOp()
    {
        var hub = new MultiplexHub();
        var broadcasts = 0;
        hub.OnBroadcastForTest += (_, _) => broadcasts++;
        using var chat = new TwitchChatHub(hub, NullLogger<TwitchChatHub>.Instance);

        chat.Clear("chan");

        Assert.Equal(0, broadcasts);
    }
}
