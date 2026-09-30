uniform float u_scale; // hint_range(0.5, 3.0, 0.05) = 1.2  blob size
uniform float u_melt; // hint_range(0.2, 3.0, 0.05) = 1.2  how far the blobs melt into each other
uniform float u_glow; // hint_range(0.3, 2.0, 0.05) = 1.0  bloom on the hottest blobs
uniform float u_audioBoost; // hint_range(0.0, 2.0, 0.05) = 1.0

// Out-of-focus molten colour: large domain-warped blobs drift and melt into
// each other with no hard edge anywhere. Loudness swells and melts them, a
// beat blooms the hottest blobs, highs add a faint shimmer.
void main() {
    vec2 centered = uvCentered();
    vec2 uv = centered / clamp(u_scale, 0.5, 3.0);
    float t = mod(u_time * u_speed * 0.08, 1000.0);
    float boost = clamp(u_audioBoost, 0.0, 2.0);
    float energy = clamp(u_audioLevel * 0.7 + u_audioBass * 0.5, 0.0, 1.0) * boost;
    float beat = u_audioBeat * boost;

    vec2 q = vec2(fbm3(uv * 0.9 + vec2(t, t * 0.6)),
                  fbm3(uv * 0.9 + vec2(5.2 - t * 0.7, 1.3 + t * 0.4)));
    float melt = clamp(u_melt, 0.2, 3.0) * (1.0 + energy * 0.6);
    float f = fbm3(uv * 0.8 + q * melt + vec2(t * 0.3, -t * 0.2));

    float blob = smoothstep(0.2, 0.62, f);
    float hot = smoothstep(0.48, 0.78, f);
    vec3 ground = tintedPalette(0.5 + q.x * 0.1) * 0.12;
    vec3 body = tintedPalette(f * 0.25 + q.y * 0.15);
    vec3 heat = mix(body, vec3(1.0), 0.4);

    vec3 col = ground + body * blob * (0.45 + 0.45 * energy + 0.35 * beat);
    col += heat * hot * (0.25 + 0.6 * beat) * clamp(u_glow, 0.3, 2.0);
    col += body * blob * vnoise(uv * 6.0 + t * 4.0) * u_audioHigh * boost * 0.12;
    // finalize() tonemaps, so the haze is authored hot.
    col *= 2.2 * (1.0 - smoothstep(0.6, 1.8, length(centered)) * 0.5);

    fragColor = vec4(finalize(col), 1.0);
}
