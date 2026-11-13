uniform float u_warp; // hint_range(0.0, 2.0, 0.05) = 1.0  domain-warp intensity
uniform float u_zoom; // hint_range(0.5, 3.0, 0.05) = 1.0  spatial frequency
void main() {
    vec2 uv = uvCentered() * max(0.1, u_zoom);
    float t = u_time * u_speed * 0.6;
    float w = max(0.0, u_warp);
    // Two-pass domain warp gives the plasma real depth.
    vec2 q = w * vec2(
        sin(uv.x * 1.8 + t * 0.9) + sin(uv.y * 1.3 - t * 0.5),
        cos(uv.y * 2.1 - t * 0.4) + cos(uv.x * 1.5 + t * 0.7));
    vec2 r = w * vec2(
        sin(q.x * 1.1 + uv.y * 2.2 + t * 0.3),
        cos(q.y * 1.3 - uv.x * 1.7 + t * 0.6));
    float v = sin(uv.x * 2.5 + r.x * 3.0 + t)
            + sin((uv.x + uv.y) * 2.0 + r.y * 2.5 + t * 0.8)
            + sin(length(uv + r * 0.4) * 4.0 + t * 0.5);
    v = v / 3.0 * 0.5 + 0.5;
    vec3 col = tintedPalette(v + t * 0.05);
    col *= 1.0 - 0.25 * smoothstep(0.8, 1.6, length(uv));
    // Palette contrast relaxed so channels sweep through their mid values, then the
    // bright hues (green, yellow) are lowered in value, not pulled toward gray.
    col = col * 0.88 + 0.06;
    float pl = dot(col, vec3(0.2126, 0.7152, 0.0722));
    col *= 1.0 - 0.12 * smoothstep(0.45, 0.8, pl);
    // Pull the colors slightly toward their own luma so a full-rainbow preset reads
    // as color rather than glare. Hue stays on the wheel.
    col = mix(vec3(dot(col, vec3(0.2126, 0.7152, 0.0722))), col, 0.9);
    fragColor = vec4(finalize(col * 1.15), 1.0);
}

float outGain() { return 1.4; }
