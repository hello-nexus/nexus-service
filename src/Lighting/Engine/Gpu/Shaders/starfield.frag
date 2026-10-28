uniform float u_density; // hint_range(8.0, 60.0, 1.0) = 45.0  stars per layer
uniform float u_layers; // hint_range(1.0, 8.0, 1.0) = 5.0  depth layers
uniform float u_trail; // hint_range(0.0, 1.0, 0.02) = 0.6  motion streak length

// Warp-speed flight through a colored sky. Each depth layer cycles a zoom so
// its grid of stars streams outward from the center; a slow palette wash and a
// central glow keep the whole frame lit behind them. Only the cells
// nearest the pixel are sampled (glow and trail are bounded to stay inside
// them), which keeps the layers cheap.
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.6;
    int lyrs = int(clamp(u_layers, 1.0, 3.0));
    float dens = clamp(u_density, 5.0, 80.0);
    float trail = clamp(u_trail, 0.0, 1.0);

    float r = length(uv);
    float ang = atan(uv.y, uv.x) / 6.28318;
    vec3 col = tintedPalette(sin(ang * 6.28318 + t * 0.15) * 0.22 + r * 0.25 - t * 0.03) * (0.20 + 0.30 * smoothstep(1.6, 0.0, r));
    col += tintedPalette(0.4 + sin(t * 0.3) * 0.1 + uv.y * 0.2) * exp(-r * r * 1.4) * 0.45;

    for (int i = 0; i < 3; i++) {
        if (i >= lyrs) break;
        float fi = float(i);
        float layerRate = 0.14 + fi * 0.05;
        float phase = fract(t * layerRate + fi * 0.21);
        float zoom = 0.4 + phase * 2.6;
        float depth = phase;
        vec2 scaled = uv / zoom;
        float cellSize = max(0.14, 1.0 / (dens * 0.16));
        vec2 grid = scaled / cellSize;
        vec2 cellId = floor(grid);
        vec2 cellUv = fract(grid) - 0.5;
        vec2 radial = normalize(scaled + 0.001);
        float starR = (0.04 + 0.07 * depth) * (0.75 + fi * 0.1);
        float trailLen = starR * (4.0 + 8.0 * trail) * depth;
        float edgeFade = smoothstep(0.0, 0.08, phase) * smoothstep(1.0, 0.9, phase);
        vec2 nearest = vec2(cellUv.x < 0.0 ? -1.0 : 1.0, cellUv.y < 0.0 ? -1.0 : 1.0);

        for (int dy = 0; dy < 2; dy++) {
            for (int dx = 0; dx < 2; dx++) {
                vec2 cellOffset = vec2(float(dx), float(dy)) * nearest;
                vec2 neighborId = cellId + cellOffset;
                float brightness = hash21(neighborId + fi * 53.0);
                if (brightness < 0.22) continue;
                brightness = (brightness - 0.22) / 0.78;
                vec2 starPos = (vec2(hash21(neighborId + fi * 17.0), hash21(neighborId + fi * 31.0)) - 0.5) * 0.6;
                vec2 delta = cellUv - cellOffset - starPos;
                float d = length(delta);
                float core = smoothstep(starR, starR * 0.15, d) * brightness;
                float glow = smoothstep(starR * 3.2, 0.0, d) * 0.9 * brightness;
                float along = dot(delta, radial);
                float perp = length(delta - radial * along);
                float trailMask = (1.0 - smoothstep(0.0, min(trailLen * 1.6, 0.38), -along)) * step(0.0, -along) * smoothstep(starR * 1.5, 0.0, perp) * trail;
                vec3 tint = tintedPalette(hash21(neighborId + fi * 7.7) * 0.9 + fi * 0.1 + t * 0.02);
                col += (tint * (core + glow + trailMask * 1.5) + vec3(core * 0.6)) * depth * edgeFade;
            }
        }
    }
    fragColor = vec4(finalize(col * 2.0), 1.0);
}
