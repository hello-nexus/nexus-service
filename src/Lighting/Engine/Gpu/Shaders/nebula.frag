uniform float u_density; // hint_range(0.3, 2.0, 0.05) = 1.0  cloud thickness
uniform float u_stars; // hint_range(0.0, 1.0, 0.02) = 0.6  star brightness
uniform float u_depth; // hint_range(1.0, 6.0, 1.0) = 4.0  number of cloud layers
uniform float u_mood; // hint_range(0.0, 5.0, 1.0) = 0.0  palette mood: 0 full spectrum, 1 warm, 2 cool, 3 neon, 4 sunset, 5 aurora
#define tintedPalette(t) moodPalette((t), u_mood)

// Layered gas clouds over a colored sky. A slow gradient keeps every pixel
// lit; drifting, domain-warped cloud layers each take their own slice of
// the palette and blend additively, so overlaps mix into new colors.
void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * 1.6, 1000.0);
    float dens = max(0.1, u_density);
    float starBr = clamp(u_stars, 0.0, 1.5);
    int layers = int(clamp(u_depth, 1.0, 3.0));

    vec3 col = tintedPalette(0.62 + uv.y * 0.35 + sin(t * 0.2) * 0.08) * 0.34;
    col += tintedPalette(0.1 + uv.x * 0.3 - t * 0.03) * 0.22;

    vec2 starGrid = floor(uv * 60.0);
    float starVal = hash21(starGrid);
    col += vec3(pow(starVal, 60.0)) * starBr * 1.2;

    for (int i = 0; i < 3; i++) {
        if (i >= layers) break;
        float fi = float(i);
        float scale = 0.9 + fi * 0.5;
        vec2 q = uv * scale + vec2(
            sin(t * 0.3 + fi * 1.4) * 0.45,
            cos(t * 0.25 + fi * 2.1) * 0.35);
        q += vec2(fbm3(q + t * 0.14), fbm3(q + vec2(3.0, 7.0) + t * 0.11)) * 0.6;
        float n = fbm3(q);
        n = n * 0.75 + fbm3(q * 2.6 - t * 0.04) * 0.25;
        float cloud = smoothstep(0.22, 0.62, n * dens);
        float core = smoothstep(0.62, 0.95, n * dens);
        vec3 tint = tintedPalette(fi * 0.3 + 0.02 + n * 0.7);
        col += tint * cloud * (0.50 + 0.10 * fi);
        col += tintedPalette(fi * 0.3 + 0.3) * core * 0.9;
    }
    fragColor = vec4(finalize(col), 1.0);
}

// Output gain applied after the tonemap so the brightest pixel reaches full scale.
float outGain() { return 1.37; }
