uniform float u_size; // hint_range(0.03, 0.25, 0.005) = 0.08  ball radius
uniform float u_count; // hint_range(1.0, 24.0, 1.0) = 8.0  ball count
uniform float u_trail; // hint_range(0.0, 1.0, 0.02) = 0.6  shockwave length
uniform float u_mood; // hint_range(0.0, 5.0, 1.0) = 0.0  palette mood: 0 full spectrum, 1 warm, 2 cool, 3 neon, 4 sunset, 5 aurora
#define tintedPalette(t) moodPalette((t), u_mood)

// Glowing orbs bounce on a floor along decaying parabolic arcs, squash on
// impact and fire a flash plus an expanding ripple along the floor. Orbs that
// drift together merge like liquid light inside one soft bloom.
const float E = 0.6;
const float LOGE = -0.5108256;
const float ARCS = 2.176;
const float CYCLE = 3.1;
const float FLOOR = 0.85;

float ripple(float dx, float v, float age, float amp, float life) {
    float a = age / life;
    if (age < 0.0 || a >= 1.0) {
        return 0.0;
    }
    float r = (0.05 + 0.62 * a) * (0.5 + amp * 0.8);
    float e = length(vec2(dx, v * 3.5));
    float w = 0.06 + 0.06 * a;
    float ring = exp(-pow((e - r) / w, 2.0)) * (1.0 - a) * (1.0 - a);
    return ring * amp * smoothstep(-0.08, 0.0, v) * exp(-max(v, 0.0) * 3.0);
}

void main() {
    vec2 uv = uvCentered();
    float ar = u_resolution.x / u_resolution.y;
    float t = u_time * u_speed;
    float rad = clamp(u_size, 0.02, 0.4) * 3.0;
    int count = int(clamp(u_count, 1.0, 24.0));
    float life = 0.5 + clamp(u_trail, 0.0, 1.0) * 0.9;
    float v = FLOOR - uv.y;

    vec3 col = tintedPalette(0.1 + uv.y * 0.2 + sin(t * 0.2 + uv.x) * 0.1) * 0.10;
    col += tintedPalette(0.55 + uv.x * 0.12 - t * 0.02) * 0.05;
    col += tintedPalette(0.3 + t * 0.02) * 0.10 * exp(-abs(v) * 40.0);

    float field = 0.0;
    vec3 fieldCol = vec3(0.0);
    vec3 halo = vec3(0.0);
    bool nearFloor = v > -0.1 && v < 0.35;

    for (int i = 0; i < 24; i++) {
        if (i >= count) break;
        float fi = float(i);
        float orbR = rad * (0.8 + fract(fi * 0.37) * 0.4);
        float unit = 0.8 * (0.8 + fract(fi * 0.173) * 0.5);
        float tt = t / (CYCLE * unit) + fi * 0.371;
        float n = floor(tt);
        float s = fract(tt) * CYCLE;
        float hr = hash21(vec2(fi, n));
        float lane = ((fi + 0.5) / float(count) * 2.0 - 1.0) * ar * 0.92;
        float cx = lane + sin(t * 0.25 + fi * 1.7) * 0.3 + (hr - 0.5) * 0.3;
        float apex = max(0.2, (1.5 - orbR) * (0.7 + 0.3 * fract(hr * 7.0)));

        float sc = min(s, ARCS - 0.0001);
        float kf = floor(log(max(1.0 - sc * (1.0 - E), 0.0001)) / LOGE);
        float ek = exp(LOGE * kf);
        float sk = (1.0 - ek) / (1.0 - E);
        float u = clamp((sc - sk) / ek, 0.0, 1.0);
        float h = apex * ek * ek * 4.0 * u * (1.0 - u);
        float sq = smoothstep(0.2, 0.0, h) * (0.2 + 0.4 * ek);
        vec2 axes = vec2(1.0 + 0.6 * sq, 1.0 - sq);
        vec2 c = vec2(cx, FLOOR - orbR * axes.y - h);
        float env = smoothstep(0.0, 0.12, s) * (1.0 - smoothstep(ARCS, ARCS + 0.6, s));

        float dx = uv.x - cx;
        vec2 q = (uv - c) / (orbR * axes);
        float dd = dot(q, q);
        bool nearRipple = nearFloor && abs(dx) < 0.95 && s > 0.0;
        bool nearOrb = dd < 16.0 && env > 0.001;
        if (!nearRipple && !nearOrb) {
            continue;
        }
        vec3 tint = tintedPalette(fi * 0.137 + t * 0.03);

        if (nearRipple && kf > 0.5) {
            float rp = ripple(dx, v, s - sk, ek, life);
            float fl = ek * exp(-(s - sk) * 7.0) * exp(-(dx * dx + v * v * 4.0) / 0.03);
            if (kf > 1.5) {
                float ekp = ek / E;
                rp += ripple(dx, v, s - (sk - ekp), ekp, life);
            }
            col += tint * rp * 0.9 + mix(tint, vec3(1.0), 0.6) * fl * 1.2;
        }
        if (nearOrb) {
            float w = max(0.0, 1.0 - dd / 3.0);
            w = w * w * env;
            field += w;
            fieldCol += tint * w;
            halo += tint * exp(-dd * 0.5) * 0.35 * env;
        }
    }

    vec3 base = fieldCol / max(field, 0.0001);
    float body = smoothstep(0.35, 0.6, field);
    float core = smoothstep(0.7, 1.6, field);
    col += halo;
    col = mix(col, base * 0.95 + vec3(core * 0.45), body);
    fragColor = vec4(finalize(col * 1.15), 1.0);
}
