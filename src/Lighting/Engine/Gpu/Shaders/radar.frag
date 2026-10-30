uniform float u_ringRate; // hint_range(0.05, 1.0, 0.02) = 0.25  expanding ring rate
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.7;
    float r = length(uv);
    float a = atan(uv.y, uv.x);
    float sweepAng = mod(t, 6.28318);
    float delta = mod(sweepAng - a, 6.28318);
    // Wide trail that fades along the whole turn, reaching the frame corners.
    float sweep = pow(1.0 - delta / 6.28318, 2.2);
    float ringMask = smoothstep(2.3, 0.0, r);
    vec3 tint = tintedPalette(0.33); // defaults to green when u_hue=0
    vec3 col = tint * (sweep * 1.5 + 0.12) * ringMask;
    float rate = max(0.05, u_ringRate);
    for (int i = 0; i < 3; i++) {
        float ringR = fract(t * rate + float(i) / 3.0) * 2.3;
        col += tint * 0.9 * smoothstep(0.03, 0.0, abs(r - ringR)) * (1.0 - ringR / 2.3);
    }
    // Fixed range rings and a bright leading edge.
    col += tint * 0.35 * smoothstep(0.015, 0.0, abs(fract(r * 2.0 + 0.5) - 0.5)) * ringMask;
    col += tint * smoothstep(0.06, 0.0, delta) * ringMask * 1.2;
    fragColor = vec4(finalize(col), 1.0);
}

// Output gain applied after the tonemap so the brightest pixel reaches full scale.
float outGain() { return 1.27; }
