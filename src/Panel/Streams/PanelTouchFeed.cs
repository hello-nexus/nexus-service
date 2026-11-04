using System.Threading.Channels;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Live touch contacts for one stream session's render host. Contacts with no subscriber
/// are dropped, and a new subscriber supersedes the previous one (a restarted host leaves a
/// half-open connection behind).
/// </summary>
public sealed class PanelTouchFeed
{
    private const int Capacity = 256;

    private readonly object _lock = new();
    private Channel<PanelTouchContact>? _current;
    private bool _closed;

    /// <summary>Null once the session closed.</summary>
    public ChannelReader<PanelTouchContact>? Subscribe()
    {
        var channel = Channel.CreateBounded<PanelTouchContact>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        Channel<PanelTouchContact>? superseded;
        lock (_lock)
        {
            if (_closed)
            {
                return null;
            }
            superseded = _current;
            _current = channel;
        }
        superseded?.Writer.TryComplete();
        return channel.Reader;
    }

    public void Unsubscribe(ChannelReader<PanelTouchContact> reader)
    {
        lock (_lock)
        {
            if (_current is { } current && ReferenceEquals(current.Reader, reader))
            {
                _current = null;
            }
        }
    }

    public void Publish(PanelTouchContact contact)
    {
        Channel<PanelTouchContact>? channel;
        lock (_lock)
        {
            channel = _current;
        }
        channel?.Writer.TryWrite(contact);
    }

    /// <summary>Ends the live subscription; later subscribers get null.</summary>
    public void Close()
    {
        Channel<PanelTouchContact>? current;
        lock (_lock)
        {
            _closed = true;
            current = _current;
            _current = null;
        }
        current?.Writer.TryComplete();
    }
}
