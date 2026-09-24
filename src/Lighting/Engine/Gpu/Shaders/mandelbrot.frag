uniform float u_depth; // hint_range(2.0, 6.0, 1.0) = 4.0  iteration count scaling
uniform float u_rotation; // hint_range(0.05, 2.0, 0.05) = 0.5  zoom rate multiplier
uniform float u_brightness; // hint_range(0.3, 2.0, 0.05) = 1.0  brightness

// Real Mandelbrot zoom with a cosine-driven breathing depth so the loop
// is smooth with no snap and no wrap. Interior points get their own
// palette sample instead of pure black, so the set silhouette still has
// texture at deep zoom. z = z*z + c is the only iteration - no overlays,
// no blended layers.
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed;
    // perf: escape-iteration count is the whole cost; cap ~2.7x lower
    // (was *40 / 50..280). Smooth coloring below hides the shallower bands.
    int maxIter = int(clamp(u_depth * 15.0, 24.0, 104.0));
    float bright = clamp(u_brightness, 0.0, 3.0);
    float rate = max(u_rotation, 0.05);

    // Cosine ramp of zoom depth: 0 -> max -> 0 -> max ... all smooth.
    // Max depth stays below float32's precision floor so the interior
    // never goes fully black mid-zoom.
    float zoomT = 6.5 * (1.0 - cos(t * rate * 0.25));
    float scale = 1.8 * exp(-zoomT);

    vec2 c = vec2(-0.743643887037151, 0.131825904205330) + uv * scale;
    vec2 z = vec2(0.0);
    float iter = 0.0;
    bool escaped = false;
    // perf: hard loop bound lowered to match the new cap (280 -> 104)
    for (int i = 0; i < 104; i++) {
        if (i >= maxIter) break;
        z = vec2(z.x*z.x - z.y*z.y, 2.0*z.x*z.y) + c;
        float r2 = dot(z, z);
        if (r2 > 256.0) {
            iter = float(i) + 1.0 - log2(log2(r2) * 0.5);
            escaped = true;
            break;
        }
    }

    vec3 col;
    if (escaped) {
        col = tintedPalette(iter * 0.025 - t * 0.03) * bright;
    } else {
        // Interior shading via last z magnitude so the set silhouette
        // isn't a flat black hole.
        col = tintedPalette(dot(z, z) * 0.08 + t * 0.05) * bright * 0.2;
    }
    fragColor = vec4(finalize(col), 1.0);
}
