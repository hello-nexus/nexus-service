uniform float u_density; // hint_range(8.0, 60.0, 1.0) = 45.0  stars per layer
uniform float u_layers; // hint_range(1.0, 8.0, 1.0) = 5.0  depth layers
uniform float u_trail; // hint_range(0.0, 1.0, 0.02) = 0.6  motion streak length
uniform float u_mood; // hint_range(0.0, 5.0, 1.0) = 0.0  palette mood: 0 full spectrum, 1 warm, 2 cool, 3 neon, 4 sunset, 5 aurora
#define tintedPalette(t) moodPalette((t), u_mood)

// Warp-speed flight through a colored sky. Every star is its own particle: it
// belongs to an angular bin, has its own random angle, start time and speed, and
// flies outward from the center, growing as it nears. Because no two stars share
// a timer, they appear one by one at independent places instead of in bursts. A
// slow palette wash and a central glow keep the whole frame lit behind them.
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.6;
    int lyrs = int(clamp(u_layers, 1.0, 3.0));
    float dens = clamp(u_density, 5.0, 80.0);
    float trail = clamp(u_trail, 0.0, 1.0);

    float r = max(length(uv), 1e-3);
    float turn = atan(uv.y, uv.x) / 6.28318 + 0.5;
    vec3 col = tintedPalette(sin(turn * 6.28318 + t * 0.15) * 0.22 + r * 0.25 - t * 0.03) * (0.20 + 0.30 * (1.0 - smoothstep(0.0, 1.6, r)));
    col += tintedPalette(0.4 + sin(t * 0.3) * 0.1 + uv.y * 0.2) * exp(-r * r * 1.4) * 0.45;

    for (int i = 0; i < 3; i++) {
        if (i >= lyrs) break;
        float fi = float(i);
        float bins = floor(dens * (0.7 + fi * 0.25));
        float b = turn * bins + fi * 13.0;
        float bi = floor(b);
        float layerRate = 0.16 + fi * 0.05;
        for (int dn = -2; dn <= 2; dn++) {
            float bn = bi + float(dn);
            vec2 sd = vec2(mod(bn, bins), fi + 1.0);
            float present = step(0.15, hash21(sd + 3.1));
            float start = hash21(sd + 7.7);
            float p = fract(t * layerRate * (0.55 + hash21(sd + 1.3) * 0.9) + start);
            float rs = pow(p, 1.7) * 2.3;
            float jit = hash21(sd + 5.5);
            float arc = (b - (bn + jit)) / bins * 6.28318 * r;
            float rad = r - rs;
            float size = 0.008 + 0.03 * p;
            float d2 = arc * arc + rad * rad;
            float core = exp(-d2 / (size * size));
            float glow = exp(-d2 / (size * size * 6.0)) * 0.7;
            float behind = step(rad, 0.0) * (1.0 - smoothstep(0.0, 0.05 + 0.45 * trail * p, -rad));
            float streak = behind * exp(-arc * arc / (size * size * 0.8)) * trail;
            float fade = smoothstep(0.0, 0.06, p) * (1.0 - smoothstep(0.9, 1.0, p)) * present;
            vec3 tint = tintedPalette(hash21(sd + 9.9) * 0.9 + fi * 0.1 + t * 0.02);
            col += (tint * (core + glow + streak * 1.2) + vec3(core * 0.7)) * (0.35 + 0.65 * p) * fade * 1.5;
        }
    }
    fragColor = vec4(finalize(col * 1.6), 1.0);
}

// Output gain applied after the tonemap so the brightest pixel reaches full scale.
float outGain() { return 1.04; }
