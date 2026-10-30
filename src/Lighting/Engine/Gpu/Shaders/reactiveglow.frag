uniform vec3 u_bands[16];
uniform int u_bandCount;

void main() {
    vec2 uv = uv01();
    int n = clamp(u_bandCount, 1, 16);

    // Band b owns the screen column centred at (b+0.5)/n, so offset by half a
    // band before sampling; without it the whole glow sits half a band left.
    float fi = uv.x * float(n) - 0.5;
    float lof = floor(fi);
    int lo = clamp(int(lof), 0, n - 1);
    int hi = clamp(int(lof) + 1, 0, n - 1);
    float t = clamp(fi - lof, 0.0, 1.0);
    // Smoothstep for soft glow blending between adjacent bands
    float st = smoothstep(0.0, 1.0, t);
    vec3 col = mix(u_bands[lo], u_bands[hi], st);

    // Vertical falloff: brightest at the centre, floored so the edges stay lit.
    float vy = 1.0 - abs(uv.y - 0.5);
    vy = mix(0.7, 1.0, vy * vy);
    col *= vy * 1.15;

    fragColor = vec4(finalize(col), 1.0);
}

float outGain() { return 1.00; }
