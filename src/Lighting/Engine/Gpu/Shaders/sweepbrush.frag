uniform float u_strokes;  // hint_range(2.0, 12.0, 1.0) = 4.0  strokes across the frame
uniform float u_feather;  // hint_range(0.02, 0.4, 0.01) = 0.12  taper at each stroke end
uniform float u_fill;     // hint_range(0.2, 1.0, 0.01) = 0.45  painted share of each pass
uniform float u_step;     // hint_range(0.0, 0.4, 0.01) = 0.2  hue step between neighbouring strokes

// Strokes painted in place, not scrolled: each slot's brush head sweeps
// forward and deposits paint behind it, the loaded head is the brightest and
// most saturated part, the paint thins into bristle streaks toward the tail,
// then the stroke dries out while neighbours are mid-stroke (clocks staggered
// per slot). Silk-style folds bend the strokes along y.
void main() {
    vec2 uv = uv01();
    // Wrapped first: seconds of the day times speed is large enough that float
    // rounding quantizes the phase, more so at high speed.
    float t = mod(u_time, 600.0) * u_speed * 0.7;
    float strokes = max(1.0, u_strokes);
    float feather = clamp(u_feather, 0.02, 0.4);
    // Stroke length in slot widths; above one cell the tail overlaps the next
    // slot's stroke.
    float len = clamp(u_fill, 0.2, 1.0) * 1.5;

    float fold = 0.5 * sin(uv.y * 5.0 + uv.x * 1.5 + t * 0.9)
               + 0.3 * sin(uv.y * 9.0 - uv.x * 0.8 - t * 0.65 + 0.9)
               + 0.2 * sin(uv.y * 3.0 + uv.x * 0.5 + t * 0.4 + 2.1);
    float crest = fold * 0.5 + 0.5;

    float xs = uv.x * strokes + 0.25 * fold;
    float base = floor(xs);
    vec3 col = vec3(0.0);
    // A stroke reaches at most two slots, so the slot and its left neighbour.
    for (int k = 0; k < 2; k++) {
        float idx = base - float(k);
        float h1 = fract(sin(idx * 12.9898 + 78.233) * 43758.5453);
        float h2 = fract(sin(idx * 39.346 + 11.135) * 24634.6345);
        // Golden-ratio stagger keeps neighbours in different stages.
        float c = t * 0.3 + idx * 0.382 + h2 * 0.15;
        float cyc = floor(c);
        float ph = c - cyc;
        // Painting happens in the first share of the life, then it holds and dries.
        float g = clamp(ph / 0.55, 0.0, 1.0);
        float head = len * (1.0 - (1.0 - g) * (1.0 - g));
        float a = xs - (idx + 0.25 * h1);
        float dry = 1.0 - smoothstep(0.7, 1.0, ph);
        // Soft rounded head, tapered start.
        float edge = smoothstep(0.0, feather + 0.1, a) * smoothstep(head, head - feather - 0.1, a);
        float behind = clamp((head - a) / len, 0.0, 1.0);
        // Loaded head: bright and saturated, decaying quickly behind it.
        float load = exp(-4.0 * behind);
        // Bristle streaks along y; along-stroke term makes strips break up too.
        float bristle = vnoise(vec2(uv.y * 7.0 + h1 * 40.0, a * 2.5 + h2 * 20.0));
        float thin = smoothstep(0.0, 1.0, behind);
        float coat = mix(1.0, smoothstep(0.15, 0.6, bristle + 0.35 * (1.0 - thin)), thin);
        float v = (0.4 + 0.6 * load) * coat * dry;
        float sat = 0.75 + 0.25 * load;
        vec3 c0 = hsv2rgb(vec3((idx + 3.0 * cyc) * u_step + u_hue + 0.008 * fold, sat, 1.0)) * v * edge;
        c0 += vec3(0.25) * load * edge * dry;
        col = max(col, c0);
    }
    col *= 0.6 + 0.4 * crest;
    fragColor = vec4(finalize(col), 1.0);
}
