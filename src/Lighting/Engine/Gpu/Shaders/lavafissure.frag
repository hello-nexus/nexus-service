uniform float u_flow; // hint_range(0.2, 2.5, 0.05) = 1.0  lava current rate
uniform float u_crackWidth; // hint_range(0.1, 0.9, 0.02) = 0.35  ridge thickness 0.1..0.9
uniform float u_shimmer; // hint_range(0.0, 1.5, 0.02) = 0.6  heat-haze distortion

// Magma fissure: dark cracked rock with glowing lava rivers. Ridge-mask
// carves channels where fbm crosses 0.5; a second warped field gives the
// lava a flowing sub-pattern and the shimmer uniform heat-warps the uv.
void main() {
    vec2 uv = uv01();
    float t = mod(u_time * u_speed * 0.35, 1000.0);
    float fl = max(0.2, u_flow);
    float cw = clamp(u_crackWidth, 0.08, 0.9);
    float shim = clamp(u_shimmer, 0.0, 1.5);

    vec2 p = uv * 2.6;
    p.x += sin(uv.y * 11.0 + t * fl * 1.3) * 0.025 * shim;
    p.y += cos(uv.x * 9.0 - t * fl * 0.9) * 0.02 * shim;

    // perf: all three fields fbm3 (3 oct) - ridge masks are thin smoothstep bands
    // that hide the top fbm octaves; cuts 15 octaves/px to 9. 1.107 restores fbm's
    // weight-sum so the 0.5 ridge crossings stay at the same density.
    vec2 q = p + vec2(t * 0.08 * fl, t * 0.03 * fl);
    float n = fbm3(q) * 1.107;
    float crack = 1.0 - smoothstep(0.0, cw * 0.32, abs(n - 0.5));
    float n2 = fbm3(q * 2.3 + vec2(3.1, -1.7)) * 1.107;
    float crack2 = 1.0 - smoothstep(0.0, cw * 0.22, abs(n2 - 0.5));
    float cracks = max(crack, crack2 * 0.7);

    float lavaFlow = fbm3(q * 1.6 + vec2(t * fl * 0.8, 0.0)) * 1.107;
    float heat = cracks * (0.35 + 0.65 * lavaFlow);

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
    fragColor = vec4(finalize(col), 1.0);
}
