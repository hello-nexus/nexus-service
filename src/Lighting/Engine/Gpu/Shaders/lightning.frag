uniform float u_boltRate; // hint_range(0.3, 3.0, 0.05) = 0.8  bolts per second
uniform float u_forks; // hint_range(0.0, 6.0, 1.0) = 3.0  fork count per bolt
uniform float u_glow; // hint_range(0.3, 2.0, 0.05) = 1.0  bolt glow halo
uniform float u_audioBoost;

vec2 rot2(vec2 v, float a) {
    float c = cos(a), s = sin(a);
    return vec2(c * v.x - s * v.y, s * v.x + c * v.y);
}

// Smooth 1D noise - quintic interpolation avoids segment-boundary kinks
float n1(float x, float seed) {
    float i = floor(x), f = fract(x);
    f = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    return mix(hash21(vec2(i, seed)), hash21(vec2(i + 1.0, seed)), f) * 2.0 - 1.0;
}

// Multi-octave bolt displacement with random-walk amplitude scaling
float boltPath(float along, float seed) {
    float d = n1(along * 2.5, seed) * 0.50
            + n1(along * 6.0, seed + 7.0) * 0.22
            + n1(along * 14.0, seed + 13.0) * 0.10
            + n1(along * 30.0, seed + 19.0) * 0.04;
    return d * (0.3 + sqrt(max(along, 0.0)) * 0.5);
}

void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed, 1000.0);
    float rate = clamp(u_boltRate, 0.1, 4.0);
    int nForks = int(clamp(u_forks, 0.0, 8.0));
    float glw = clamp(u_glow, 0.1, 3.0);

    float aPresence = audioPresence() * u_audioBoost;
    float beatRate = 1.0 + u_audioBeat * aPresence * 0.6;
    float bassFlash = u_audioBass * aPresence;

    // perf: cloud-haze fbm->fbm3 (soft backdrop, fine octaves invisible)
    float c1 = fbm3(uv * 1.2 + vec2(t * 0.08, t * 0.05));
    float c2 = fbm3(uv * 2.0 - vec2(t * 0.06, -t * 0.09));
    vec3 col = tintedPalette(0.62 + c1 * 0.08) * (0.05 + c1 * 0.06 + c2 * 0.02);

    vec3 hot = vec3(1.0, 0.97, 0.92);
    float totalFlash = 0.0;

    for (int b = 0; b < 3; b++) {
        float bf = float(b);
        float tr = rate * beatRate * (0.55 + bf * 0.22);
        float ph = t * tr * 0.35 + bf * 0.47;
        float bp = fract(ph);
        float bid = floor(ph) + bf * 113.0;

        float grow = smoothstep(0.0, 0.10, bp);
        float decay = exp(-bp * 6.0) * smoothstep(0.0, 0.015, bp);
        float rs1 = exp(-abs(bp - 0.25) * 45.0) * 0.5;
        float rs2 = exp(-abs(bp - 0.38) * 55.0) * 0.25;
        float life = min(decay + (rs1 + rs2) * decay * 2.0, 1.4);

        float sp = hash21(vec2(bid, 1.0));
        float ep = (hash21(vec2(bid, 2.0)) - 0.5) * 2.2;
        float tilt = (hash21(vec2(bid, 3.0)) - 0.5) * 1.0;
        vec2 origin, dir;
        if (sp < 0.50) {
            origin = vec2(ep, -1.15);
            dir = rot2(vec2(0.0, 1.0), tilt);
        } else if (sp < 0.70) {
            origin = vec2(-1.3, ep);
            dir = rot2(vec2(1.0, 0.0), tilt);
        } else if (sp < 0.90) {
            origin = vec2(1.3, ep);
            dir = rot2(vec2(-1.0, 0.0), tilt);
        } else {
            origin = vec2(ep, 1.15);
            dir = rot2(vec2(0.0, -1.0), tilt);
        }

        float bLen = 1.8 + hash21(vec2(bid, 4.0)) * 1.2;
        float aLen = bLen * grow;
        vec2 perp = vec2(-dir.y, dir.x);
        vec3 bTint = tintedPalette(0.58 + hash21(vec2(bid, 5.0)) * 0.1);

        // Trunk
        vec2 dp = uv - origin;
        float proj = dot(dp, dir);
        float across = dot(dp, perp);

        if (proj > -0.2 && proj < aLen + 0.2) {
            float cp = clamp(proj, 0.0, aLen);
            float disp = boltPath(cp, bid);
            float d = abs(across - disp);
            float taper = smoothstep(aLen, aLen - 0.35, proj)
                        * smoothstep(-0.05, 0.15, proj);
            col += (hot * exp(-d * 90.0) + bTint * exp(-d * 10.0 / glw) * 0.55)
                 * life * taper;
        }

        // Forks branch from the displaced trunk
        for (int f = 0; f < 8; f++) {
            if (f >= nForks) break;
            float ff = float(f);
            float fAt = 0.15 + hash21(vec2(bid, ff * 13.0 + 40.0)) * aLen * 0.7;
            if (fAt > aLen) continue;

            vec2 fOrig = origin + dir * fAt + perp * boltPath(fAt, bid);
            float fAng = (hash21(vec2(bid, ff * 17.0 + 50.0)) - 0.5) * 1.8;
            vec2 fDir = rot2(dir, fAng);
            vec2 fPerp = vec2(-fDir.y, fDir.x);
            float fLen = 0.3 + hash21(vec2(bid, ff * 23.0 + 60.0)) * 0.7;

            vec2 fdp = uv - fOrig;
            float fProj = dot(fdp, fDir);
            float fAcross = dot(fdp, fPerp);

            if (fProj > -0.1 && fProj < fLen + 0.1) {
                float fcp = clamp(fProj, 0.0, fLen);
                float fd = abs(fAcross - boltPath(fcp, bid + ff * 31.0 + 200.0));
                float ft = (1.0 - smoothstep(0.0, fLen, fProj))
                         * smoothstep(-0.02, 0.06, fProj);
                col += (hot * exp(-fd * 120.0) + bTint * exp(-fd * 14.0 / glw) * 0.35)
                     * life * ft * 0.65;
            }
        }

        totalFlash += life * life;
        col += hot * exp(-length(uv - origin) * 3.0) * life * 0.35;
    }

    vec3 flashTint = tintedPalette(0.60);
    col += flashTint * totalFlash * (0.12 + bassFlash * 0.08);
    col += flashTint * c1 * totalFlash * 0.06;

    fragColor = vec4(finalize(col), 1.0);
}
