uniform float u_folds; // hint_range(2.0, 12.0, 1.0) = 6.0  fold count
uniform float u_flow; // hint_range(0.0, 2.0, 0.05) = 1.2  drift speed
uniform float u_sheen; // hint_range(0.0, 2.0, 0.05) = 1.0  specular highlight

void main() {
    vec2 uv = uvCentered();
    // Base speed multiplier raised so the cloth actually flows at default
    // speed=50 - the old value (0.35) read as near-static until the user
    // pushed the slider hard.
    float t = mod(u_time * u_speed * 0.9, 1000.0);
    float folds = clamp(u_folds, 1.0, 14.0);
    float flow = clamp(u_flow, 0.0, 2.5);
    float sheen = clamp(u_sheen, 0.0, 2.5);

    // Cloth "height field" is a sum of sinusoidal folds travelling at
    // different angles + a domain-warped noise layer for organic ripples.
    // Normal-like direction is estimated via derivatives of the height field
    // so specular highlights track the folds.
    vec2 p = uv;
    float tw = t * flow;

    // Height field = sine folds + a noise layer. The normal needs the field's
    // gradient; accumulate the sine sum's analytic gradient in-loop
    // (d/dp[A sin(dot(p,d)f + ph)] = A f cos(.) d) instead of re-evaluating the
    // whole field at four offset points (the old finite-diff ran 5 passes).
    float h = 0.0;
    vec2 gradH = vec2(0.0);
    for (int i = 0; i < 14; i++) {
        float fi = float(i);
        if (fi >= folds) break;
        float ang = fi * 0.7 + sin(t * 0.15 + fi) * 0.3;
        vec2 d = vec2(cos(ang), sin(ang));
        float freq = 1.5 + fi * 0.6;
        float phase = fi * 0.9 + tw * (0.4 + fi * 0.08);
        float amp = 1.0 / (1.0 + fi * 0.6);
        float arg = dot(p, d) * freq + phase;
        h += sin(arg) * amp;
        gradH += amp * freq * cos(arg) * d;
    }
    // Noise micro-texture has no closed-form slope: forward-difference it from
    // two extra taps, reusing the centre sample.
    // perf: 3 finite-diff noise taps were 5-octave fbm (12 vnoise); the micro
    // texture is a subtle ripple -> fbm3 (3 octaves) keeps the look, ~40% off each.
    float eps = 0.003;
    float nC = fbm3(p * 2.0 + tw * 0.1);
    h += (nC - 0.5) * 0.5;
    float nX = fbm3((p + vec2(eps, 0.0)) * 2.0 + tw * 0.1);
    float nY = fbm3((p + vec2(0.0, eps)) * 2.0 + tw * 0.1);
    gradH += vec2(nX - nC, nY - nC) / eps * 0.5;

    vec3 n = normalize(vec3(-gradH, 1.0));

    // Light direction rotates slowly so the silk catches the light.
    vec3 L = normalize(vec3(sin(t * 0.3) * 0.7, cos(t * 0.25) * 0.7, 0.6));
    float diff = max(0.0, dot(n, L));
    float spec = pow(max(0.0, dot(n, normalize(L + vec3(0.0, 0.0, 1.0)))), 28.0);

    // Color: base tinted palette shifted by the fold height.
    vec3 base = tintedPalette(h * 0.2 + 0.1);
    vec3 col = base * (0.4 + 0.6 * diff);
    col += vec3(1.0, 0.95, 0.9) * spec * sheen * 0.7;
    // Rim light along fold peaks for satin sheen.
    float rim = smoothstep(0.7, 1.0, 1.0 - n.z) * sheen;
    col += tintedPalette(h * 0.2 + 0.4) * rim * 0.3;

    fragColor = vec4(finalize(col), 1.0);
}
