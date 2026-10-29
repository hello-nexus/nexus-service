uniform float u_size; // hint_range(0.03, 0.25, 0.005) = 0.08  ball radius
uniform float u_count; // hint_range(1.0, 24.0, 1.0) = 8.0  ball count
uniform float u_trail; // hint_range(0.0, 1.0, 0.02) = 0.6  trail length
uniform float u_mood; // hint_range(0.0, 5.0, 1.0) = 0.0  palette mood: 0 full spectrum, 1 warm, 2 cool, 3 neon, 4 sunset, 5 aurora
#define tintedPalette(t) moodPalette((t), u_mood)

// Elastic balls ricocheting off the frame edges in straight lines. Each ball
// stretches along its motion, squashes flat against the wall it hits, and drags
// a trail of fading ghosts; every ball also lights the backdrop around it, so
// the whole frame fills with moving color.
float tri(float x) {
    return abs(fract(x) * 2.0 - 1.0);
}

void main() {
    vec2 uv = uvCentered();
    float ar = u_resolution.x / u_resolution.y;
    float t = u_time * u_speed * 0.35;
    float s = clamp(u_size, 0.02, 0.4) * 3.4;
    int count = int(clamp(u_count, 1.0, 24.0));
    float trail = clamp(u_trail, 0.0, 1.0);

    vec3 col = tintedPalette(0.1 + uv.y * 0.25 + sin(t * 0.5 + uv.x) * 0.1) * 0.40;
    col += tintedPalette(0.55 + uv.x * 0.15 - t * 0.04) * 0.18;

    for (int i = 0; i < 24; i++) {
        if (i >= count) break;
        float fi = float(i);
        float rad = s * (0.75 + fract(fi * 0.37) * 0.5);
        vec2 range = max(vec2(ar - rad, 1.0 - rad), vec2(0.001));
        vec2 rate = vec2(0.13 + fract(fi * 0.173) * 0.17, 0.11 + fract(fi * 0.241) * 0.19);
        // The head and its ghosts all stay near the head, so skip a ball that is far from this pixel.
        vec2 ph0 = t * rate + vec2(fi * 0.317, fi * 0.531);
        vec2 head = (vec2(tri(ph0.x), tri(ph0.y)) * 2.0 - 1.0) * range;
        vec2 toHead = uv - head;
        if (dot(toHead, toHead) > (rad * 4.6 + 0.4) * (rad * 4.6 + 0.4)) {
            continue;
        }
        vec3 tint = tintedPalette(fi * 0.137 + t * 0.03);
        for (int kk = 0; kk < 2; kk++) {
            int k = 1 - kk;
            if (k > 0 && trail < 0.02) continue;
            float lag = float(k) * 0.1 * trail;
            vec2 ph = (t - lag) * rate + vec2(fi * 0.317, fi * 0.531);
            vec2 c = (vec2(tri(ph.x), tri(ph.y)) * 2.0 - 1.0) * range;
            vec2 wall = (1.0 - abs(c) / range);
            float sqx = smoothstep(0.12, 0.0, wall.x);
            float sqy = smoothstep(0.12, 0.0, wall.y);
            vec2 axes = vec2(1.0 + 0.3 - 0.55 * sqx + 0.25 * sqy, 1.0 - 0.15 + 0.3 * sqx - 0.55 * sqy);
            vec2 q = (uv - c) / (rad * axes);
            float dd = dot(q, q);
            if (dd > 10.24) {
                continue;
            }
            float d = sqrt(dd);
            float fade = k == 0 ? 1.0 : 0.4 * trail;
            float body = smoothstep(1.0, 0.8, d);
            col += tint * exp(-dd * 0.7) * 0.55 * fade;
            if (k == 0) {
                // Only the head is shaded like a sphere; trailing ghosts are flat color.
                float lit = 1.0 - smoothstep(0.0, 1.0, length(q - vec2(-0.3, -0.35)));
                col = mix(col, tint * (0.8 + 0.9 * lit) + vec3(lit * lit * 0.45), body);
            } else {
                col = mix(col, tint * 0.95, body * min(fade * 1.4, 1.0));
            }
        }
    }
    fragColor = vec4(finalize(col * 1.2), 1.0);
}
