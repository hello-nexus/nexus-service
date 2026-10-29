uniform float u_comets; // hint_range(1.0, 8.0, 1.0) = 3.0  comets across the frame
uniform float u_tail;   // hint_range(0.1, 1.0, 0.01) = 0.7  tail length
uniform float u_color;  // hint_range(0.0, 1.0, 0.01) = 0.09  streak colour on the hue wheel, the default is gold
uniform float u_head;   // hint_range(0.0, 1.0, 0.01) = 0.7  how white the leading edge burns

// Shine: gold-leaf streaks with a white-hot head and a wide bloom ahead of it.
// Everything is smooth at LED sample spacing: broad sine sheen crests roll
// through the body and low-frequency noise drifts slowly along y.
void main() {
    vec2 uv = uv01();
    // u_time counts seconds of the day, so time * speed reaches 1e5 and float
    // rounding at that size quantizes the comet phase in steps that grow with
    // speed. Wrapping first keeps the product small; the pan repeats its
    // phase once per wrap.
    float tw = mod(u_time, 600.0);
    float T = tw * u_speed;
    // Texture drift and sheen evolve slower than the pan and grow only gently with speed.
    float Tn = tw * (0.5 + 0.5 * abs(u_speed)) * sign(u_speed + 1e-4);
    float f = uv.x * u_comets - T * 1.8 + 0.05 * sin(uv.y * 3.0 + Tn * 0.8 + uv.x * 2.0);
    float cell = floor(f);
    float d = f - cell;
    float behind = 1.0 - d;
    float tail = exp(-behind * (2.2 / clamp(u_tail, 0.1, 1.0)));
    float head = smoothstep(0.8, 1.0, d);
    float bloom = exp(-behind * behind * 40.0) * 0.55;
    float core = exp(-behind * behind * 120.0);
    float drift = vnoise(vec2(d * 2.5 + cell * 7.3, uv.y * 2.0 - Tn * 0.5));
    float wave = 0.5 + 0.5 * sin(d * 4.0 + uv.y * 3.0 - Tn * 1.2 + cell * 1.7);
    float crest = wave * wave * (3.0 - 2.0 * wave);
    float grain = (0.9 + 0.2 * drift) * mix(0.7, 1.2, crest);
    float glow = (tail * 0.5 + bloom) * crest * 0.5;
    vec3 base = hsv2rgb(vec3(u_color + u_hue, 0.95, 1.0));
    float hot = clamp(head * 0.6 + core, 0.0, 1.0) * u_head;
    vec3 body = mix(base, vec3(1.0), clamp(hot * 0.85 + glow * 0.4, 0.0, 1.0));
    // Dim wash the crests ride on, so the gaps between comets never go dark.
    float wash = (0.11 + 0.07 * crest) * (0.9 + 0.2 * drift);
    vec3 col = body * (tail * grain * 1.3 + bloom * 0.6 + glow) + base * wash + vec3(hot) * 0.8;
    fragColor = vec4(finalize(col), 1.0);
}
