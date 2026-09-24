uniform float u_streams; // hint_range(0.5, 3.0, 0.05) = 1.5  stream count multiplier
uniform float u_flow; // hint_range(0.2, 3.0, 0.05) = 1.0  flow speed
uniform float u_colorSpread; // hint_range(0.0, 1.5, 0.05) = 0.6  color variance per stream

// Curl-noise particle streams flowing across the entire frame, like
// wind made visible. Stateless: each pixel evaluates the noise field
// at its position and renders trails behind imaginary particles seeded
// by the noise itself.
void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * u_flow * 0.4, 1000.0);
    float streams = clamp(u_streams, 0.3, 4.0);
    float spread = clamp(u_colorSpread, 0.0, 2.0);

    // Curl-noise approximation: gradient of fbm rotated 90 degrees.
    // perf: the flow direction is set by the low frequencies; high octaves in a
    // finite-difference gradient are just jitter -> fbm3 (3 oct) on all 4 taps.
    float eps = 0.04;
    float fY1 = fbm3(uv * 1.6 + vec2(0.0, eps) + t * 0.3);
    float fY0 = fbm3(uv * 1.6 + vec2(0.0, -eps) + t * 0.3);
    float fX1 = fbm3(uv * 1.6 + vec2(eps, 0.0) + t * 0.3);
    float fX0 = fbm3(uv * 1.6 + vec2(-eps, 0.0) + t * 0.3);
    vec2 grad = vec2((fY1 - fY0), -(fX1 - fX0));
    vec2 flowDir = normalize(grad + vec2(0.001));

    // "Particle" position: backtrace this pixel along the flow to find
    // where a particle would have come from.
    float trailLen = 0.18 * streams;
    vec2 traceFrom = uv - flowDir * trailLen;
    // Density of particles: fbm sampled at the trace origin.
    float densityNoise = fbm(traceFrom * 4.0 + t * 0.1);
    float density = pow(densityNoise, 1.5) * streams;

    // Color from local field angle.
    float angle = atan(flowDir.y, flowDir.x);
    vec3 col = tintedPalette(0.5 + angle / 6.28318 + spread * densityNoise + t * 0.05);

    // Brightness: glow concentrates where stream density is high.
    float bright = pow(density, 1.4) * 1.6;
    col *= bright * 1.2;
    // Faint background tint so the gaps aren't black.
    col += tintedPalette(0.65) * 0.06;

    fragColor = vec4(finalize(col), 1.0);
}
