uniform float u_strokes;  // hint_range(2.0, 12.0, 1.0) = 4.0  color patches across the frame
uniform float u_feather;  // hint_range(0.02, 0.4, 0.01) = 0.12  softness of the dry-brush gaps
uniform float u_fill;     // hint_range(0.2, 1.0, 0.01) = 0.45  paint coverage, higher leaves fewer gaps
uniform float u_step;     // hint_range(0.0, 0.4, 0.01) = 0.2  hue change per patch

// Wet paint dragged along the strip: a full field whose hue drifts smoothly
// and blends between patches, with bristle streak noise that modulates
// brightness and hue. Noise is sampled in paint coordinates, so the texture
// travels with the color and slowly evolves as it goes. Gaps are rare and small.
void main() {
    vec2 uv = uv01();
    float t = u_time * u_speed * 0.7;
    float f = uv.x * max(2.0, u_strokes) - t;
    float y = uv.y;
    float drift = u_time * u_speed * 0.06;

    // Smooth hue flow with a wobble so patches differ in length.
    float flow = f + 0.7 * vnoise(vec2(f * 0.45, 4.0));
    float hue = u_hue + flow * u_step + 0.12 * (vnoise(vec2(f * 0.8, 9.0)) - 0.5);

    // Bristle streaks at two scales; the slow drift term makes them shimmer.
    float s1 = vnoise(vec2(f * 2.2 + drift, y * 24.0 + drift * 1.7));
    float s2 = vnoise(vec2(f * 5.5 - drift * 1.3, y * 55.0 + 3.0));
    float tex = 0.6 * s1 + 0.4 * s2;

    hue += (tex - 0.5) * 0.14;
    float sat = 0.82 + 0.18 * s2;
    float val = 0.5 + 0.5 * smoothstep(0.1, 0.9, tex);

    // Dry-brush gaps: only the lowest streak troughs drop out.
    float feather = clamp(u_feather, 0.02, 0.4);
    float gapAt = (1.0 - clamp(u_fill, 0.2, 1.0)) * 0.3;
    float paint = smoothstep(gapAt, gapAt + feather, mix(s1, s2, 0.5));
    val *= mix(0.25, 1.0, paint);

    vec3 col = hsv2rgb(vec3(hue, sat, val));
    fragColor = vec4(finalize(col), 1.0);
}
