uniform float u_comets; // hint_range(1.0, 8.0, 1.0) = 3.0  comets across the frame
uniform float u_tail;   // hint_range(0.1, 1.0, 0.01) = 0.7  tail length
uniform float u_color;  // hint_range(0.0, 1.0, 0.01) = 0.09  streak colour on the hue wheel, the default is gold
uniform float u_head;   // hint_range(0.0, 1.0, 0.01) = 0.7  how white the leading edge burns

// Shine: gold-leaf streaks with a white-hot head and a wide bloom ahead of it.
// Everything is smooth at LED sample spacing: broad sine sheen crests roll
// through the body and low-frequency noise drifts slowly along y.
void main() {
    vec2 uv = uv01();
    // u_time counts seconds of the day, so time * speed is large and float rounding
    // quantizes the pan phase. Time wraps over one loop sized so the pan moves a whole
    // multiple of CELLS comet cells per loop, and every other time term is periodic over
    // that loop (whole sine cycles, a cross-faded noise window, cell identity mod CELLS),
    // so the wrap is invisible. Below a minimum speed the pan product stays small and
    // reads unwrapped time.
    const float CELLS = 8.0;
    const float TAU = 6.2831853;
    float sa = abs(u_speed);
    float loops = max(1.0, floor(600.0 * 1.8 * sa / CELLS + 0.5));
    float L = sa >= 0.02 ? CELLS * loops / (1.8 * sa) : 600.0;
    float tw = mod(u_time, L);
    float u = tw / L;
    float T = (sa >= 0.02 ? tw : u_time) * u_speed;
    // Texture drift and sheen evolve slower than the pan and grow only gently with speed.
    float rate = (0.5 + 0.5 * sa) * (u_speed < 0.0 ? -1.0 : 1.0);
    float cyA = TAU * fract(max(1.0, floor(L * abs(rate) * 0.8 / TAU + 0.5)) * u) * sign(rate);
    float cyB = TAU * fract(max(1.0, floor(L * abs(rate) * 1.2 / TAU + 0.5)) * u) * sign(rate);
    float f = uv.x * u_comets - T * 1.8 + 0.05 * sin(uv.y * 3.0 + cyA + uv.x * 2.0);
    float cell = floor(f);
    float d = f - cell;
    float behind = 1.0 - d;
    float tail = exp(-behind * (2.2 / clamp(u_tail, 0.1, 1.0)));
    float head = smoothstep(0.8, 1.0, d);
    float bloom = exp(-behind * behind * 40.0) * 0.55;
    float core = exp(-behind * behind * 120.0);
    float cid = mod(cell, CELLS);
    float span = 0.5 * rate * L;
    vec2 np = vec2(d * 2.5 + cid * 7.3, uv.y * 2.0 - span * u);
    float drift = mix(vnoise(np), vnoise(np + vec2(0.0, span)), u);
    float wave = 0.5 + 0.5 * sin(d * 4.0 + uv.y * 3.0 - cyB + cid * 1.7);
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

// Output gain applied after the tonemap so the brightest pixel reaches full scale.
float outGain() { return 1.30; }
