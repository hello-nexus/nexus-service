uniform float u_strokes;  // hint_range(2.0, 12.0, 1.0) = 4.0  strokes across the frame
uniform float u_feather;  // hint_range(0.02, 0.4, 0.01) = 0.12  taper at each stroke end
uniform float u_fill;     // hint_range(0.2, 1.0, 0.01) = 0.45  painted share of each pass
uniform float u_step;     // hint_range(0.0, 0.4, 0.01) = 0.2  hue step between neighbouring strokes

// Loaded rainbow brushstrokes: each pass tapers in and out and carries a
// bristle grain, so the frame reads as paint dragged across it. Slower than
// the hard-edged tiles - a smear wants to be seen travelling.
void main() {
    vec2 uv = uv01();
    // Wrapped first: seconds of the day times speed is large enough that float
    // rounding quantizes the stroke phase, more so at high speed.
    float t = mod(u_time, 600.0) * u_speed * 0.7;
    // Bristles run along the travel axis; fine across it, stretched along it.
    // Advanced at the strokes' own rate (t is in stroke widths), so the grain
    // travels WITH the paint instead of sliding through it.
    float grain = 0.72 + 0.28 * vnoise(vec2(uv.x * 7.0 - t * 7.0 / max(1.0, u_strokes), uv.y * 36.0));
    // Broad, slow streaks stretched along the travel axis: a faint brightness
    // and hue ripple that drifts with the paint. Low frequency so neighbouring
    // LEDs see a continuous gradient.
    float streak = vnoise(vec2(uv.x * 2.0 - t * 2.0 / max(1.0, u_strokes), uv.y * 4.0 + mod(u_time, 600.0) * 0.05));
    grain *= 0.88 + 0.24 * streak;
    // Silk-style folds: two sines crossing at different angles with drifting
    // phases, so brightness undulates along y like cloth. Low frequency keeps
    // neighbouring LEDs on a smooth slope; the x term tilts the folds and the
    // time term keeps a 1D strip (fixed y) breathing.
    float fold = 0.5 * sin(uv.y * 5.0 + uv.x * 1.5 + t * 0.35)
               + 0.3 * sin(uv.y * 9.0 - uv.x * 0.8 - t * 0.25 + 0.9)
               + 0.2 * sin(uv.y * 3.0 + uv.x * 0.5 + t * 0.15 + 2.1);
    grain *= 1.0 + 0.1 * fold;
    float f = uv.x * u_strokes - t;
    float cell = floor(f);
    float d = f - cell;
    float feather = clamp(u_feather, 0.02, 0.4);
    // Painted up to the fill point, unlit past it, so the strokes read as
    // separate passes instead of one continuous band.
    float fill = clamp(u_fill, 0.2, 1.0);
    float body = smoothstep(0.0, feather, d) * smoothstep(fill, fill - feather, d);
    // One hue step per stroke.
    vec3 col = hsv2rgb(vec3(cell * u_step + u_hue + 0.03 * (streak - 0.5) + 0.008 * fold, 1.0, 1.0)) * body * grain;
    fragColor = vec4(finalize(col), 1.0);
}
