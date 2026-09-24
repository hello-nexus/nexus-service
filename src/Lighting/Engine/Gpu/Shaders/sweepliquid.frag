uniform float u_warp;  // hint_range(0.0, 2.0, 0.05) = 1.0  how far the spectrum folds and swirls
uniform float u_scale; // hint_range(0.5, 3.0, 0.05) = 1.6  spectrum cycles across the frame

// Liquid rainbow: the full spectrum as a flowing plasma, with dark glossy seams
// between the colour bands so it reads as poured satin rather than a gradient.
// The field drifts along +x and every warp runs on the same clock, so the speed
// sign reverses everything at once.
void main() {
    vec2 uv = uv01();
    float ar = u_resolution.x / u_resolution.y;
    vec2 p = vec2(uv.x * ar, uv.y);
    float t = u_time * u_speed * 0.6;
    // Four-term plasma: two axis waves, a diagonal, and a ring around a
    // wandering centre. Summed they fold the bands without pooling into one hue.
    vec2 c = vec2(ar * 0.5 + 0.4 * sin(t * 0.7), 0.5 + 0.3 * cos(t * 0.5));
    float pl = sin(p.x * 3.1 + t * 1.3) + sin(p.y * 4.3 - t * 1.7)
             + sin((p.x + p.y) * 2.6 + t * 0.9) + sin(length(p - c) * 6.0 - t * 2.0);
    float f = uv.x * u_scale - t * 0.6 + u_warp * 0.12 * pl;
    float s = sin(f * 25.13 + t * 3.0);
    // The seam floor stays well above black: where the plasma flattens, one
    // gloss value fills a wide patch, and a dark one reads as a dead blob.
    float gloss = 0.4 + 0.6 * smoothstep(-0.6, 0.9, s);
    vec3 col = hsv2rgb(vec3(f + u_hue, 1.0, gloss));
    fragColor = vec4(finalize(col), 1.0);
}
