uniform float u_strokes;  // hint_range(2.0, 12.0, 1.0) = 4.0  strokes across the frame
uniform float u_feather;  // hint_range(0.02, 0.4, 0.01) = 0.12  taper at each stroke end
uniform float u_fill;     // hint_range(0.2, 1.0, 0.01) = 0.45  painted share of each pass
uniform float u_step;     // hint_range(0.0, 0.4, 0.01) = 0.2  hue step between neighbouring strokes

// Painted strokes, deliberately unlike sharplines' even hard bars: two
// overlapping layers of different period, each warped so spacing is irregular,
// every stroke with its own width, offset and hue, tapered ends, bristle
// streaks and uneven paint load. Strokes are wide so the layers overlap and
// gaps stay small, a dim wash fills what is left, and layers screen-blend so
// colors mix where strokes cross. Bristles are seeded in stroke space and
// drift with time, so the texture evolves as it pans.

// One layer: f is its travel coordinate in strokes, id decorrelates layers.
// Returns the painted color.
vec3 brushLayer(float f, float y, float id) {
    float feather = clamp(u_feather, 0.02, 0.4);
    float fill = clamp(u_fill, 0.2, 1.0);
    // Monotone warp (slope stays below one) so strokes bunch and spread.
    f += 0.6 * vnoise(vec2(f * 0.35 + id * 11.0, id));
    float cell = floor(f);
    float d = f - cell;
    float h1 = hash21(vec2(cell, id + 1.0));
    float h2 = hash21(vec2(cell + 17.0, id + 5.0));
    // Per-stroke width and start offset; the offset leaves uneven gaps.
    float w = clamp(0.5 + fill * (0.55 + 0.5 * h1), 0.5, 1.0);
    float start = h2 * (1.0 - w);
    float u = (d - start) / w;
    float seed = cell * 5.31 + id * 3.0;
    // Bristles run along the stroke and are seeded in stroke space, so they
    // travel with the paint.
    float drift = u_time * u_speed * 0.12;
    float bristle = vnoise(vec2(u * 2.5 + seed + drift, y * 28.0 + seed - drift * 1.4));
    float coarse = vnoise(vec2(u * 7.0 + seed * 1.7 - drift * 0.8, y * 12.0 + drift));
    // Ragged edges.
    float ue = u + (bristle - 0.5) * 0.18;
    float body = smoothstep(0.0, feather * 3.0 / w, ue) * smoothstep(1.0, 1.0 - feather / w, ue);
    // Dry brush: low bristles thin out toward the tail but never go black.
    float dry = pow(clamp(1.0 - u, 0.0, 1.0), 1.2);
    float load = mix(0.4, 1.0, smoothstep(dry * 0.6 - 0.15, dry * 0.6 + 0.15, mix(bristle, coarse, 0.4)));
    // Uneven paint load along the stroke.
    float uneven = 0.6 + 0.4 * vnoise(vec2(u * 3.0 + seed, id * 7.0 + h1 * 9.0));
    float hue = cell * u_step + id * 0.33 + u_hue + (h2 - 0.5) * 0.08;
    return hsv2rgb(vec3(hue, 0.95, 1.0)) * body * load * uneven;
}

void main() {
    vec2 uv = uv01();
    float t = u_time * u_speed * 0.7;
    float n = max(2.0, u_strokes);
    vec3 a = brushLayer(uv.x * n - t, uv.y, 0.0);
    vec3 b = brushLayer(uv.x * n * 0.62 - t * 0.62 + 3.3, uv.y, 1.0) * 0.8;
    vec3 col = a + b - a * b;
    vec3 wash = hsv2rgb(vec3(u_hue + uv.x * n * u_step - t, 0.9, 0.22));
    col = col + wash - col * wash;
    fragColor = vec4(finalize(col), 1.0);
}
