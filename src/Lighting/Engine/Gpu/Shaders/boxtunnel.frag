uniform float u_depth; // hint_range(0.5, 4.0, 0.05) = 1.2  frame spacing / fly rate
uniform float u_square; // hint_range(0.0, 1.0, 0.02) = 1.0  0 = round rings, 1 = screen-fitted rectangle
uniform float u_glow; // hint_range(0.3, 2.0, 0.05) = 1.0  frame glow

// Rectangular infinity-mirror tunnel. Uses NON aspect-corrected centred
// coords (uv01*2-1), so the box metric max(|x|,|y|)=1 hugs the actual
// draw rectangle - the frames match the screen they're projected on (wide
// on a monitor, tall on a portrait panel). u_square blends that rectangle
// toward a round tunnel.
void main() {
    vec2 p = uv01() * 2.0 - 1.0;
    float t = u_time * u_speed * clamp(u_depth, 0.5, 4.0);
    float sq = clamp(u_square, 0.0, 1.0);
    float glow = clamp(u_glow, 0.1, 3.0);

    float d = mix(length(p), max(abs(p.x), abs(p.y)), sq) + 1e-3;

    float z = 1.0 / d - t;     // nested frames receding to the centre
    float idx = floor(z);
    float band = fract(z) - 0.5;
    float frame = exp(-band * band * 26.0);

    vec3 col = tintedPalette(idx * 0.07 + t * 0.03) * frame * glow * (0.4 + d * 0.9);
    col += tintedPalette(idx * 0.07 + 0.2) * 0.05;   // dim wall fill
    col += vec3(1.0, 0.95, 0.85) * (0.5 / (1.0 + d * d * 30.0));
    fragColor = vec4(finalize(col), 1.0);
}
