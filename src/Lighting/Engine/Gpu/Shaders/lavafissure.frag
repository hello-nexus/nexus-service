uniform float u_flow; // hint_range(0.2, 2.5, 0.05) = 1.0  lava current rate
uniform float u_crackWidth; // hint_range(0.1, 0.9, 0.02) = 0.35  ridge thickness 0.1..0.9
uniform float u_shimmer; // hint_range(0.0, 1.5, 0.02) = 0.6  heat-haze distortion
uniform float u_mood; // hint_range(0.0, 5.0, 1.0) = 0.0  palette mood: 0 full spectrum, 1 warm, 2 cool, 3 neon, 4 sunset, 5 aurora
#define tintedPalette(t) moodPalette((t), u_mood)

// Magma fissure: dark cracked rock with glowing lava rivers. Ridge-mask
// carves channels where fbm crosses 0.5; a second warped field gives the
// lava a flowing sub-pattern and the shimmer uniform heat-warps the uv.
void main() {
    vec2 uv = uv01();
    float t = mod(u_time * u_speed * 0.5, 1000.0);
    float fl = max(0.2, u_flow);
    float cw = clamp(u_crackWidth, 0.08, 0.9);
    float shim = clamp(u_shimmer, 0.0, 1.5);

    vec2 p = uv * 2.6;
    // The whole fissure breathes: the field slowly warps so cracks shift and widen.
    p += 0.12 * vec2(sin(uv.y * 3.0 + t * 0.9), cos(uv.x * 2.6 - t * 0.7));
    p.x += sin(uv.y * 11.0 + t * fl * 1.3) * 0.025 * shim;
    p.y += cos(uv.x * 9.0 - t * fl * 0.9) * 0.02 * shim;

    // perf: all three fields fbm3 (3 oct) - ridge masks are thin smoothstep bands
    // that hide the top fbm octaves; cuts 15 octaves/px to 9. 1.107 restores fbm's
    // weight-sum so the 0.5 ridge crossings stay at the same density.
    vec2 q = p + vec2(t * 0.08 * fl, t * 0.03 * fl);
    float n = fbm3(q) * 1.107;
    float widen = 1.0 + 0.35 * sin(t * 0.8 + n * 9.0);
    float crack = 1.0 - smoothstep(0.0, cw * 0.32 * widen, abs(n - 0.5));
    float n2 = fbm3(q * 2.3 + vec2(3.1, -1.7)) * 1.107;
    float crack2 = 1.0 - smoothstep(0.0, cw * 0.22, abs(n2 - 0.5));
    float cracks = max(crack, crack2 * 0.7);

    float lavaFlow = fbm3(q * 1.6 + vec2(t * fl * 1.6, t * fl * 0.9)) * 1.107;
    float pulse = 0.82 + 0.28 * sin(t * 2.6 + lavaFlow * 8.0 + n * 6.0);
    float heat = min(cracks * (0.35 + 0.65 * lavaFlow) * pulse, 1.0);

    vec3 rock = mix(vec3(0.02, 0.01, 0.01), vec3(0.09, 0.04, 0.02),
                    smoothstep(0.3, 0.7, n));
    // Lava colour is driven by the noise field so different regions pick
    // different hues. At hue=0 colorize=0 the full field reads as rainbow
    // lava; at hue=0.03 colorize=0.45 finalize() collapses it toward red.
    float lavaHue = n * 0.9 + lavaFlow * 0.15 + t * 0.02;
    vec3 lava = mix(tintedPalette(lavaHue), tintedPalette(lavaHue + 0.08),
                    smoothstep(0.15, 0.7, heat));
    lava = mix(lava, vec3(1.0, 0.85, 0.55), pow(heat, 3.5));

    vec3 col = mix(rock, lava * 1.6, cracks);
    // Heat bleeds into the rock around the cracks.
    col += tintedPalette(lavaHue) * pow(cracks, 0.5) * 0.12 * pulse;
    // Embers rise through the frame and flicker out.
    vec2 ep = vec2(uv.x * 34.0, uv.y * 20.0 + t * 7.0);
    vec2 ec = floor(ep);
    vec2 ef = fract(ep) - 0.5;
    float eh = hash21(ec);
    vec2 ejit = vec2(hash21(ec + 3.3), hash21(ec + 8.8)) - 0.5;
    float ember = step(0.93, eh) * exp(-dot(ef - ejit * 0.5, ef - ejit * 0.5) * 70.0)
                * (0.5 + 0.5 * sin(t * 6.0 + eh * 60.0));
    col += mix(vec3(1.0, 0.6, 0.2), tintedPalette(lavaHue), 0.4) * ember * 2.4 * (0.3 + 0.7 * smoothstep(0.0, 0.8, 1.0 - uv.y));
    fragColor = vec4(finalize(col), 1.0);
}
