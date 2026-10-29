uniform float u_comets; // hint_range(1.0, 8.0, 1.0) = 3.0  comets across the frame
uniform float u_tail;   // hint_range(0.1, 1.0, 0.01) = 0.7  tail length
uniform float u_color;  // hint_range(0.0, 1.0, 0.01) = 0.09  streak colour on the hue wheel, the default is gold
uniform float u_head;   // hint_range(0.0, 1.0, 0.01) = 0.7  how white the leading edge burns

// Shine: coloured streaks with white-hot heads, a bloom halo ahead of the
// head and glints along the tail.
void main() {
    vec2 uv = uv01();
    float f = uv.x * u_comets - u_time * u_speed * 1.8;
    float cell = floor(f);
    float d = f - cell;
    // Head sits at the cell's leading edge; the tail decays back from it.
    float behind = 1.0 - d;
    float tail = exp(-behind * (3.0 / clamp(u_tail, 0.1, 1.0)));
    float head = smoothstep(0.94, 1.0, d);
    // Wide soft halo around the head, and a tighter hot core.
    float bloom = exp(-behind * behind * 90.0) * 0.55;
    float core = exp(-behind * behind * 1800.0);
    // Glints: time-stable twinkle riding the tail (f moves with the comet),
    // gated to the brighter part of the tail.
    float tw = vnoise(vec2(d * 22.0 + cell * 7.3, u_time * u_speed * 2.0));
    float glint = smoothstep(0.72, 0.95, tw) * tail * smoothstep(0.15, 0.6, d);
    vec3 base = hsv2rgb(vec3(u_color + u_hue, 0.95, 1.0));
    float hot = clamp(head + core, 0.0, 1.0) * u_head;
    // The head desaturates toward white as it brightens.
    vec3 body = mix(base, vec3(1.0), clamp(hot * 0.85 + glint * 0.5, 0.0, 1.0));
    vec3 col = body * (tail * 1.3 + bloom * 0.6 + glint * 0.9) + vec3(hot) * 0.8;
    fragColor = vec4(finalize(col), 1.0);
}
