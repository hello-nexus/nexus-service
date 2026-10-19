using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;
using Nexus.Service.Rendering;
using SkiaSharp;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>
/// Composes a moving sprite over a 400x400 background into a baseline JPEG for the
/// Flex fan screens. Motion lives in a 360 px area centered in the frame. Pure and
/// deterministic in the elapsed time and seed; not thread safe (Bounce and Collision
/// cache a stepped simulation and restart it when time moves backwards). Owns the
/// bitmaps it is given and disposes them.
/// </summary>
public sealed class FlexOverlayRenderer : IDisposable
{
    public const int FrameSize = 400;
    public const int AreaSize = 360;
    private const float AreaOffset = (FrameSize - AreaSize) / 2f;
    private const float AreaCenter = AreaSize / 2f;

    private static readonly double[] BounceTable = [0.0, 0.7, 0.8, 0.9, 0.95];
    private static readonly double[] RotationTable = [0.4, 0.7, 1.0];
    private const double RotationDecay = 0.8;
    private const double RotationMax = 15.0;
    private const double RestSpeedFrames = 1.5;

    private readonly LianLiFlexOverlaySettings _settings;
    private readonly SKBitmap? _background;
    private readonly IReadOnlyList<SKBitmap> _frames;
    private readonly IReadOnlyList<int> _delaysMs;
    private readonly int _seed;
    private readonly int _speed;
    private readonly double _zoom;
    private readonly double _dirSign;
    private readonly string _type;
    private readonly bool _animatedSprite;
    private readonly double _totalDelayMs;

    private BounceSim? _bounce;
    private CollisionSim? _collision;
    private SKBitmap?[]? _scaled;

    public FlexOverlayRenderer(
        LianLiFlexOverlaySettings settings,
        SKBitmap? background,
        IReadOnlyList<SKBitmap> spriteFrames,
        IReadOnlyList<int> spriteDelaysMs,
        int seed = 1)
    {
        _settings = settings;
        _background = background;
        _frames = spriteFrames;
        _delaysMs = spriteDelaysMs;
        _seed = seed;
        _speed = Math.Clamp(settings.Speed, 1, 10);
        _zoom = Math.Clamp(settings.Zoom, 50, 200) / 100.0;
        _dirSign = settings.Direction == 1 ? -1.0 : 1.0;
        _type = settings.Type;
        _animatedSprite = spriteFrames.Count > 1;
        for (var i = 0; i < spriteFrames.Count; i++)
        {
            _totalDelayMs += DelayAt(i);
        }

        FrameIntervalMs = _type switch
        {
            "rotate" => (int)Math.Round(1000.0 / (_animatedSprite ? 25 + (_speed - 1) * 10 : 20)),
            "collision" => 40,
            "bounce" => 50,
            _ => (int)Math.Max(10, Math.Round(1000.0 / (20 + (_speed - 1) * 5))),
        };
    }

    public int FrameIntervalMs { get; }

    public bool Finished { get; private set; }

    public byte[] RenderJpeg(double elapsedSeconds)
    {
        var t = Math.Max(0.0, elapsedSeconds);
        var (x, y, rotation) = Motion(t);

        using var image = RenderKit.NewImage(FrameSize, FrameSize, SKColors.Black);
        using (var canvas = new SKCanvas(image))
        {
            DrawBackground(canvas);
            DrawSprite(canvas, t, x, y, rotation);
        }

        return RenderKit.EncodeJpeg(image);
    }

    public void Dispose()
    {
        _background?.Dispose();
        foreach (var frame in _frames)
        {
            frame.Dispose();
        }

        if (_scaled is not null)
        {
            foreach (var frame in _scaled)
            {
                frame?.Dispose();
            }
        }
    }

    private int DelayAt(int index) => index < _delaysMs.Count ? Math.Max(10, _delaysMs[index]) : 100;

    private (double X, double Y, double RotationDeg) Motion(double t) => _type switch
    {
        "rotate" => MotionRotate(t),
        "bounce" => MotionBounce(t),
        "collision" => MotionCollision(t),
        _ => MotionCircular(t),
    };

    /// <summary>
    /// Revolutions per second equal speed * 0.2: the per-frame step
    /// (speed * 0.2 * interval/1000 turns) is independent of the interval.
    /// Starts at the right of the orbit and runs clockwise on screen for direction 0.
    /// </summary>
    private (double, double, double) MotionCircular(double t)
    {
        var turnsPerSecond = _speed * 0.2;
        var turns = t * turnsPerSecond;
        if (!_settings.Loop && turns >= 1.0)
        {
            turns = 1.0;
            Finished = true;
        }

        var angle = turns * 2.0 * Math.PI * _dirSign;
        var radius = Math.Max(0, _settings.Params.Radius);
        var x = _settings.CenterX + Math.Cos(angle) * radius;
        var y = _settings.CenterY + Math.Sin(angle) * radius;
        return (x, y, _settings.Params.SelfRotate ? angle * 180.0 / Math.PI : 0.0);
    }

    private (double, double, double) MotionRotate(double t)
    {
        var sweep = Math.Max(1, _settings.Params.RotateAngle);
        var fps = _animatedSprite ? 25 + (_speed - 1) * 10 : 20;
        var sweepSeconds = sweep / (_speed * 7.2) / fps;
        var phases = _settings.Params.AutoReverse ? 2.0 : 1.0;
        var cycles = t / sweepSeconds / phases;
        double progress;
        if (!_settings.Loop && cycles >= 1.0)
        {
            Finished = true;
            progress = phases;
        }
        else
        {
            progress = cycles % 1.0 * phases;
        }

        var unit = progress <= 1.0 ? progress : 2.0 - progress;
        return (_settings.CenterX, _settings.CenterY, unit * sweep * _dirSign);
    }

    private (double, double, double) MotionBounce(double t)
    {
        _bounce ??= new BounceSim(_settings, _speed);
        var step = (long)Math.Floor(t * 1000.0 / FrameIntervalMs);
        var offset = _bounce.Advance(step, _settings.Loop);
        if (_bounce.Settled && !_settings.Loop)
        {
            Finished = true;
        }

        return (_settings.CenterX, _settings.CenterY - offset, 0.0);
    }

    private (double, double, double) MotionCollision(double t)
    {
        var (w, h) = SpriteSize();
        _collision ??= new CollisionSim(_seed, _speed, RotationTable[Math.Clamp(_settings.Params.RotationIntensity, 0, 2)], Math.Max(w, h));
        _collision.Advance((long)Math.Floor(t * 1000.0 / FrameIntervalMs));
        return (_collision.X, _collision.Y, _collision.Rotation * _dirSign);
    }

    private (double W, double H) SpriteSize() =>
        _frames.Count == 0 ? (0, 0) : (_frames[0].Width * _zoom, _frames[0].Height * _zoom);

    private void DrawBackground(SKCanvas canvas)
    {
        if (_background is null)
        {
            return;
        }

        if (_background.Width == FrameSize && _background.Height == FrameSize)
        {
            RenderKit.DrawImage(canvas, _background, 0, 0);
            return;
        }

        using var fitted = RenderKit.Resize(_background, FrameSize, FrameSize);
        RenderKit.DrawImage(canvas, fitted, 0, 0);
    }

    private void DrawSprite(SKCanvas canvas, double t, double x, double y, double rotationDeg)
    {
        if (_frames.Count == 0)
        {
            return;
        }

        var index = SpriteFrameIndex(t);
        var scaled = ScaledFrame(index);
        var cx = AreaOffset + (float)Math.Clamp(x, 0, AreaSize);
        var cy = AreaOffset + (float)Math.Clamp(y, 0, AreaSize);
        if (Math.Abs(rotationDeg % 360.0) < 0.01)
        {
            RenderKit.DrawImage(canvas, scaled, (int)Math.Round(cx - scaled.Width / 2f), (int)Math.Round(cy - scaled.Height / 2f));
            return;
        }

        using var rotated = Rotate(scaled, rotationDeg);
        RenderKit.DrawImage(canvas, rotated, (int)Math.Round(cx - rotated.Width / 2f), (int)Math.Round(cy - rotated.Height / 2f));
    }

    private SKBitmap ScaledFrame(int index)
    {
        _scaled ??= new SKBitmap?[_frames.Count];
        if (_scaled[index] is { } cached)
        {
            return cached;
        }

        var src = _frames[index];
        var w = Math.Max(1, (int)Math.Round(src.Width * _zoom));
        var h = Math.Max(1, (int)Math.Round(src.Height * _zoom));
        var made = RenderKit.Resize(src, w, h);
        _scaled[index] = made;
        return made;
    }

    /// <summary>
    /// CPU rotation about the center into a bitmap sized to the rotated bounds
    /// (bilinear, premultiplied RGBA). The Skia build in use has no canvas
    /// transform entry points, so this does not go through SKCanvas.
    /// </summary>
    private static SKBitmap Rotate(SKBitmap src, double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var w = src.Width;
        var h = src.Height;
        var outW = (int)Math.Ceiling(Math.Abs(w * cos) + Math.Abs(h * sin)) + 1;
        var outH = (int)Math.Ceiling(Math.Abs(w * sin) + Math.Abs(h * cos)) + 1;
        var dst = new SKBitmap(RenderKit.Info(outW, outH));
        var from = src.GetPixelSpan();
        var to = dst.GetPixelSpan();
        to.Clear();
        var srcStride = src.RowBytes;
        var dstStride = dst.RowBytes;
        for (var dy = 0; dy < outH; dy++)
        {
            for (var dx = 0; dx < outW; dx++)
            {
                var px = dx + 0.5 - outW / 2.0;
                var py = dy + 0.5 - outH / 2.0;
                var sx = px * cos + py * sin + w / 2.0 - 0.5;
                var sy = -px * sin + py * cos + h / 2.0 - 0.5;
                if (sx < -0.5 || sy < -0.5 || sx > w - 0.5 || sy > h - 0.5)
                {
                    continue;
                }

                var x0 = (int)Math.Floor(sx);
                var y0 = (int)Math.Floor(sy);
                var fx = sx - x0;
                var fy = sy - y0;
                for (var c = 0; c < 4; c++)
                {
                    var v00 = Texel(from, srcStride, w, h, x0, y0, c);
                    var v10 = Texel(from, srcStride, w, h, x0 + 1, y0, c);
                    var v01 = Texel(from, srcStride, w, h, x0, y0 + 1, c);
                    var v11 = Texel(from, srcStride, w, h, x0 + 1, y0 + 1, c);
                    var v = v00 * (1 - fx) * (1 - fy) + v10 * fx * (1 - fy) + v01 * (1 - fx) * fy + v11 * fx * fy;
                    to[dy * dstStride + dx * 4 + c] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
                }
            }
        }

        return dst;
    }

    // Outside the source reads as transparent so edges fade rather than smear.
    private static double Texel(ReadOnlySpan<byte> px, int stride, int w, int h, int x, int y, int channel) =>
        x < 0 || y < 0 || x >= w || y >= h ? 0.0 : px[y * stride + x * 4 + channel];

    private int SpriteFrameIndex(double t)
    {
        if (!_animatedSprite || _totalDelayMs <= 0)
        {
            return 0;
        }

        var ms = t * 1000.0 % _totalDelayMs;
        for (var i = 0; i < _frames.Count; i++)
        {
            ms -= DelayAt(i);
            if (ms < 0)
            {
                return i;
            }
        }

        return _frames.Count - 1;
    }

    /// <summary>
    /// Vertical offset above the rest position, in px. Falls from the up height under
    /// gravity (px per frame squared), rebounds with the restitution, then dwells at
    /// the bottom and (when looping) repeats.
    /// </summary>
    private sealed class BounceSim
    {
        private readonly double _up;
        private readonly double _restitution;
        private readonly double _gravity;
        private readonly int _dwellFrames;
        private double _offset;
        private double _velocity;
        private int _dwell;
        private long _step;
        private bool _resting;

        public BounceSim(LianLiFlexOverlaySettings settings, int speed)
        {
            _up = Math.Max(0, settings.Params.UpHeight);
            _restitution = BounceTable[Math.Clamp(settings.Params.Bounciness, 0, BounceTable.Length - 1)];
            _gravity = 0.5 + speed * 0.1;
            _dwellFrames = (int)Math.Round(Math.Max(0, settings.Params.DwellSeconds) * 1000.0 / 50.0);
            Reset();
        }

        public bool Settled { get; private set; }

        public double Advance(long targetStep, bool loop)
        {
            if (targetStep < _step)
            {
                Reset();
            }

            while (_step < targetStep && !Settled)
            {
                Tick(loop);
                _step++;
            }

            return _offset;
        }

        private void Reset()
        {
            _offset = _up;
            _velocity = 0;
            _dwell = 0;
            _step = 0;
            _resting = false;
            Settled = false;
        }

        private void Tick(bool loop)
        {
            if (_resting)
            {
                _dwell++;
                if (_dwell < _dwellFrames)
                {
                    return;
                }

                if (loop)
                {
                    _offset = _up;
                    _velocity = 0;
                    _dwell = 0;
                    _resting = false;
                }
                else
                {
                    Settled = true;
                }

                return;
            }

            _velocity += _gravity;
            _offset -= _velocity;
            if (_offset > 0)
            {
                return;
            }

            _offset = 0;
            _velocity = -_velocity * _restitution;
            if (_restitution <= 0 || Math.Abs(_velocity) < RestSpeedFrames * _gravity)
            {
                _velocity = 0;
                _resting = true;
                _dwell = 0;
                return;
            }

            // Velocity is downward speed, so the rebound is negative and the next tick lifts the sprite.
        }
    }

    /// <summary>
    /// Fixed-step billiard inside a circle around the area center. Speed is px per
    /// frame. A wall hit reflects the velocity and kicks the spin, which then decays.
    /// </summary>
    private sealed class CollisionSim
    {
        private readonly int _seed;
        private readonly double _startSpeed;
        private readonly double _intensity;
        private readonly double _radius;
        private Random _rng = null!;
        private double _vx;
        private double _vy;
        private double _spinSign;
        private double _spin;
        private long _step;

        public CollisionSim(int seed, int speed, double intensity, double spriteExtent)
        {
            _seed = seed;
            _startSpeed = 10 + (speed - 1) * 5;
            _intensity = intensity;
            _radius = Math.Max(1.0, AreaCenter - spriteExtent / 2.0);
            Reset();
        }

        public double X { get; private set; }

        public double Y { get; private set; }

        public double Rotation { get; private set; }

        public void Advance(long targetStep)
        {
            if (targetStep < _step)
            {
                Reset();
            }

            while (_step < targetStep)
            {
                Tick();
                _step++;
            }
        }

        private void Reset()
        {
            _rng = new Random(_seed);
            var angle = _rng.NextDouble() * 2.0 * Math.PI;
            _vx = Math.Cos(angle) * _startSpeed;
            _vy = Math.Sin(angle) * _startSpeed;
            _spinSign = _rng.Next(2) == 0 ? 1.0 : -1.0;
            _spin = 0;
            X = AreaCenter;
            Y = AreaCenter;
            Rotation = 0;
            _step = 0;
        }

        private void Tick()
        {
            X += _vx;
            Y += _vy;
            var dx = X - AreaCenter;
            var dy = Y - AreaCenter;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > _radius)
            {
                var nx = dx / dist;
                var ny = dy / dist;
                var dot = _vx * nx + _vy * ny;
                _vx -= 2 * dot * nx;
                _vy -= 2 * dot * ny;
                X = AreaCenter + nx * _radius;
                Y = AreaCenter + ny * _radius;
                _spin = Math.Min(RotationMax, _spin + RotationMax * _intensity);
            }

            Rotation += _spin * _spinSign;
            _spin *= RotationDecay;
        }
    }
}
