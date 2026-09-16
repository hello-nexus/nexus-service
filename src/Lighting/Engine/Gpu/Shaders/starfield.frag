uniform float u_density; // hint_range(8.0, 60.0, 1.0) = 45.0  stars per layer
uniform float u_layers; // hint_range(1.0, 8.0, 1.0) = 5.0  depth layers
uniform float u_trail; // hint_range(0.0, 1.0, 0.02) = 0.6  motion streak length

void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.6;
    int lyrs = int(clamp(u_layers, 1.0, 3.0)); // perf: depth cap 8->3
    float dens = clamp(u_density, 5.0, 80.0);
    float trail = clamp(u_trail, 0.0, 1.0);
    vec3 col = vec3(0.0);

    // Classic warp-speed: each layer has a cycling zoom phase.
    // As zoom grows the grid tiles spread outward from center, giving
    // the illusion of flying forward. When the phase wraps, new tiles
    // appear near the center seamlessly because the grid repeats.
    for (int i = 0; i < 3; i++) { // perf: 8->3 layer bound
        if (i >= lyrs) break;
        float fi = float(i);
        float layerRate = 0.12 + fi * 0.04;
        float phase = fract(t * layerRate + fi * 0.21);
        // Zoom 0.4 -> 3.0 over the cycle. Starts dense (far), ends spread (near).
        float zoom = 0.4 + phase * 2.6;
        float depth = phase; // brighter as it approaches
        vec2 scaled = uv / zoom;
        // Density slider actually has an effect across the whole range
        // now - the previous max(4.0, ...) pinned it to a constant
        // until dens > 33, which is why the low end "did nothing".
        float cellSize = max(0.12, 1.0 / (dens * 0.18));
        vec2 grid = scaled / cellSize;
        vec2 cellId = floor(grid);
        vec2 cellUv = fract(grid) - 0.5;
        // Radial trail direction: streak pointing away from the center of the frame.
        vec2 radial = normalize(scaled + 0.001);
        // Star grows as it zooms closer.
        // fill: bigger stars so the field stays dense with fewer depth layers (8->3)
        float starR = (0.035 + 0.09 * depth) * (0.65 + fi * 0.1);
        float trailLen = starR * (4.0 + 8.0 * trail) * depth;
        // Fade edges of the cycle so the phase-wrap seam isn't visible.
        float edgeFade = smoothstep(0.0, 0.08, phase) * smoothstep(1.0, 0.9, phase);

        // Sample the 3x3 cell neighborhood so a star whose glow extends past
        // its own cell boundary still contributes inside the neighbor cell,
        // instead of leaving a hard square seam at the boundary.
        for (int dy = -1; dy <= 1; dy++) {
            for (int dx = -1; dx <= 1; dx++) {
                vec2 cellOffset = vec2(float(dx), float(dy));
                vec2 neighborId = cellId + cellOffset;
                float brightness = hash21(neighborId + fi * 53.0);
                if (brightness < 0.30) continue;
                brightness = (brightness - 0.30) / 0.70;
                vec2 starPos = (vec2(hash21(neighborId + fi * 17.0), hash21(neighborId + fi * 31.0)) - 0.5) * 0.7;
                vec2 delta = cellUv - cellOffset - starPos;
                float d = length(delta);
                float core = smoothstep(starR, starR * 0.15, d) * brightness;
                // fill: wider glow halo to fill gaps left by fewer depth layers
                float glow = smoothstep(starR * 4.5, 0.0, d) * 0.85 * brightness;
                float along = dot(delta, radial);
                float perp = length(delta - radial * along);
                float trailMask = smoothstep(0.0, trailLen, -along) * smoothstep(starR * 1.5, 0.0, perp) * trail;
                vec3 tint = tintedPalette(brightness * 0.4 + fi * 0.1);
                col += tint * (core + glow + trailMask * 0.6) * depth * edgeFade;
            }
        }
    }
    fragColor = vec4(finalize(col * 2.2), 1.0);
}
