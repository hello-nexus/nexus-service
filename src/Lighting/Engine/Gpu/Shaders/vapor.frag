uniform float u_density; // hint_range(0.3, 2.0, 0.05) = 1.0  smoke density
uniform float u_scale; // hint_range(0.5, 3.0, 0.05) = 1.5  wisp scale
uniform float u_drift; // hint_range(0.2, 2.0, 0.05) = 1.0  rise rate

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

    // fbm3 sits ~0.32..0.72; stretch that to the full 0..1 so the field isn't
    // mostly dark, then keep a visible floor so thin areas still show smoke.
    float n = smoothstep(0.32, 0.72, fbm3(q));
    float smoke = mix(0.12, 1.0, n) * (0.5 + 0.5 * density);

    vec3 col = tintedPalette(n * 0.4 + t * 0.03) * smoke;
    fragColor = vec4(finalize(col), 1.0);
}
