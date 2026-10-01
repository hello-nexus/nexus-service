uniform float u_bands; // hint_range(2.0, 10.0, 0.5) = 5.0  ribbons across the frame
uniform float u_flow; // hint_range(0.3, 3.0, 0.05) = 1.5  how far the ribbons bend
uniform float u_glow; // hint_range(0.3, 2.0, 0.05) = 1.0  hot core of each ribbon
uniform float u_audioBoost; // hint_range(0.0, 2.0, 0.05) = 1.0

// Glowing lava ribbons on a dark ground: contour bands of a domain-warped
// noise field. Loudness bends, brightens and shifts them; a beat flashes the
// cores. Idle: the ribbons still drift and glow at a low simmer.
void main() {
    vec2 uv = uvCentered() * 1.3;
    float t = mod(u_time * u_speed * 0.12, 1000.0);
    float boost = clamp(u_audioBoost, 0.0, 2.0);
    float energy = clamp(u_audioLevel * 0.7 + u_audioBass * 0.5, 0.0, 1.0) * boost;
    float beat = u_audioBeat * boost;

    vec2 warp = vec2(vnoise(uv + vec2(t, -t * 0.7)),
                     vnoise(uv + vec2(-t * 0.8, t) + 7.3)) - 0.5;
    float f = vnoise(uv * 1.35 + warp * (clamp(u_flow, 0.3, 3.0) + energy * 1.5)
                     + vec2(t * 0.6, t * 0.4));

    // The ramp turns closed noise contours into bands that sweep the frame.
    float g = (f * 2.6 + dot(uv, vec2(0.35, 0.55)) + warp.y * 0.8)
              * clamp(u_bands, 2.0, 10.0) / 7.0;
    float d = abs(fract(g - t * 2.0 - u_audioLevel * boost * 0.35) - 0.5);
    float band = smoothstep(0.4, 0.04, d);
    band *= band * band;
    float hot = smoothstep(0.08, 0.0, d);

    vec3 ground = tintedPalette(0.0) * 0.05;
    vec3 tint = tintedPalette(f * 0.12);
    vec3 core = mix(tint, vec3(1.0), 0.55);
    float ribbon = 0.55 + 0.55 * energy + 0.8 * beat;
    vec3 col = ground + (tint * band + core * hot * 0.8 * clamp(u_glow, 0.3, 2.0)) * ribbon;
    // finalize() tonemaps, so the ribbons are authored hot.
    col *= 1.2 + 0.5 * energy + 0.5 * beat;

    fragColor = vec4(finalize(col), 1.0);
}
