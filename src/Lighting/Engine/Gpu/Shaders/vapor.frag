uniform float u_density; // hint_range(0.3, 2.0, 0.05) = 1.0  smoke density
uniform float u_scale; // hint_range(0.5, 3.0, 0.05) = 1.5  wisp scale
uniform float u_drift; // hint_range(0.2, 2.0, 0.05) = 1.0  rise rate
uniform float u_mood; // hint_range(0.0, 5.0, 1.0) = 0.0  palette mood: 0 full spectrum, 1 warm, 2 cool, 3 neon, 4 sunset, 5 aurora
#define tintedPalette(t) moodPalette((t), u_mood)

// Drifting smoke/vapour wisps. A 3-octave fbm warped by an always-animated
// billow (so motion is visible even at low drift), advected upward at a rate
// set by drift. The field is remapped to fbm3's actual band and floored so no
// slot reads as solid black.
void main() {
    vec2 uv = uv01();
    float t = u_time * u_speed * 0.6;
    float density = clamp(u_density, 0.2, 2.2);
    float scale = clamp(u_scale, 0.4, 3.5);
    float drift = clamp(u_drift, 0.1, 2.2);

    vec2 q = uv * (1.6 + scale);
    // base billow animates at a fixed rate; rise/lateral advection scale with drift
    q += 0.5 * vec2(sin(q.y * 1.3 + t), cos(q.x * 1.1 - t * 0.8));
    q -= vec2(t * drift * 0.25, t * (0.4 + drift));

    // Opaque, contrasty smoke: a bright body, a darker undertone in the gaps, and hot
    // highlights on the thickest wisps, all colored.
    float n = smoothstep(0.3, 0.72, fbm3(q));
    float n2 = smoothstep(0.35, 0.7, fbm3(q * 1.9 + vec2(4.0, 1.0) - t * 0.2));
    float body = mix(0.25, 1.0, n) * (0.7 + 0.5 * density);
    vec3 under = tintedPalette(0.7 + n2 * 0.25 + t * 0.03) * 0.45;
    vec3 wisp = tintedPalette(n * 0.9 + t * 0.03) * body * 1.4;
    vec3 col = mix(under, wisp, smoothstep(0.1, 0.6, n));
    col += tintedPalette(0.25 + n2 * 0.3) * n * n2 * 0.9;
    fragColor = vec4(finalize(col), 1.0);
}
