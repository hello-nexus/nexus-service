using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nexus.Service.Peripherals.Corsair.Galleon;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Corsair;

/// <summary>
/// Onboard saved-colors writer against a simulated board that keeps an object store and answers in the
/// layout the hardware probe captured (w3 command echo, w4 status, data from w5). Verifies the wire
/// order, that foreign objects are untouched, undo, debounce, abort handling and id verification.
/// No hardware: the simulator is the layer under test's only peer.
/// </summary>
public class GalleonSdOnboardTests
{
    private const ushort Registry = 0x000F;
    private const ushort Profile = 0x6D60;
    private const ushort FnLayer = 0x6D61;
    private const ushort ListRes = 0x6D62;

    private static readonly byte[] ProfileBytes = BuildProfile(0x6AF538B0);
    private static readonly ushort[] UserLayers = { 0x6D67, 0x6D66, 0x6D65, 0x6D64 };

    private static byte[] BuildProfile(uint ts)
    {
        var name = System.Text.Encoding.ASCII.GetBytes("GALLEON 100 SD Default Profile");
        var b = new List<byte> { 0x1E, 0xF1, (byte)ts, (byte)(ts >> 8), (byte)(ts >> 16), (byte)(ts >> 24) };
        ushort[] slots = { 0, FnLayer, 0, ListRes, 0x6D63, 0, 0, 0, 0, 0, 0, 0 };
        foreach (var s in slots)
        {
            b.Add((byte)s);
            b.Add((byte)(s >> 8));
        }
        b.Add((byte)name.Length);
        b.Add(0);
        b.AddRange(name);
        b.Add(0);
        return b.ToArray();
    }

    private sealed class Board : IHidDevice
    {
        public Dictionary<ushort, byte[]> Store { get; } = new();
        public List<byte[]> Writes { get; } = new();
        public List<ushort> Written { get; } = new();
        public int ActiveIndex { get; set; }
        public int StrayCloses { get; private set; }
        public List<string> Modes { get; } = new();
        public Func<ushort, int, bool>? RefuseOpen { get; set; }
        public Func<ushort, int, bool>? SilentOpen { get; set; }
        public Func<byte, bool>? ErrorStatusOn { get; set; }
        public bool CorruptWrites { get; set; }
        public Action? OnFirstWrite { get; set; }
        public HashSet<byte> BusyHandles { get; } = new();
        public bool GarbleCreateReply { get; set; }
        /// <summary>0B on an id that already exists; must never happen.</summary>
        public int CreatedExisting { get; private set; }
        /// <summary>0C on a resource some layer list still references; must never happen.</summary>
        public int DeletedWhileReferenced { get; private set; }

        public bool Referenced(ushort res)
        {
            foreach (var v in Store.Values)
            {
                if (GalleonSdOnboardCodec.TryParseLayerList(v, out var refs) && refs.Contains(res)) return true;
            }
            return false;
        }

        /// <summary>A second profile (6D80, list 6D82 holding layer 6D83) after the first in the registry.</summary>
        public void AddSecondProfile()
        {
            Store[Registry] = new byte[] { 0x13, 0x00, 0x02, 0x00, 0x60, 0x6D, 0x80, 0x6D };
            var p = BuildProfile(0x6AF538C0);
            p[6 + 2] = 0x81; p[6 + 3] = 0x6D; p[6 + 6] = 0x82; p[6 + 7] = 0x6D; p[6 + 8] = 0; p[6 + 9] = 0;
            Store[0x6D80] = p;
            Store[0x6D81] = new byte[] { 1, 1 };
            Store[0x6D82] = GalleonSdOnboardCodec.BuildLayerList(new ushort[] { 0x6D83 });
            Store[0x6D83] = new byte[] { 0x65, 0x00, 0x00, 0x09 };
        }

        private readonly Dictionary<ushort, int> _opens = new();
        private readonly Dictionary<byte, ushort> _open = new();
        private readonly Dictionary<byte, int> _readPos = new();
        private readonly Dictionary<byte, List<byte>> _pending = new();
        private readonly Dictionary<byte, int> _expect = new();
        private readonly Queue<byte[]> _replies = new();
        private bool _wrote;

        public int VendorId => GalleonSdProtocol.VendorId;
        public int ProductId => GalleonSdProtocol.ProductId;
        public string Path => "galleon";
        public string? Serial => "SN1";
        public int UsagePage => GalleonSdProtocol.VendorUsagePage;
        public int Usage => 1;

        public Board()
        {
            Store[Registry] = new byte[] { 0x13, 0x00, 0x01, 0x00, 0x60, 0x6D };
            Store[Profile] = ProfileBytes;
            Store[FnLayer] = new byte[] { 0x3D, 0xD4, 0x0D, 0x00, 0x49, 0x00, 0xFF, 0xFF, 0xFF, 0x00 };
            Store[ListRes] = GalleonSdOnboardCodec.BuildLayerList(UserLayers);
            Store[0x6D63] = new byte[] { 0x01, 0x02, 0x03 };
            Store[GalleonSdProtocol.DirectResource] = Array.Empty<byte>();
            foreach (var l in UserLayers) Store[l] = new byte[] { 0x65, 0x00, 0x00, 0x04, 0x04, 0x00, 0x00, 0x7D, (byte)l, 0x05, 0x06 };
        }

        private static byte[] Reply(byte cmd, byte status = 0, byte[]? data = null)
        {
            var r = new byte[GalleonSdProtocol.WireLength];
            r[3] = cmd;
            r[4] = status;
            data?.CopyTo(r, 5);
            return r;
        }

        public bool Write(ReadOnlySpan<byte> report)
        {
            var w = report[1..].ToArray();
            Writes.Add(w);
            var cmd = w[3];
            if (cmd == GalleonSdProtocol.CmdBlockFirst && !_wrote)
            {
                _wrote = true;
                OnFirstWrite?.Invoke();
            }
            var forced = ErrorStatusOn?.Invoke(cmd) == true;
            switch (cmd)
            {
                case GalleonSdProtocol.CmdSet:
                    if (w[4] == GalleonSdProtocol.PropMode) Modes.Add(w[6] == 2 ? "02" : "01");
                    _replies.Enqueue(Reply(cmd, (byte)(forced ? 1 : 0)));
                    break;
                case GalleonSdProtocol.CmdGet:
                    _replies.Enqueue(Reply(cmd, 0, new[] { (byte)(w[4] == GalleonSdProtocol.PropActiveProfile ? ActiveIndex : 0) }));
                    break;
                case GalleonSdProtocol.CmdStartTx:
                {
                    var res = (ushort)(w[5] | (w[6] << 8));
                    _opens[res] = _opens.GetValueOrDefault(res) + 1;
                    var n = _opens[res];
                    if (SilentOpen?.Invoke(res, n) == true) break;
                    if (forced || BusyHandles.Contains(w[4]) || RefuseOpen?.Invoke(res, n) == true || !Store.ContainsKey(res) || _open.ContainsKey(w[4]))
                    {
                        _replies.Enqueue(Reply(cmd, 1));
                        break;
                    }
                    _open[w[4]] = res;
                    _readPos[w[4]] = 0;
                    _replies.Enqueue(Reply(cmd));
                    break;
                }
                case GalleonSdProtocol.CmdSize:
                {
                    var len = Store[_open[w[4]]].Length;
                    _replies.Enqueue(Reply(cmd, 0, new[] { (byte)(len >> 16), (byte)(len >> 8), (byte)len }));
                    break;
                }
                case GalleonSdProtocol.CmdRead:
                {
                    var data = Store[_open[w[4]]];
                    var pos = _readPos[w[4]];
                    if (pos >= data.Length)
                    {
                        _replies.Enqueue(Reply(cmd, 2));
                        break;
                    }
                    var take = Math.Min(123, data.Length - pos);
                    _readPos[w[4]] = pos + take;
                    _replies.Enqueue(Reply(cmd, 0, data.AsSpan(pos, take).ToArray()));
                    break;
                }
                case GalleonSdProtocol.CmdBlockFirst:
                {
                    var len = w[5] | (w[6] << 8);
                    var first = Math.Min(len, 119);
                    _pending[w[4]] = new List<byte>(w.AsSpan(9, first).ToArray());
                    _expect[w[4]] = len;
                    _replies.Enqueue(Reply(cmd, (byte)(forced ? 1 : 0)));
                    break;
                }
                case GalleonSdProtocol.CmdBlockNext:
                {
                    var buf = _pending[w[4]];
                    var take = Math.Min(123, _expect[w[4]] - buf.Count);
                    buf.AddRange(w.AsSpan(5, take).ToArray());
                    _replies.Enqueue(Reply(cmd));
                    break;
                }
                case GalleonSdProtocol.CmdStopTx:
                {
                    var h = w[5];
                    if (!_open.TryGetValue(h, out var res))
                    {
                        StrayCloses++;
                        _replies.Enqueue(Reply(cmd, 1));
                        break;
                    }
                    if (_pending.TryGetValue(h, out var buf))
                    {
                        var data = buf.ToArray();
                        if (CorruptWrites && data.Length > 0) data[^1] ^= 0xFF;
                        Store[res] = data;
                        Written.Add(res);
                        _pending.Remove(h);
                    }
                    _open.Remove(h);
                    _replies.Enqueue(Reply(cmd));
                    break;
                }
                case GalleonSdProtocol.CmdCreate:
                {
                    var res = (ushort)(w[4] | (w[5] << 8));
                    if (Store.ContainsKey(res)) CreatedExisting++;
                    if (!forced) Store[res] = Array.Empty<byte>();
                    _replies.Enqueue(GarbleCreateReply ? Reply(0x7F) : Reply(cmd, (byte)(forced ? 1 : 0)));
                    break;
                }
                case GalleonSdProtocol.CmdDelete:
                {
                    var res = (ushort)(w[4] | (w[5] << 8));
                    if (Referenced(res)) DeletedWhileReferenced++;
                    if (!forced) Store.Remove(res);
                    _replies.Enqueue(Reply(cmd, (byte)(forced ? 1 : 0)));
                    break;
                }
                default:
                    _replies.Enqueue(Reply(cmd));
                    break;
            }
            return true;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (!_replies.TryDequeue(out var r)) return 0;
            r.CopyTo(buffer);
            return r.Length;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
        public void Dispose() { }

        public string[] Tokens()
        {
            var t = new List<string>();
            foreach (var w in Writes)
            {
                switch (w[3])
                {
                    case GalleonSdProtocol.CmdSet: t.Add(w[4] == GalleonSdProtocol.PropMode ? $"SET:{w[6]:X2}" : $"SETP:{w[4]:X2}"); break;
                    case GalleonSdProtocol.CmdGet: t.Add($"GET:{w[4]:X2}"); break;
                    case GalleonSdProtocol.CmdStartTx: t.Add($"OPEN:h{w[4]}:{(w[5] | (w[6] << 8)):X4}"); break;
                    case GalleonSdProtocol.CmdSize: t.Add("SIZE"); break;
                    case GalleonSdProtocol.CmdRead: t.Add("READ"); break;
                    case GalleonSdProtocol.CmdStopTx: t.Add($"CLOSE:h{w[5]}"); break;
                    case GalleonSdProtocol.CmdBlockFirst: t.Add($"WRITE:h{w[4]}"); break;
                    case GalleonSdProtocol.CmdBlockNext: t.Add("CONT"); break;
                    case GalleonSdProtocol.CmdCreate: t.Add($"CREATE:{(w[4] | (w[5] << 8)):X4}"); break;
                    case GalleonSdProtocol.CmdDelete: t.Add($"DELETE:{(w[4] | (w[5] << 8)):X4}"); break;
                    default: t.Add($"?{w[3]:X2}"); break;
                }
            }
            return t.ToArray();
        }
    }

    private sealed class OneHid : IHidEnumerator
    {
        private readonly Board _board;
        public OneHid(Board board) => _board = board;
        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => FindAll();
        public IReadOnlyList<HidDeviceInfo> FindAll() => new[]
        {
            new HidDeviceInfo { VendorId = 0x1B1C, ProductId = 0x2B0C, Path = "galleon", Serial = "SN1", UsagePage = 0xFF42, OutputReportByteLength = 129, InputReportByteLength = 129 },
        };
        public IHidDevice? Open(string path, bool forInput = false) => _board;
    }

    private sealed class Rig
    {
        public Board Board { get; } = new();
        public InMemoryConfigStore Store { get; } = new();
        public GalleonSdHub Hub { get; }
        public long Clock { get; set; } = 1_000_000_000;
        private int _seconds = 1_900_000_000;

        public Rig(bool verify = false)
        {
            Hub = new GalleonSdHub(new OneHid(Board), () => Clock, matchOnLinux: true, store: Store)
            {
                VerifyOnboardWrites = verify,
                PostTxProbe = false,
                UtcNow = () => DateTimeOffset.FromUnixTimeSeconds(_seconds++),
            };
            Hub.Reconcile(true);
            Board.Writes.Clear();
        }

        public void Advance(double seconds) => Clock += (long)(seconds * Stopwatch.Frequency);

        /// <summary>Presents the frame, lets it settle past the debounce, and presents it again so the transaction runs.</summary>
        public void Settle(RgbColor[] frame)
        {
            Hub.SetSavedToDevice(true, frame);
            Advance(5.1);
            Hub.SetSavedToDevice(true, frame);
        }

        public GalleonOnboardRecord? Record => Store.Load().Devices.GalleonOnboard.GetValueOrDefault("SN1");
    }

    private static RgbColor[] Solid(RgbColor c) => Enumerable.Repeat(c, GalleonSdProtocol.LedCount).ToArray();

    private static RgbColor[] Split(RgbColor a, RgbColor b)
    {
        var f = Solid(a);
        for (var i = f.Length / 2; i < f.Length; i++) f[i] = b;
        return f;
    }

    private static readonly RgbColor Red = new(255, 0, 0);
    private static readonly RgbColor Blue = new(0, 0, 255);

    // ---- quantizer ----

    [Fact]
    public void Quantizer_caps_a_gradient_at_eight_colors_is_deterministic_and_skips_black()
    {
        var frame = new RgbColor[GalleonSdProtocol.LedCount];
        for (var i = 0; i < frame.Length; i++) frame[i] = i < 6 ? new RgbColor(0, 0, 0) : new RgbColor((byte)(i * 2), (byte)(255 - i), (byte)((i * 7) % 256));

        var a = GalleonSdOnboardCodec.Quantize(frame);
        var b = GalleonSdOnboardCodec.Quantize(frame);

        Assert.InRange(a.Count, 1, GalleonSdOnboardCodec.MaxLayers);
        Assert.Equal(a.Select(l => (l.Color, l.Keys.ToArray())), b.Select(l => (l.Color, l.Keys.ToArray())), new LayerComparer());
        var keys = a.SelectMany(l => l.Keys).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.Equal(GalleonSdProtocol.LedCount - 6, keys.Length);
        Assert.DoesNotContain(a, l => GalleonSdOnboardCodec.IsOff(l.Color));
        Assert.DoesNotContain(GalleonSdProtocol.KeyIds.Take(6), id => keys.Contains((byte)id));
    }

    private sealed class LayerComparer : IEqualityComparer<(RgbColor Color, byte[] Keys)>
    {
        public bool Equals((RgbColor Color, byte[] Keys) x, (RgbColor Color, byte[] Keys) y) => x.Color == y.Color && x.Keys.SequenceEqual(y.Keys);
        public int GetHashCode((RgbColor Color, byte[] Keys) o) => o.Color.GetHashCode();
    }

    [Fact]
    public void Quantizer_keeps_up_to_eight_exact_colors_and_all_black_makes_no_layer()
    {
        var frame = Solid(new RgbColor(0, 0, 0));
        Assert.Empty(GalleonSdOnboardCodec.Quantize(frame));

        for (var i = 0; i < frame.Length; i++) frame[i] = new RgbColor((byte)(10 + (i % 8)), 20, 30);
        var layers = GalleonSdOnboardCodec.Quantize(frame);

        Assert.Equal(8, layers.Count);
        Assert.All(layers, l => Assert.Equal(20, l.Color.G));
        Assert.Equal(GalleonSdProtocol.LedCount, layers.Sum(l => l.Keys.Length));
    }

    [Fact]
    public void Layer_body_for_a_full_frame_lists_all_126_key_ids_including_the_corsair_extras()
    {
        Assert.Equal(126, GalleonSdProtocol.LedCount);
        var layer = Assert.Single(GalleonSdOnboardCodec.Quantize(Solid(new RgbColor(0xFC, 0x98, 0x02))));

        var body = GalleonSdOnboardCodec.BuildLayerBody(layer);

        Assert.Equal(new byte[] { 0x7E, 0x20, 0x01, 0x00, 0x00, 0x00, 0x01, 0xFF, 0x02, 0x98, 0xFC, 0x7E }, body[..12]);
        Assert.Equal(GalleonSdProtocol.KeyIds.Select(i => (byte)i), body[12..]);
        Assert.Equal(138, body.Length);
        Assert.All(Enumerable.Range(0x81, 0x91 - 0x81 + 1).Where(id => GalleonSdProtocol.KeyIds.Contains(id)), id => Assert.Contains((byte)id, body[12..]));
    }

    // ---- wire order, ownership, undo ----

    [Fact]
    public void Turning_on_follows_the_web_hub_sequence_and_a_refused_probe_open_gets_no_close()
    {
        var rig = new Rig();

        rig.Settle(Solid(Red));

        var expected = new List<string> { "SET:02", "GET:6E" };
        void Read(string h, string res, int reads = 1) { expected.Add($"OPEN:h0:{res}"); expected.Add("SIZE"); for (var i = 0; i < reads; i++) expected.Add("READ"); expected.Add("CLOSE:h0"); }
        Read("0", "000F");
        Read("0", "6D60");
        Read("0", "6D62");
        foreach (var l in UserLayers) Read("0", l.ToString("X4"));
        expected.AddRange(new[] { "OPEN:h1:6D62", "CLOSE:h1", "OPEN:h1:6D68", "CREATE:6D68", "OPEN:h1:6D68", "WRITE:h1", "CONT", "CLOSE:h1" });
        expected.AddRange(new[] { "OPEN:h2:6D62", "WRITE:h2", "CLOSE:h2" });
        expected.Add("SET:02");
        Read("0", "6D60");
        expected.AddRange(new[] { "OPEN:h3:6D60", "WRITE:h3", "CLOSE:h3" });
        expected.Add("SET:01");
        expected.AddRange(new[] { "OPEN:h1:000F", "WRITE:h1", "CLOSE:h1" });
        expected.Add("SET:01");

        var tokens = rig.Board.Tokens().SkipWhile(t => t != "SET:02").ToArray();
        Assert.Equal(expected, tokens);
        Assert.Equal(0, rig.Board.StrayCloses);
    }

    [Fact]
    public void Foreign_layers_the_fn_slot_and_the_registry_stay_byte_identical_and_ours_go_first()
    {
        var rig = new Rig();
        var before = rig.Board.Store.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());

        rig.Settle(Solid(Red));

        foreach (var l in UserLayers) Assert.Equal(before[l], rig.Board.Store[l]);
        Assert.Equal(before[FnLayer], rig.Board.Store[FnLayer]);
        Assert.Equal(before[0x6D63], rig.Board.Store[0x6D63]);
        Assert.Equal(before[Registry], rig.Board.Store[Registry]);
        Assert.Equal(new ushort[] { 0x6D68, Registry, ListRes, Profile }.OrderBy(x => x), rig.Board.Written.Distinct().OrderBy(x => x));
        Assert.DoesNotContain(rig.Board.Written, w => UserLayers.Contains(w) || w == FnLayer);

        Assert.True(GalleonSdOnboardCodec.TryParseLayerList(rig.Board.Store[ListRes], out var list));
        Assert.Equal(new ushort[] { 0x6D68 }.Concat(UserLayers), list);

        var profileAfter = rig.Board.Store[Profile];
        Assert.Equal(before[Profile][..2], profileAfter[..2]);
        Assert.NotEqual(before[Profile][2..6], profileAfter[2..6]);
        Assert.Equal(before[Profile][6..], profileAfter[6..]);
        GalleonSdOnboardCodec.TryParseProfile(profileAfter, out var stamp, out _);
        GalleonSdOnboardCodec.TryParseProfile(before[Profile], out var oldStamp, out _);
        Assert.True(stamp > oldStamp);
        Assert.Equal(GalleonSdOnboardCodec.BuildLayerBody(GalleonSdOnboardCodec.Quantize(Solid(Red))[0]), rig.Board.Store[0x6D68]);
    }

    [Fact]
    public void The_backup_is_persisted_before_the_first_write_and_holds_the_raw_original_bytes()
    {
        var rig = new Rig();
        GalleonOnboardRecord? atFirstWrite = null;
        rig.Board.OnFirstWrite = () => atFirstWrite = rig.Record;
        var original = rig.Board.Store.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());

        rig.Settle(Solid(Red));

        Assert.NotNull(atFirstWrite);
        Assert.True(atFirstWrite!.HasBackup());
        Assert.Equal(original[Profile], Convert.FromBase64String(atFirstWrite.ProfileB64));
        Assert.Equal(original[ListRes], Convert.FromBase64String(atFirstWrite.ListB64));
        Assert.Equal(original[Registry], Convert.FromBase64String(atFirstWrite.RegistryB64));
        Assert.Equal(Profile, atFirstWrite.ProfileRes);
        Assert.Equal(ListRes, atFirstWrite.ListRes);
        Assert.Equal(new[] { 0x6D68 }, rig.Record!.Owned.Select(o => o.Id));
    }

    [Fact]
    public void Turning_off_deletes_our_layers_restores_the_original_list_clears_ids_and_resumes_streaming()
    {
        var rig = new Rig();
        var originalList = rig.Board.Store[ListRes].ToArray();
        rig.Settle(Split(Red, Blue));
        Assert.Equal(2, rig.Record!.Owned.Count);
        rig.Board.Writes.Clear();

        rig.Hub.SetSavedToDevice(false);

        var tokens = rig.Board.Tokens();
        Assert.Equal(2, tokens.Count(t => t.StartsWith("DELETE:", StringComparison.Ordinal)));
        Assert.Equal(originalList, rig.Board.Store[ListRes]);
        Assert.All(rig.Record!.Owned.Select(o => (ushort)o.Id), id => Assert.False(rig.Board.Store.ContainsKey(id)));
        Assert.Empty(rig.Record!.Owned);
        Assert.True(rig.Record!.HasBackup());
        Assert.Equal("SET:01", tokens[^1]);
        foreach (var l in UserLayers) Assert.True(rig.Board.Store.ContainsKey(l));
        Assert.Equal(0, rig.Board.DeletedWhileReferenced);
        Assert.True(Array.IndexOf(tokens, "WRITE:h2") < tokens.ToList().FindIndex(t => t.StartsWith("DELETE:", StringComparison.Ordinal)));

        var before = rig.Board.Writes.Count;
        rig.Hub.SetSavedToDevice(false);
        Assert.Equal(before, rig.Board.Writes.Count);

        rig.Advance(1);
        Assert.True(rig.Hub.WriteFrame(Solid(Red)));
        Assert.Contains(rig.Board.Tokens().Skip(tokens.Length), t => t == "SET:02");
        Assert.Contains(rig.Board.Writes, w => w[3] == GalleonSdProtocol.CmdBlockFirst && w[5] == (byte)GalleonSdProtocol.DirectPayloadLength);
    }

    [Fact]
    public void Undo_keeps_the_users_current_layers_when_the_list_changed_meanwhile()
    {
        var rig = new Rig();
        rig.Settle(Solid(Red));
        var userAdded = new ushort[] { 0x6D68, 0x6D70, 0x6D67, 0x6D66, 0x6D65, 0x6D64 };
        rig.Board.Store[0x6D70] = new byte[] { 9, 9, 9 };
        rig.Board.Store[ListRes] = GalleonSdOnboardCodec.BuildLayerList(userAdded);

        rig.Hub.SetSavedToDevice(false);

        Assert.True(GalleonSdOnboardCodec.TryParseLayerList(rig.Board.Store[ListRes], out var list));
        Assert.Equal(new ushort[] { 0x6D70, 0x6D67, 0x6D66, 0x6D65, 0x6D64 }, list);
        Assert.Equal(new byte[] { 9, 9, 9 }, rig.Board.Store[0x6D70]);
    }

    // ---- update, debounce, skip ----

    [Fact]
    public void A_color_change_rewrites_only_the_changed_layers_and_creates_or_deletes_owned_ones()
    {
        var rig = new Rig();
        rig.Settle(Solid(Red));
        rig.Board.Written.Clear();
        rig.Board.Writes.Clear();

        rig.Settle(Split(Red, Blue));

        var created = rig.Board.Tokens().Where(t => t.StartsWith("CREATE:", StringComparison.Ordinal)).ToArray();
        Assert.Single(created);
        Assert.Equal(2, rig.Record!.Owned.Count);
        Assert.Contains(0x6D68, rig.Record.Owned.Select(o => o.Id));
        var red = rig.Board.Store[0x6D68];
        Assert.True(GalleonSdOnboardCodec.TryReadLayerColor(red, out var c));
        Assert.Equal(Red, c);
        Assert.Equal(GalleonSdProtocol.LedCount / 2, red[11]);

        rig.Board.Writes.Clear();
        rig.Settle(Solid(Red));

        Assert.Single(rig.Board.Tokens(), t => t.StartsWith("DELETE:", StringComparison.Ordinal));
        Assert.Equal(0, rig.Board.DeletedWhileReferenced);
        Assert.Equal(0, rig.Board.CreatedExisting);
        Assert.Single(rig.Record!.Owned);
        Assert.Equal(126, rig.Board.Store[0x6D68][11]);
        Assert.True(GalleonSdOnboardCodec.TryParseLayerList(rig.Board.Store[ListRes], out var list));
        Assert.Equal(new ushort[] { 0x6D68 }.Concat(UserLayers), list);
    }

    [Fact]
    public void Nothing_is_written_before_the_frame_has_held_still_for_the_debounce_and_a_late_change_restarts_it()
    {
        var rig = new Rig();
        var frame = Solid(Red);

        rig.Hub.SetSavedToDevice(true, frame);
        rig.Advance(4.9);
        rig.Hub.SetSavedToDevice(true, frame);
        Assert.DoesNotContain(rig.Board.Tokens(), t => t.StartsWith("OPEN:", StringComparison.Ordinal));

        var other = Solid(Blue);
        rig.Hub.SetSavedToDevice(true, other);
        rig.Advance(4.9);
        rig.Hub.SetSavedToDevice(true, other);
        Assert.DoesNotContain(rig.Board.Tokens(), t => t.StartsWith("OPEN:", StringComparison.Ordinal));

        rig.Advance(0.2);
        rig.Hub.SetSavedToDevice(true, other);
        Assert.Contains("OPEN:h1:6D68", rig.Board.Tokens());
    }

    [Fact]
    public void An_animated_frame_never_settles_so_nothing_is_written()
    {
        var rig = new Rig();
        for (var i = 0; i < 40; i++)
        {
            rig.Hub.SetSavedToDevice(true, Solid(new RgbColor((byte)(10 + i), 0, 0)));
            rig.Advance(1);
        }

        Assert.DoesNotContain(rig.Board.Writes, w => w[3] is GalleonSdProtocol.CmdBlockFirst or GalleonSdProtocol.CmdCreate or GalleonSdProtocol.CmdStartTx);
    }

    [Fact]
    public void An_identical_or_equivalent_frame_is_skipped_with_no_traffic()
    {
        var rig = new Rig();
        var frame = Solid(Red);
        rig.Settle(frame);
        rig.Board.Writes.Clear();

        rig.Settle(frame);
        var nearBlack = Solid(Red);
        nearBlack[0] = new RgbColor(1, 0, 0);
        var withOff = Solid(Red);
        withOff[0] = new RgbColor(0, 0, 0);
        rig.Settle(withOff);
        rig.Settle(nearBlack);

        Assert.Equal(2, rig.Board.Tokens().Count(t => t == "SET:02"));
    }

    // ---- errors, backoff, ids ----

    [Fact]
    public void A_refused_write_open_aborts_restores_hardware_mode_sends_no_stray_close_and_backs_off()
    {
        var rig = new Rig();
        rig.Board.RefuseOpen = (res, n) => res == ListRes && n >= 2;
        var frame = Solid(Red);

        rig.Settle(frame);

        var tokens = rig.Board.Tokens();
        Assert.DoesNotContain("OPEN:h2:6D62", tokens.Skip(1).Where(t => t == "CLOSE:h2"));
        Assert.DoesNotContain("CLOSE:h2", tokens);
        Assert.Equal(0, rig.Board.StrayCloses);
        Assert.Equal("SET:01", tokens[^1]);
        Assert.Equal("01", rig.Board.Modes[^1]);
        Assert.DoesNotContain(tokens, t => t == "WRITE:h2");

        var seen = rig.Board.Writes.Count;
        rig.Advance(1);
        rig.Hub.SetSavedToDevice(true, frame);
        Assert.Equal(seen, rig.Board.Writes.Count);

        rig.Board.RefuseOpen = null;
        rig.Advance(5);
        rig.Hub.SetSavedToDevice(true, frame);
        Assert.True(rig.Board.Writes.Count > seen);
        Assert.Equal(0, rig.Board.StrayCloses);
    }

    [Fact]
    public void A_silent_open_aborts_without_a_close()
    {
        var rig = new Rig();
        rig.Board.SilentOpen = (res, n) => res == Profile && n >= 2;

        rig.Settle(Solid(Red));

        var tokens = rig.Board.Tokens();
        Assert.Equal("SET:01", tokens[^1]);
        Assert.DoesNotContain("WRITE:h3", tokens);
        Assert.Equal(0, rig.Board.StrayCloses);
        Assert.Equal(tokens.Count(t => t.StartsWith("OPEN:", StringComparison.Ordinal)), tokens.Count(t => t.StartsWith("CLOSE:", StringComparison.Ordinal)) + 2);
    }

    [Fact]
    public void An_error_status_on_the_layer_write_closes_only_the_handle_we_opened_and_restores_hardware()
    {
        var rig = new Rig();
        rig.Board.ErrorStatusOn = cmd => cmd == GalleonSdProtocol.CmdBlockFirst;

        rig.Settle(Solid(Red));

        var tokens = rig.Board.Tokens();
        var write = Array.IndexOf(tokens, "WRITE:h1");
        Assert.True(write > 0);
        Assert.Equal("CLOSE:h1", tokens[write + 1 + tokens.Skip(write + 1).TakeWhile(t => t == "CONT").Count()]);
        Assert.Equal("SET:01", tokens[^1]);
        Assert.DoesNotContain("WRITE:h2", tokens);
        Assert.Equal(0, rig.Board.StrayCloses);
    }

    [Fact]
    public void A_refused_create_aborts_before_any_list_write()
    {
        var rig = new Rig();
        rig.Board.ErrorStatusOn = cmd => cmd == GalleonSdProtocol.CmdCreate;
        var listBefore = rig.Board.Store[ListRes].ToArray();

        rig.Settle(Solid(Red));

        Assert.Equal(listBefore, rig.Board.Store[ListRes]);
        Assert.DoesNotContain("WRITE:h2", rig.Board.Tokens());
        Assert.Equal("SET:01", rig.Board.Tokens()[^1]);
    }

    [Fact]
    public void A_persisted_id_whose_body_changed_is_foreign_and_is_never_touched_or_reused()
    {
        var rig = new Rig();
        var stolen = new byte[] { 0x7E, 0x20, 0x01, 0x00, 0x00, 0x00, 0x01, 0xFF, 1, 2, 3, 1, 4 };
        rig.Board.Store[0x6D68] = stolen;
        rig.Store.Update(s => s.Devices.GalleonOnboard = new() { ["SN1"] = new GalleonOnboardRecord
        {
            Owned = new() { new GalleonOwnedLayer { Id = 0x6D68, BodyB64 = Convert.ToBase64String(new byte[] { 0x7E, 0x20, 0x01, 0x00, 0x00, 0x00, 0x01, 0xFF, 9, 9, 9, 1, 4 }) } },
        } });

        rig.Settle(Solid(Red));

        Assert.Equal(stolen, rig.Board.Store[0x6D68]);
        Assert.DoesNotContain(0x6D68, rig.Board.Tokens().Where(t => t.StartsWith("CREATE:", StringComparison.Ordinal)).Select(t => Convert.ToInt32(t[7..], 16)));
        Assert.DoesNotContain(rig.Board.Written, w => w == 0x6D68);
        Assert.Contains("CREATE:6D69", rig.Board.Tokens());
        Assert.Equal(new[] { 0x6D69 }, rig.Record!.Owned.Select(o => o.Id));
    }

    [Fact]
    public void A_persisted_id_that_still_matches_is_reused_in_place_with_no_create()
    {
        var rig = new Rig();
        rig.Settle(Solid(Red));
        var hub2 = new GalleonSdHub(new OneHid(rig.Board), () => rig.Clock, matchOnLinux: true, store: rig.Store) { VerifyOnboardWrites = false };
        hub2.Reconcile(true);
        rig.Board.Writes.Clear();

        hub2.SetSavedToDevice(true, Solid(Blue));
        rig.Advance(5.1);
        hub2.SetSavedToDevice(true, Solid(Blue));

        Assert.DoesNotContain(rig.Board.Tokens(), t => t.StartsWith("CREATE:", StringComparison.Ordinal));
        Assert.True(GalleonSdOnboardCodec.TryReadLayerColor(rig.Board.Store[0x6D68], out var c));
        Assert.Equal(Blue, c);
        Assert.Equal(new[] { 0x6D68 }, rig.Record!.Owned.Select(o => o.Id));
        hub2.Dispose();
    }

    [Fact]
    public void A_persisted_id_that_still_matches_is_undone_after_a_restart()
    {
        var rig = new Rig();
        var originalList = rig.Board.Store[ListRes].ToArray();
        rig.Settle(Solid(Red));
        var hub2 = new GalleonSdHub(new OneHid(rig.Board), () => rig.Clock, matchOnLinux: true, store: rig.Store) { VerifyOnboardWrites = false };
        hub2.Reconcile(true);

        hub2.SetSavedToDevice(false);

        Assert.Equal(originalList, rig.Board.Store[ListRes]);
        Assert.False(rig.Board.Store.ContainsKey(0x6D68));
        Assert.Empty(rig.Record!.Owned);
        hub2.Dispose();
    }

    [Fact]
    public void The_active_profile_index_picks_the_profile_to_edit_and_an_index_outside_the_registry_aborts()
    {
        var rig = new Rig();
        rig.Board.ActiveIndex = 3;

        rig.Settle(Solid(Red));

        Assert.DoesNotContain(rig.Board.Writes, w => w[3] is GalleonSdProtocol.CmdBlockFirst or GalleonSdProtocol.CmdCreate);
        Assert.Equal("SET:01", rig.Board.Tokens()[^1]);
    }

    // ---- review fixes ----

    [Fact]
    public void A_profile_switch_between_write_and_undo_undoes_on_the_recorded_profile_only()
    {
        var rig = new Rig();
        rig.Board.AddSecondProfile();
        var original = rig.Board.Store[ListRes].ToArray();
        var other = rig.Board.Store[0x6D82].ToArray();
        rig.Settle(Solid(Red));
        Assert.Equal(Profile, rig.Record!.ProfileRes);
        rig.Board.ActiveIndex = 1;
        rig.Board.Written.Clear();

        rig.Hub.SetSavedToDevice(false);

        Assert.Equal(original, rig.Board.Store[ListRes]);
        Assert.Equal(other, rig.Board.Store[0x6D82]);
        Assert.DoesNotContain(rig.Board.Written, w => w is 0x6D80 or 0x6D82 or 0x6D81);
        Assert.Empty(rig.Record!.Owned);
        Assert.Equal(0, rig.Board.DeletedWhileReferenced);
    }

    [Fact]
    public void An_update_after_a_profile_switch_undoes_the_old_profile_first_then_starts_fresh_on_the_active_one()
    {
        var rig = new Rig();
        rig.Board.AddSecondProfile();
        var original = rig.Board.Store[ListRes].ToArray();
        rig.Settle(Solid(Red));
        rig.Board.ActiveIndex = 1;

        rig.Settle(Solid(Blue));
        Assert.Equal(original, rig.Board.Store[ListRes]);
        Assert.Empty(rig.Record!.Owned);

        rig.Settle(Solid(Blue));

        Assert.Equal(original, rig.Board.Store[ListRes]);
        Assert.True(GalleonSdOnboardCodec.TryParseLayerList(rig.Board.Store[0x6D82], out var list));
        Assert.Equal(2, list.Length);
        Assert.Equal(0x6D83, list[1]);
        Assert.Equal(0x6D80, rig.Record.ProfileRes);
        Assert.Equal(0, rig.Board.DeletedWhileReferenced);
        Assert.Equal(0, rig.Board.CreatedExisting);
    }

    [Fact]
    public void An_owned_empty_layer_from_a_create_without_its_write_is_reused_not_fatal()
    {
        var rig = new Rig();
        rig.Board.Store[0x6D68] = Array.Empty<byte>();
        var red = GalleonSdOnboardCodec.BuildLayerBody(GalleonSdOnboardCodec.Quantize(Solid(Red))[0]);
        rig.Store.Update(s => s.Devices.GalleonOnboard = new() { ["SN1"] = new GalleonOnboardRecord
        {
            ProfileRes = Profile, ListRes = ListRes,
            Owned = new() { new GalleonOwnedLayer { Id = 0x6D68, PendingB64 = Convert.ToBase64String(red) } },
        } });

        rig.Settle(Solid(Red));

        Assert.DoesNotContain(rig.Board.Tokens(), t => t.StartsWith("CREATE:", StringComparison.Ordinal));
        Assert.Equal(red, rig.Board.Store[0x6D68]);
        Assert.Equal(0, rig.Board.CreatedExisting);
        Assert.Equal(new[] { 0x6D68 }, rig.Record!.Owned.Select(o => o.Id));
    }

    [Fact]
    public void An_empty_owned_layer_in_the_list_is_removed_from_the_list_then_deleted_on_undo()
    {
        var rig = new Rig();
        rig.Board.Store[0x6D68] = Array.Empty<byte>();
        rig.Board.Store[ListRes] = GalleonSdOnboardCodec.BuildLayerList(new ushort[] { 0x6D68 }.Concat(UserLayers).ToArray());
        rig.Store.Update(s => s.Devices.GalleonOnboard = new() { ["SN1"] = new GalleonOnboardRecord
        {
            ProfileRes = Profile, ListRes = ListRes,
            Owned = new() { new GalleonOwnedLayer { Id = 0x6D68, PendingB64 = "AQ==" } },
        } });

        rig.Hub.SetSavedToDevice(false);

        Assert.True(GalleonSdOnboardCodec.TryParseLayerList(rig.Board.Store[ListRes], out var list));
        Assert.Equal(UserLayers, list);
        Assert.False(rig.Board.Store.ContainsKey(0x6D68));
        Assert.Equal(0, rig.Board.DeletedWhileReferenced);
    }

    [Fact]
    public void An_owned_id_that_vanished_is_dropped_from_the_list_and_never_written_back()
    {
        var rig = new Rig();
        rig.Settle(Solid(Red));
        rig.Board.Store.Remove(0x6D68);

        rig.Hub.SetSavedToDevice(false);

        Assert.True(GalleonSdOnboardCodec.TryParseLayerList(rig.Board.Store[ListRes], out var list));
        Assert.Equal(UserLayers, list);
    }

    [Fact]
    public void An_unparsed_create_reply_aborts_cleanly_and_the_layer_stays_recorded_then_is_reused()
    {
        var rig = new Rig();
        rig.Board.GarbleCreateReply = true;

        rig.Settle(Solid(Red));

        var tokens = rig.Board.Tokens();
        Assert.Equal("SET:01", tokens[^1]);
        Assert.DoesNotContain("WRITE:h1", tokens);
        Assert.Equal(0x6D68, Assert.Single(rig.Record!.Owned).Id);
        Assert.Equal(0, rig.Board.StrayCloses);

        rig.Board.GarbleCreateReply = false;
        rig.Advance(5.1);
        rig.Hub.SetSavedToDevice(true, Solid(Red));

        Assert.Equal(1, rig.Board.Tokens().Count(t => t.StartsWith("CREATE:", StringComparison.Ordinal)));
        Assert.Equal(0, rig.Board.CreatedExisting);
        Assert.True(GalleonSdOnboardCodec.TryReadLayerColor(rig.Board.Store[0x6D68], out var c));
        Assert.Equal(Red, c);
    }

    [Fact]
    public void Allocation_reserves_ids_from_every_profile()
    {
        var rig = new Rig();
        rig.Board.AddSecondProfile();

        rig.Settle(Solid(Red));

        Assert.Contains("CREATE:6D84", rig.Board.Tokens());
        Assert.Equal(0, rig.Board.CreatedExisting);
    }

    [Fact]
    public void A_busy_handle_refusal_is_not_taken_as_a_free_id()
    {
        var rig = new Rig();
        rig.Board.BusyHandles.Add(1);

        rig.Settle(Solid(Red));

        Assert.DoesNotContain(rig.Board.Tokens(), t => t.StartsWith("CREATE:", StringComparison.Ordinal));
        Assert.Equal("SET:01", rig.Board.Tokens()[^1]);
        Assert.Equal(0, rig.Board.StrayCloses);
    }

    [Fact]
    public void A_silent_probe_open_of_a_candidate_id_is_ambiguous_and_aborts_before_create()
    {
        var rig = new Rig();
        rig.Board.SilentOpen = (res, _) => res == 0x6D68;

        rig.Settle(Solid(Red));

        Assert.DoesNotContain(rig.Board.Tokens(), t => t.StartsWith("CREATE:", StringComparison.Ordinal));
        Assert.Equal("SET:01", rig.Board.Tokens()[^1]);
    }

    [Fact]
    public void A_pending_body_survives_a_crash_between_the_write_and_its_confirmation()
    {
        var rig = new Rig();
        rig.Settle(Solid(Red));
        var blue = GalleonSdOnboardCodec.BuildLayerBody(GalleonSdOnboardCodec.Quantize(Solid(Blue))[0]);
        var rec = rig.Record!.Clone();
        rec.Owned[0].PendingB64 = Convert.ToBase64String(blue);
        rig.Store.Update(s => s.Devices.GalleonOnboard = new() { ["SN1"] = rec });
        rig.Board.Store[0x6D68] = blue;
        var backupBefore = rec.ListB64;
        var hub2 = new GalleonSdHub(new OneHid(rig.Board), () => rig.Clock, matchOnLinux: true, store: rig.Store) { PostTxProbe = false };
        hub2.Reconcile(true);
        rig.Board.Written.Clear();
        rig.Board.Writes.Clear();

        hub2.SetSavedToDevice(true, Solid(Blue));
        rig.Advance(5.1);
        hub2.SetSavedToDevice(true, Solid(Blue));

        Assert.DoesNotContain(rig.Board.Written, w => w == 0x6D68);
        Assert.DoesNotContain(rig.Board.Tokens(), t => t.StartsWith("CREATE:", StringComparison.Ordinal));
        Assert.Equal(blue, rig.Board.Store[0x6D68]);
        Assert.Equal(backupBefore, rig.Record!.ListB64);
        hub2.Dispose();
    }

    [Fact]
    public void Transactions_are_capped_per_hour_and_resume_when_the_window_frees()
    {
        var rig = new Rig();
        for (var i = 0; i < 36; i++) rig.Settle(Solid(new RgbColor((byte)(20 + (i * 6)), 0, 0)));

        Assert.Equal(30, rig.Board.Tokens().Count(t => t == "GET:6E"));

        rig.Advance(3700);
        rig.Settle(Solid(new RgbColor(5, 200, 0)));
        Assert.Equal(31, rig.Board.Tokens().Count(t => t == "GET:6E"));
    }

    [Fact]
    public void Small_color_drift_does_not_commit_a_write()
    {
        var rig = new Rig();
        rig.Settle(Solid(new RgbColor(200, 100, 50)));
        var tx = rig.Board.Tokens().Count(t => t == "GET:6E");

        rig.Settle(Solid(new RgbColor(202, 99, 52)));
        Assert.Equal(tx, rig.Board.Tokens().Count(t => t == "GET:6E"));

        rig.Settle(Solid(new RgbColor(215, 99, 52)));
        Assert.Equal(tx + 1, rig.Board.Tokens().Count(t => t == "GET:6E"));
    }

    [Fact]
    public void Taking_the_device_out_of_control_does_not_undo_but_turning_the_switch_off_does()
    {
        var rig = new Rig();
        rig.Settle(Solid(Red));
        rig.Board.Writes.Clear();

        rig.Hub.SetSavedToDevice(false, undo: false);
        Assert.Empty(rig.Board.Tokens());
        Assert.Single(rig.Record!.Owned);

        rig.Hub.SetSavedToDevice(false);
        Assert.Contains(rig.Board.Tokens(), t => t.StartsWith("DELETE:", StringComparison.Ordinal));
        Assert.Empty(rig.Record!.Owned);
    }

    [Fact]
    public void A_post_transaction_probe_walk_runs_outside_the_write_bracket_by_default()
    {
        var rig = new Rig();
        rig.Hub.PostTxProbe = true;

        rig.Settle(Solid(Red));

        var tokens = rig.Board.Tokens();
        var lastWrite = Array.FindLastIndex(tokens, t => t.StartsWith("WRITE:", StringComparison.Ordinal));
        var after = tokens.Skip(lastWrite).ToArray();
        Assert.Contains("SET:01", after);
        Assert.Contains("GET:6E", after);
        Assert.DoesNotContain(after.SkipWhile(t => t != "GET:6E"), t => t.StartsWith("WRITE:", StringComparison.Ordinal) || t.StartsWith("CREATE:", StringComparison.Ordinal) || t.StartsWith("DELETE:", StringComparison.Ordinal));
        Assert.Equal("SET:01", tokens[^1]);
    }

    [Fact]
    public void Foreign_duplicate_list_entries_are_preserved_exactly()
    {
        var rig = new Rig();
        var dup = new ushort[] { 0x6D67, 0x6D66, 0x6D67, 0x6D64 };
        rig.Board.Store[ListRes] = GalleonSdOnboardCodec.BuildLayerList(dup);

        rig.Settle(Solid(Red));

        Assert.True(GalleonSdOnboardCodec.TryParseLayerList(rig.Board.Store[ListRes], out var list));
        Assert.Equal(new ushort[] { 0x6D68 }.Concat(dup), list);
    }

    // ---- diagnostic read-back ----

    [Fact]
    public void Verify_mode_reads_every_write_back_and_counts_mismatches()
    {
        var rig = new Rig(verify: true);
        rig.Settle(Solid(Red));
        Assert.Equal(0, rig.Hub.VerifyMismatchCount);
        var tokens = rig.Board.Tokens();
        Assert.Equal(4, tokens.Count(t => t.StartsWith("WRITE:", StringComparison.Ordinal)));
        var firstClose = Array.IndexOf(tokens, "CLOSE:h1", Array.IndexOf(tokens, "WRITE:h1"));
        Assert.Equal(new[] { "OPEN:h0:6D68", "SIZE", "READ", "READ", "CLOSE:h0" }, tokens.Skip(firstClose + 1).Take(5));

        var bad = new Rig(verify: true);
        bad.Board.CorruptWrites = true;
        bad.Settle(Solid(Red));
        Assert.True(bad.Hub.VerifyMismatchCount > 0);
    }

    // ---- persistence ----

    [Fact]
    public void The_onboard_record_round_trips_through_the_settings_file()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexus-test-galleon-ob-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "settings.json");
        try
        {
            var store = new JsonConfigStore(path);
            try
            {
                store.Update(s => s.Devices.GalleonOnboard = new()
                {
                    ["SN1"] = new GalleonOnboardRecord
                    {
                        ProfileRes = 0x6D60, ProfileB64 = "AQI=", ListRes = 0x6D62, ListB64 = "AwQ=", RegistryB64 = "BQY=",
                        Owned = new() { new GalleonOwnedLayer { Id = 0x6D68, BodyB64 = "BwgJ" } },
                        WrittenListB64 = "CgsM",
                    },
                });
            }
            finally { store.Dispose(); }

            var reopened = new JsonConfigStore(path);
            try
            {
                var rec = reopened.Load().Devices.GalleonOnboard["SN1"];
                Assert.Equal(0x6D60, rec.ProfileRes);
                Assert.Equal("BQY=", rec.RegistryB64);
                Assert.Equal(0x6D68, Assert.Single(rec.Owned).Id);
                Assert.Equal("CgsM", rec.WrittenListB64);
                Assert.True(rec.HasBackup());
            }
            finally { reopened.Dispose(); }
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
