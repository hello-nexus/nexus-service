uniform float u_turbulence; // hint_range(0.2, 2.0, 0.05) = 0.8  warp intensity
uniform float u_direction; // hint_range(-3.14, 3.14, 0.05) = 0.0  flow direction angle
uniform float u_bite; // hint_range(0.3, 2.5, 0.05) = 1.0  contrast curve (renamed to avoid u_contrast clash)

// Inigo Quilez recursive domain warp. Two fbm passes displace the sample
// coordinate before the final fbm read, producing the smoky, continent-like
// macrostructure that domain warping is famous for.
void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * 0.95, 1000.0);
    float turb = max(0.2, u_turbulence);
    float dir = u_direction;
    float bite = max(0.3, u_bite);

    vec2 p = uv * 1.4;
    vec2 flow = vec2(cos(dir), sin(dir)) * 0.3;

    // perf: both warp passes feed displaced coords whose top octaves the warp
    // washes out -> fbm3 (3 oct). Final read stays fbm for macro detail.
    vec2 q = vec2(
        fbm3(p + vec2(t * 0.2, 0.0) + flow),
        fbm3(p + vec2(5.2, 1.3) - flow)
    );
    vec2 r = vec2(
        fbm3(p + turb * q + vec2(1.7, 9.2) + flow * 2.0 + t * 0.15),
        fbm3(p + turb * q + vec2(8.3, 2.8) - flow * 2.0 + t * 0.12)
    );
    // perf: after a double warp the high octaves are smeared; fbm3 reads nearly
    // identical here and drops the per-pixel cost another 2 octaves.
    float n = fbm3(p + turb * r) + 0.12;
    n = pow(clamp(n, 0.0, 1.0), bite);

    vec3 col = tintedPalette(n * 0.6 + 0.05);
    col *= 0.3 + 1.2 * smoothstep(0.1, 0.9, n);

    fragColor = vec4(finalize(col), 1.0);
}
