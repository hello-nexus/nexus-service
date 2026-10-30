uniform float u_density; // hint_range(3.0, 14.0, 1.0) = 8.0  spike grid density
uniform float u_sharpness; // hint_range(0.4, 3.0, 0.05) = 1.5  spike sharpness
uniform float u_motion; // hint_range(0.0, 2.0, 0.05) = 1.0  spike height variation
uniform float u_mood; // hint_range(0.0, 5.0, 1.0) = 0.0  palette mood: 0 full spectrum, 1 warm, 2 cool, 3 neon, 4 sunset, 5 aurora
#define tintedPalette(t) moodPalette((t), u_mood)

// Magnetic liquid sea. The whole field sways in a slow swirl, magnetic waves
// roll across it raising and lowering the spikes, and every spike is a bright
// gradient cone with a white-hot tip. Between the spikes the pool is an
// iridescent color flow.
void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * 0.6, 1000.0);
    float dens = clamp(u_density, 2.0, 16.0);
    float sharp = clamp(u_sharpness, 0.2, 4.0);
    float mot = clamp(u_motion, 0.0, 2.5);

    // The field flows: a travelling swirl bends the grid so spikes slide, not just pulse.
    vec2 flow = vec2(sin(uv.y * 2.2 + t * 0.9), cos(uv.x * 1.8 - t * 0.7)) * 0.16 * (0.4 + mot);
    vec2 grid = (uv + flow) * dens;
    float row = floor(grid.y);
    grid.x += mod(row, 2.0) * 0.5;
    vec2 cell = floor(grid);
    vec2 inCell = fract(grid) - 0.5;

    float spikeHash = hash21(cell);
    // Magnetic wave front sweeping diagonally plus each spike's own phase.
    float wave = sin(dot(cell, vec2(0.55, 0.35)) - t * 2.6);
    float phase = spikeHash * 6.28 + t * (1.2 + spikeHash * 0.8);
    float height = 0.5 + 0.5 * sin(phase);
    height = (0.45 + 0.55 * height) * (0.65 + 0.35 * wave * (0.5 + mot)) * (0.5 + mot) + 0.35;
    height = max(height, 0.05);

    float r = length(inCell);
    float spike = pow(max(1.0 - r * 2.0, 0.0), sharp + 1.0);
    float spikeIntensity = spike * height;

    float poolFbm = fbm3(uv * 1.5 + vec2(t * 0.45, -t * 0.3));
    vec3 base = tintedPalette(0.1 + poolFbm * 0.9 + uv.y * 0.2 + t * 0.03) * (0.38 + poolFbm * 0.55);
    vec3 tipColor = tintedPalette(spikeHash * 0.5 + 0.45 + t * 0.06 + wave * 0.05);
    vec3 col = base + tipColor * spikeIntensity * 2.4;
    float halo = pow(max(1.0 - r * 1.4, 0.0), 2.0) * height;
    col += tipColor * halo * 0.6;
    col += vec3(0.95, 0.95, 1.0) * pow(spikeIntensity, 3.0) * 0.9;

    fragColor = vec4(finalize(col), 1.0);
}

// Output gain applied after the tonemap so the brightest pixel reaches full scale.
float outGain() { return 1.01; }
