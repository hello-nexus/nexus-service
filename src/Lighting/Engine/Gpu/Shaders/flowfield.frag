uniform float u_streams; // hint_range(0.5, 3.0, 0.05) = 1.5  stream count multiplier
uniform float u_flow; // hint_range(0.2, 3.0, 0.05) = 1.0  flow speed
uniform float u_colorSpread; // hint_range(0.0, 1.5, 0.05) = 0.6  color variance per stream
uniform float u_mood; // hint_range(0.0, 5.0, 1.0) = 0.0  palette mood: 0 full spectrum, 1 warm, 2 cool, 3 neon, 4 sunset, 5 aurora
#define tintedPalette(t) moodPalette((t), u_mood)

// Flow direction at a point: a smooth angle field that slowly evolves.
float fieldAngle(vec2 p, float t) {
    // The whole field also turns slowly, so stroke directions keep sweeping around.
    return 2.3 * sin(p.x * 1.25 + p.y * 0.8 + t * 1.2)
         + 1.9 * cos(p.y * 1.6 - p.x * 0.7 - t * 0.95)
         + 1.1 * sin((p.x + p.y) * 2.1 + t * 1.7)
         + t * 0.35;
}

// Streamlines made visible: each pixel averages fine grain along the field line
// that passes through it (a line-integral convolution), so the grain smears into
// long directional strokes that follow the field, and brightness pulses travel
// along each stroke in the direction of flow.
void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * u_flow * 0.5, 1000.0);
    // Gentle drift of the whole pattern.
    uv += vec2(sin(t * 0.35), cos(t * 0.28)) * 0.18;
    float streams = clamp(u_streams, 0.3, 4.0);
    float spread = clamp(u_colorSpread, 0.0, 2.0);

    // Low-frequency grain, never finer than a few pixels, so strokes stay soft and clean on small canvases.
    float grain = min(20.0 * streams, max(8.0, u_resolution.y * 0.12));
    float acc = 0.0;
    float accP = 0.0;
    float wsum = 0.0;
    float ang0 = fieldAngle(uv, t);
    vec2 pf = uv;
    vec2 pb = uv;
    float stepLen = 0.043;
    for (int i = 0; i < 4; i++) {
        float w = 1.0 - float(i) / 4.0;
        float fi = float(i);
        float af = fieldAngle(pf, t);
        float ab = fieldAngle(pb, t);
        pf += stepLen * vec2(cos(af), sin(af));
        pb -= stepLen * vec2(cos(ab), sin(ab));
        float grainF = vnoise(pf * grain);
        float grainB = vnoise(pb * grain);
        float pulseF = 0.55 + 0.45 * sin(-fi * 0.9 + t * 6.0 + grainF * 2.0);
        float pulseB = 0.55 + 0.45 * sin(fi * 0.9 + t * 6.0 + grainB * 2.0);
        acc += w * (grainF + grainB);
        accP += w * (pulseF * grainF + pulseB * grainB);
        wsum += 2.0 * w;
    }
    float stroke = smoothstep(0.0, 1.0, (acc / wsum - 0.5) * 4.6 + 0.5);
    float pulse = clamp(accP / max(acc, 1e-3), 0.3, 1.0);
    stroke *= 0.5 + 0.8 * pulse;

    vec3 body = tintedPalette(0.5 + ang0 / 12.0 + spread * (vnoise(uv * 2.0 + t * 0.1) - 0.5) + t * 0.03);
    vec3 col = body * (0.28 + 1.45 * stroke);
    col += tintedPalette(0.1 + ang0 / 12.0) * 0.1;
    fragColor = vec4(finalize(col), 1.0);
}

// Output gain applied after the tonemap so the brightest pixel reaches full scale.
float outGain() { return 1.40; }
