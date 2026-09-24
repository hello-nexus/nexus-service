uniform float u_ringRate; // hint_range(0.05, 1.0, 0.02) = 0.25  expanding ring rate
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.7;
    float r = length(uv);
    float a = atan(uv.y, uv.x);
    float sweepAng = mod(t, 6.28318);
    float delta = mod(sweepAng - a, 6.28318);
    float sweep = pow(1.0 - delta / 6.28318, 6.0);
    float ringMask = smoothstep(1.0, 0.0, r);
    vec3 tint = tintedPalette(0.33); // defaults to green when u_hue=0
    vec3 col = tint * sweep * ringMask;
    float ringR = fract(t * max(0.05, u_ringRate));
    col += tint * 0.5 * smoothstep(0.02, 0.0, abs(r - ringR)) * (1.0 - ringR);
    fragColor = vec4(finalize(col), 1.0);
}
