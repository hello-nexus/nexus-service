uniform float u_density; // hint_range(3.0, 14.0, 1.0) = 8.0  spike grid density
uniform float u_sharpness; // hint_range(0.4, 3.0, 0.05) = 1.5  spike sharpness
uniform float u_motion; // hint_range(0.0, 2.0, 0.05) = 1.0  spike height variation

// Magnetic black-liquid spike sea: a hex-ish grid of spikes whose
// heights pulse with time and noise. Each spike is rendered as a
// gradient cone, with bright iridescent tips against a dark base.
void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * 0.6, 1000.0);
    float dens = clamp(u_density, 2.0, 16.0);
    float sharp = clamp(u_sharpness, 0.2, 4.0);
    float mot = clamp(u_motion, 0.0, 2.5);

    // Hex-ish grid: skew odd rows for honeycomb packing.
    vec2 grid = uv * dens;
    float row = floor(grid.y);
    grid.x += mod(row, 2.0) * 0.5;
    vec2 cell = floor(grid);
    vec2 inCell = fract(grid) - 0.5;

    // Each spike has its own phase + base height.
    float spikeHash = hash21(cell);
    float phase = spikeHash * 6.28 + t * (1.0 + spikeHash * 0.6);
    float height = 0.5 + 0.5 * sin(phase);
    height = pow(height, 1.5) * mot + 0.3;

    // Spike profile: sharper at peak, soft base.
    float r = length(inCell);
    float spike = pow(max(1.0 - r * 2.0, 0.0), sharp + 1.0);
    float spikeIntensity = spike * height;

    // Iridescent pool base - liquid surface between spikes catches light
    // and shifts hue with the global palette so the frame is never black.
    // perf: fbm3 (3 oct) - this only drives a low-amplitude base tint, fine detail is invisible.
    float poolFbm = fbm3(uv * 1.6 + vec2(t * 0.3, -t * 0.2));
    vec3 base = tintedPalette(0.45 + poolFbm * 0.25 + uv.y * 0.1) *
                (0.15 + poolFbm * 0.15);
    vec3 tipColor = tintedPalette(spikeHash * 0.7 + t * 0.04);
    vec3 col = base + tipColor * spikeIntensity * 2.0;
    // Soft halo around each spike so the pool glows outward.
    float halo = pow(max(1.0 - r * 1.4, 0.0), 2.0) * height;
    col += tipColor * halo * 0.35;
    // White-hot rim at the tip.
    col += vec3(0.95, 0.95, 1.0) * pow(spikeIntensity, 4.0) * 0.8;

    fragColor = vec4(finalize(col), 1.0);
}
