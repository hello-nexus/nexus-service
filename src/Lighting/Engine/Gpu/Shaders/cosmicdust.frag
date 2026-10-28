uniform float u_particles; // hint_range(0.3, 3.0, 0.05) = 1.8  density multiplier
uniform float u_twinkle; // hint_range(0.2, 4.0, 0.05) = 1.5  flicker rate
uniform float u_parallax; // hint_range(0.0, 1.2, 0.02) = 0.6  depth drift strength

// Glittering dust drifting through a colored gas haze. The haze is a slow
// domain-warped color field that always fills the frame; parallax
// layers of soft-haloed motes drift across it, each twinkling on its own phase.
void main() {
    vec2 uv = uv01();
    float ar = u_resolution.x / u_resolution.y;
    float t = u_time * u_speed * 0.12;
    float dens = clamp(u_particles, 0.3, 3.0);
    float twk = max(0.2, u_twinkle);
    float plx = clamp(u_parallax, 0.0, 1.2);

    vec2 p = vec2(uv.x * ar, uv.y);
    vec2 w = p * 2.3 + vec2(t * 0.9, -t * 0.6);
    float warp = fbm3(w + t);
    float haze = fbm3(w + vec2(warp, warp * 1.7 + 0.3) * 1.3 - t * 0.5);
    float body = smoothstep(0.2, 0.75, haze);
    vec3 col = tintedPalette(0.05 + haze * 1.0 + uv.x * 0.45 + t * 0.1) * (0.30 + 1.2 * body);

    for (int i = 0; i < 3; i++) {
        float layerZ = float(i) / 3.0;
        float drift = mix(1.0, 0.25, layerZ) * (0.35 + plx);
        float cells = (7.0 + layerZ * 6.0) * dens;
        vec2 q = p * cells;
        q.x += t * drift * 4.0;
        q.y += t * drift * 1.6 + sin(t * 2.0 + layerZ * 4.0) * 0.4;
        vec2 cell = floor(q);
        vec2 gv = fract(q) - 0.5;
        float rnd = hash21(cell + layerZ * 31.0);
        float has = step(0.45, rnd);
        vec2 off = vec2(hash21(cell + 7.1), hash21(cell + 13.7)) - 0.5;
        float d = length(gv - off * 0.5);
        float phase = fract(t * twk * 0.6 + rnd * 7.3);
        float tw = 0.35 + 0.65 * sin(phase * 6.28318) * sin(phase * 6.28318);
        float size = mix(1.0, 0.6, layerZ);
        float core = exp(-d * d * 120.0 / size);
        float halo = exp(-d * d * 30.0 / size) * (1.0 - smoothstep(0.1, 0.24, d)) * 0.7;
        vec3 tint = tintedPalette(rnd * 1.3 + t * 0.2);
        float br = mix(1.0, 0.6, layerZ) * tw * has;
        col += (vec3(1.0) * core * 1.4 + tint * (core * 1.0 + halo * 1.8)) * br;
    }

    fragColor = vec4(finalize(col), 1.0);
}
