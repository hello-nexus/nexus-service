using System.Collections.Generic;

namespace Nexus.Service.Models.Twitch;

/// <summary>
/// One run inside a chat message. A text run carries <see cref="Text"/> with an
/// empty <see cref="EmoteId"/>; an emote run carries the emote id plus the
/// source text it replaced, which the client uses as the image's alt text.
/// Splitting server-side keeps Twitch's code-point-indexed emote ranges out of
/// the browser, where UTF-16 string indexing would misplace them.
/// </summary>
public sealed class TwitchChatFragment
{
    public string Text { get; set; } = "";
    public string EmoteId { get; set; } = "";
}

public sealed class TwitchChatMessage
{
    /// <summary>Monotonic per-channel sequence. Clients merge and de-duplicate on this.</summary>
    public long Seq { get; set; }
    public string User { get; set; } = "";
    /// <summary>Twitch-assigned name colour as <c>#RRGGBB</c>; empty when the sender never set one.</summary>
    public string Color { get; set; } = "";
    public List<TwitchChatFragment> Fragments { get; set; } = new();
}

/// <summary>
/// Payload for the <c>twitch/chat/{channel}</c> topic. Live frames carry only
/// the messages appended since the last frame; the snapshot delivered on
/// subscribe carries the whole retained buffer. Both are merged by
/// <see cref="TwitchChatMessage.Seq"/>, so the two can arrive in either order.
/// </summary>
public sealed class TwitchChatFrame
{
    public string Channel { get; set; } = "";
    public bool Connected { get; set; }
    /// <summary>
    /// Whether the channel is a real Twitch account: true once it answers the
    /// JOIN with ROOMSTATE, false once it has stayed silent past the probe
    /// window, null while that is still outstanding. Twitch accepts a JOIN for
    /// any name, so the reply is the only thing that distinguishes a typo from
    /// a quiet channel. It says nothing about the stream being live - an
    /// offline channel answers identically.
    /// </summary>
    public bool? Exists { get; set; }
    /// <summary>On the frame a clear sends, the last seq it removed; clients drop every message at or below it, whatever order frames arrive in. 0 otherwise.</summary>
    public long ClearedThrough { get; set; }
    /// <summary>Empty when the frame only reports a connection-state change.</summary>
    public List<TwitchChatMessage> Messages { get; set; } = new();
}
