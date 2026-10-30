uniform float u_particles; // hint_range(0.3, 3.0, 0.05) = 1.8  density multiplier
uniform float u_twinkle; // hint_range(0.2, 4.0, 0.05) = 1.5  flicker rate
uniform float u_parallax; // hint_range(0.0, 1.2, 0.02) = 0.6  depth drift strength
uniform float u_mood; // hint_range(0.0, 5.0, 1.0) = 0.0  palette mood: 0 full spectrum, 1 warm, 2 cool, 3 neon, 4 sunset, 5 aurora
#define tintedPalette(t) moodPalette((t), u_mood)

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

    vec2 cq = p * 1.25 + vec2(0.0, t * 0.05);
    float clusterField = 0.62 * vnoise(cq) + 0.38 * vnoise(cq * 2.03 + 4.1) + 0.04;
    for (int i = 0; i < 3; i++) {
        float layerZ = float(i) / 3.0;
        // Each layer has its own grid scale, rotation and offset, and drifts in its own direction, so no lattice lines up between them.
        float ang = 0.55 + float(i) * 1.37;
        mat2 rot = mat2(cos(ang), -sin(ang), sin(ang), cos(ang));
        float drift = mix(1.0, 0.25, layerZ) * (0.35 + plx);
        float cells = (7.0 + float(i) * 3.7) * dens;
        vec2 flow = vec2(cos(ang * 2.3 + 0.8), sin(ang * 1.7 + 0.3)) * t * drift * 4.0;
        vec2 q = rot * p * cells + flow + vec2(float(i) * 17.3, float(i) * 9.1);
        vec2 cell = floor(q);
        vec2 gv = fract(q) - 0.5;
        float rnd = hash21(cell + float(i) * 31.0);
        // Density follows a slow low-frequency field, so some regions are nearly empty and others gather stars.
        float cluster = smoothstep(0.4, 0.68, clusterField + 0.12 * sin(p.x * 2.3 + float(i) * 2.1) * sin(p.y * 1.9 + float(i)));
        float thr = mix(0.985, 0.64, cluster);
        // Most cells hold no star, so skip all of the star work for them.
        if (rnd < thr) {
            continue;
        }
        float has = smoothstep(thr, thr + 0.07, rnd);
        // Full-cell jitter; the halo fades out before the cell edge so nothing is cut.
        vec2 off = vec2(hash21(cell + 7.1), hash21(cell + 13.7)) - 0.5;
        float d = length(gv - off * 0.62);
        float rnd2 = hash21(cell + 91.7);
        float phase = fract(t * twk * (0.35 + rnd2 * 0.6) + rnd * 7.3);
        float tw = 0.25 + 0.75 * sin(phase * 6.28318) * sin(phase * 6.28318);
        float size = mix(1.0, 0.6, layerZ) * (0.35 + rnd2 * rnd2 * 2.6);
        float core = exp(-d * d * 120.0 / size) * (1.0 - smoothstep(0.1, 0.19, d));
        float halo = exp(-d * d * 30.0 / size) * (1.0 - smoothstep(0.08, 0.19, d)) * 0.7;
        vec3 tint = tintedPalette(rnd * 1.3 + t * 0.2);
        float br = mix(1.0, 0.6, layerZ) * tw * has * (0.3 + 1.4 * rnd2 * rnd2 * rnd2);
        col += (vec3(1.0) * core * 1.4 + tint * (core * 1.0 + halo * 1.8)) * br;
    }

    fragColor = vec4(finalize(col), 1.0);
}

// Output gain applied after the tonemap so the brightest pixel reaches full scale.
float outGain() { return 1.17; }
