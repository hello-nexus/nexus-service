uniform float u_strokes;  // hint_range(2.0, 12.0, 1.0) = 4.0  strokes across the frame
uniform float u_feather;  // hint_range(0.02, 0.4, 0.01) = 0.12  taper at each stroke end
uniform float u_fill;     // hint_range(0.2, 1.0, 0.01) = 0.45  painted share of each pass

// Loaded brushstrokes in blues: each pass tapers in and out and carries a
// bristle grain, so the frame reads as paint dragged across it. Slower than
// the hard-edged tiles - a smear wants to be seen travelling.
void main() {
    vec2 uv = uv01();
    float t = u_time * u_speed * 0.7;
    // Bristles run along the travel axis; fine across it, stretched along it.
    // Advanced at the strokes' own rate (t is in stroke widths), so the grain
    // travels WITH the paint instead of sliding through it.
    float grain = 0.72 + 0.28 * vnoise(vec2(uv.x * 7.0 - t * 7.0 / max(1.0, u_strokes), uv.y * 36.0));
    float f = uv.x * u_strokes - t;
    float cell = floor(f);
    float d = f - cell;
    float feather = clamp(u_feather, 0.02, 0.4);
    // Painted up to the fill point, unlit past it, so the strokes read as
    // separate passes instead of one continuous band.
    float fill = clamp(u_fill, 0.2, 1.0);
    float body = smoothstep(0.0, feather, d) * smoothstep(fill, fill - feather, d);
    // Azure through indigo: enough spread to tell strokes apart, all blue.
    float hue = 0.56 + 0.06 * fract(cell * 0.37) + u_hue;
    vec3 col = hsv2rgb(vec3(hue, 0.85, 1.0)) * body * grain;
    fragColor = vec4(finalize(col), 1.0);
}
