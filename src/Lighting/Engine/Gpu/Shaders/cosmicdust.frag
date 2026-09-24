uniform float u_particles; // hint_range(0.3, 3.0, 0.05) = 1.8  density multiplier
uniform float u_twinkle; // hint_range(0.2, 4.0, 0.05) = 1.5  flicker rate
uniform float u_parallax; // hint_range(0.0, 1.2, 0.02) = 0.6  depth drift strength

// Sparse particle field with three depth layers drifting at different
// rates (parallax). Each grid cell has a small chance of hosting a star,
// and each star twinkles on its own phase.
void main() {
    vec2 uv = uv01();
    float t = u_time * u_speed * 0.05;
    float dens = clamp(u_particles, 0.3, 3.0);
    float twk = max(0.2, u_twinkle);
    float plx = clamp(u_parallax, 0.0, 1.2);

    vec3 col = vec3(0.04, 0.03, 0.07);
    for (int i = 0; i < 3; i++) {
        float layerZ = float(i) / 3.0;
        float drift = mix(1.0, 0.2, layerZ) * plx;
        vec2 q = uv * (42.0 + layerZ * 28.0) * dens;
        q.x += t * drift * 5.5;
        q.y += t * drift * 2.2;
        vec2 cell = floor(q);
        vec2 gv = fract(q) - 0.5;
        float rnd = hash21(cell);
        float hasStar = step(0.70, rnd);
        float starD = length(gv);
        float phase = fract(t * twk + rnd * 7.3);
        float tw = 0.4 + 0.6 * sin(phase * 6.28318);
        float star = exp(-starD * starD * (90.0 - 30.0 * tw)) * hasStar;
        float br = mix(0.55, 1.0, 1.0 - layerZ);
        col += tintedPalette(0.1 + rnd * 0.45 + t * 0.04) * star * br * tw * 2.0;
    }

    fragColor = vec4(finalize(col), 1.0);
}
