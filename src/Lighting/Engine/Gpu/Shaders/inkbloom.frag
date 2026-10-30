uniform float u_spread; // hint_range(0.3, 2.0, 0.05) = 1.0  bloom growth rate
uniform float u_curl; // hint_range(0.0, 2.0, 0.05) = 0.6  tendril turbulence
uniform float u_fade; // hint_range(0.3, 3.0, 0.05) = 0.7  lifespan falloff
uniform float u_mood; // hint_range(0.0, 5.0, 1.0) = 0.0  palette mood: 0 full spectrum, 1 warm, 2 cool, 3 neon, 4 sunset, 5 aurora
#define tintedPalette(t) moodPalette((t), u_mood)

// Ink in water: overlapping drops, each growing a fbm-bordered blob from
// its own origin while its color thins with age. A slow swirl bends the whole
// field so the edges keep curling, and a faint tinted wash keeps the water
// itself colored. Additive blending lets overlapping drops mix into
// secondary hues.
void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * 0.3, 1000.0);
    float spr = max(0.2, u_spread);
    float curl = max(0.0, u_curl);
    float fade = max(0.3, u_fade);

    // Swirl: rotate the sampling point by an angle that varies with distance and time.
    float sw = sin(length(uv) * 2.4 - t * 1.3) * 0.35 * (0.3 + curl);
    float cs = cos(sw);
    float sn = sin(sw);
    vec2 puv = vec2(cs * uv.x - sn * uv.y, sn * uv.x + cs * uv.y);

    vec3 col = tintedPalette(0.55 + puv.x * 0.3 + sin(t * 0.7 + puv.y * 1.3) * 0.15) * 0.42;
    for (int i = 0; i < 4; i++) {
        float idx = float(i);
        float lifespan = 3.4;
        float ofs = idx * 0.85;
        float cyc = floor((t + ofs) / lifespan);
        float age = mod(t + ofs, lifespan) / lifespan;
        // Every cycle a drop lands somewhere new.
        vec2 origin = vec2(sin(idx * 2.1 + cyc * 3.7) * 0.9, cos(idx * 2.3 + cyc * 2.9) * 0.55);
        float rad = age * spr * 1.9 + 0.05;
        vec2 dir = puv - origin;
        float d = length(dir);
        float ang = atan(dir.y, dir.x);
        vec2 dn = dir / max(d, 1e-3);
        vec2 np = dn * 1.2 + vec2(age * 5.0 + idx * 4.0 + d * 5.0, t);
        float edgeN = 0.56 * vnoise(np) + 0.34 * vnoise(np * 2.02) - 0.5 + 0.05;
        float edge = d - rad + edgeN * curl * 0.5 * (0.3 + age);
        float blob = smoothstep(0.14, -0.04, edge);
        // Denser pigment toward the leading edge reads as ink, not a flat disc.
        float filament = 0.5 + 0.5 * sin(d * 16.0 - ang * 3.0 + age * 7.0 + edgeN * 9.0 + idx * 2.0);
        float body = (0.35 + 0.65 * smoothstep(-0.5, 0.0, edge)) * (0.55 + 0.45 * filament);
        float life = pow(max(1.0 - age, 0.0), fade) * smoothstep(0.0, 0.06, age);
        vec3 tint = tintedPalette(0.1 + idx * 0.27 + age * 0.12 + cyc * 0.13);
        col += tint * blob * body * life * 1.9;
    }

    fragColor = vec4(finalize(col), 1.0);
}

float outGain() { return 1.29; }
