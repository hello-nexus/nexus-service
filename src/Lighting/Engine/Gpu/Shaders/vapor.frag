uniform float u_density; // hint_range(0.3, 2.0, 0.05) = 1.0  smoke density
uniform float u_scale; // hint_range(0.5, 3.0, 0.05) = 1.5  wisp scale
uniform float u_drift; // hint_range(0.2, 2.0, 0.05) = 1.0  rise rate

// Colors spread evenly across the color wheel range: hue is its middle and
// 1 - colorize its width, so a narrow range is one tone and a full one is the rainbow.
vec3 wheelColor(float k) {
    vec3 stops = u_palette[0] + u_palette[1] + u_palette[2] + u_palette[3];
    if (u_paletteMix > 0.5 && dot(stops, vec3(1.0)) > 0.0) {
        return artPalette(k);
    }
    float span = 1.0 - clamp(u_colorize, 0.0, 1.0);
    return hsv2rgb(vec3(u_hue + (clamp(k, 0.0, 1.0) - 0.5) * span, 1.0, 1.0));
}

// finalize() without its colorize blend: the wheel range alone decides hue.
vec3 finish(vec3 col) {
    float bl = dot(max(col, 0.0), vec3(0.2126, 0.7152, 0.0722));
    col = mix(vec3(bl), col, clamp(u_saturation, 0.0, 4.0));
    col = (col - 0.5) * clamp(u_contrast, 0.0, 4.0) + 0.5;
    return gainOut(tonemap(max(col, 0.0)));
}

// Drifting smoke wisps: a 3-octave fbm warped twice by slow billows, advected
// upward at a rate set by drift. The clock is wrapped because the shader clock
// grows all day and the noise hash loses its fraction at large coordinates,
// which flattened the whole frame to one color. The field is remapped to
// fbm3's actual band and floored so no slot reads as solid black.
void main() {
    float ar = u_resolution.x / u_resolution.y;
    vec2 uv = uv01();
    float t = mod(u_time * u_speed * 0.6, 600.0);
    float density = clamp(u_density, 0.2, 2.2);
    float scale = clamp(u_scale, 0.4, 3.5);
    float drift = clamp(u_drift, 0.1, 2.2);

    vec2 q = vec2(uv.x * ar, uv.y) * (1.6 + scale);
    // base billow animates at a fixed rate; rise/lateral advection scale with drift
    q += 0.5 * vec2(sin(q.y * 1.3 + t), cos(q.x * 1.1 - t * 0.8));
    q -= vec2(t * drift * 0.25, t * (0.4 + drift));
    // second warp: the wisps fold over themselves as they rise
    vec2 w = vec2(fbm3(q * 0.6 + vec2(0.0, t * 0.3)), fbm3(q * 0.6 + vec2(5.2, 1.3) - t * 0.2));
    q += (w - 0.45) * 2.2;

    // Opaque, contrasty smoke: a bright body, a darker undertone in the gaps, and hot
    // highlights on the thickest wisps, all colored from the wheel range.
    float n = smoothstep(0.28, 0.7, fbm3(q));
    float n2 = smoothstep(0.35, 0.7, fbm3(q * 1.9 + vec2(4.0, 1.0) - t * 0.2));
    float body = mix(0.25, 1.0, n) * (0.7 + 0.5 * density);
    float sway = 0.5 + 0.5 * sin(t * 0.25);
    vec3 under = wheelColor(0.7 + n2 * 0.25) * 0.45;
    vec3 wisp = wheelColor(n * 0.75 + 0.25 * sway) * body * 1.4;
    vec3 col = mix(under, wisp, smoothstep(0.1, 0.6, n));
    col += mix(wheelColor(0.25 + n2 * 0.5), vec3(1.0), 0.25) * n * n2 * 0.9;
    fragColor = vec4(finish(col), 1.0);
}

float outGain() { return 1.35; }
