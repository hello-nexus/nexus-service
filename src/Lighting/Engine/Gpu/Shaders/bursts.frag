uniform float u_rate; // hint_range(0.2, 6.0, 0.1) = 1.8  bursts per second
uniform float u_particles; // hint_range(3.0, 20.0, 1.0) = 14.0  particles per burst
uniform float u_size; // hint_range(0.2, 2.0, 0.05) = 1.1  burst radius

void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.5;
    float rate = clamp(u_rate, 0.2, 6.0);
    int parts = int(clamp(u_particles, 3.0, 10.0)); // perf: particle cap 20->10
    float sz = clamp(u_size, 0.15, 2.5);
    vec3 col = vec3(0.012, 0.008, 0.022);

    // A few big concurrent bursts, staggered so the screen pops continuously
    // rather than in sync. Each slot maintains its own burst index over time.
    for (int b = 0; b < 3; b++) { // perf: few big bursts (3 slots)
        float fb = float(b);
        float period = 1.0 / rate;
        float phaseOffset = fract(fb * 0.173) * period;
        float rawPhase = t + phaseOffset;
        float burstId = floor(rawPhase / period);
        float life = fract(rawPhase / period); // 0..1 over a single burst

        vec2 burstPos = vec2(
            hash21(vec2(burstId, fb * 17.0 + 3.0)),
            hash21(vec2(burstId, fb * 31.0 + 11.0))
        ) * 2.4 - 1.2;
        float hueIdx = hash21(vec2(burstId, fb * 53.0 + 7.0));
        vec3 tint = tintedPalette(hueIdx);
        // fill: only 3 bursts -> make each much larger to fill the frame
        float thisSize = sz * (1.8 + hash21(vec2(burstId, fb * 41.0)) * 0.9);

        // White-hot flash at the moment of detonation.
        // fill: wide flash core
        float flash = smoothstep(0.22, 0.0, life) * exp(-length(uv - burstPos) * 9.0);
        col += mix(vec3(1.0), tint, 0.3) * flash * 5.2;

        // Early-out: pixels far from this burst's influence can skip the
        // entire particle loop. Reach scales with the (now larger) radial
        // spread plus the wider particle falloff.
        if (length(uv - burstPos) > thisSize * 1.7 + 0.3) {
            continue;
        }

        // Particles streak outward on their own angles.
        for (int p = 0; p < 10; p++) { // perf: 20->10 particle bound
            if (p >= parts) break;
            float fp = float(p);
            float angle = (fp + 0.5) / float(parts) * 6.28318
                        + hash21(vec2(burstId, fp * 13.0)) * 0.9;
            vec2 dir = vec2(cos(angle), sin(angle));
            // fill: wide fan + big particles fill the screen with few bursts
            float spread = 1.4 + hash21(vec2(burstId, fp * 19.0)) * 0.8;
            // Ease-out radial expansion with slight gravity droop.
            float radial = thisSize * spread * (1.0 - pow(1.0 - life, 1.8));
            vec2 ppos = burstPos + dir * radial + vec2(0.0, life * life * 0.2);
            float pd = length(uv - ppos);
            // fill: much bigger particle blobs (falloff 37->18)
            float particle = exp(-pd * 18.0);
            // Trailing line from the burst centre back to the particle.
            vec2 pd2 = uv - burstPos;
            float alongP = dot(pd2, dir);
            float perpP = length(pd2 - dir * alongP);
            // fill: thicker trail
            float trail = smoothstep(radial, 0.0, alongP)
                        * smoothstep(0.0, 0.01, alongP)
                        * smoothstep(0.07, 0.0, perpP);
            float fade = (1.0 - life) * (1.0 - life * 0.7);
            col += tint * particle * 3.0 * fade;
            col += tint * trail * 1.4 * fade;
        }
    }
    fragColor = vec4(finalize(col), 1.0);
}
