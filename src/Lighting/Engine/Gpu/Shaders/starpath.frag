uniform float u_trail; // hint_range(0.1, 1.5, 0.05) = 0.6  trail arc length
uniform float u_axisShift; // hint_range(0.0, 1.0, 0.05) = 0.3  polar axis offset
uniform float u_brightness; // hint_range(0.3, 2.0, 0.05) = 1.0  trail brightness

// Long-exposure star-trail look: every star sweeps a curved arc around
// a polar axis as the camera (notional) rotates. Stars are placed on a
// uniform grid in (r, theta) space, each one drawn as a smear from its
// current angle backwards by the trail length.
void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * 0.1, 1000.0);
    float trail = clamp(u_trail, 0.05, 2.0);
    float axisShift = clamp(u_axisShift, 0.0, 1.5);
    float bright = clamp(u_brightness, 0.0, 3.0);

    // Offset polar axis so trails curve dramatically across the frame
    // instead of all being concentric on (0,0).
    vec2 p = uv - vec2(axisShift * 0.6, axisShift * 0.3);
    float r = length(p) + 0.001;
    float a = atan(p.y, p.x);

    // Faint milky-way-style ambient gradient across the frame so the
    // background is never pure black - gives the arcs something to sit on.
    // perf: faint ambient sky gradient, low-detail -> fbm3
    float skyFbm = fbm3(uv * 1.8 + vec2(0.0, t * 0.3));
    vec3 col = tintedPalette(0.6 + r * 0.1) * (0.06 + skyFbm * 0.08);

    // Background star field - tiny bright points scattered across the frame.
    vec2 starGrid = floor(uv * 30.0);
    float bgHash = hash21(starGrid);
    vec2 starLocal = fract(uv * 30.0) - 0.5;
    if (bgHash > 0.88) {
        float bgStar = exp(-dot(starLocal, starLocal) * 90.0) *
                       (0.5 + 0.5 * sin(t * 2.0 + bgHash * 30.0));
        col += tintedPalette(bgHash * 0.6) * bgStar * bright * 0.9;
    }

    // Long-exposure trail rings.
    float rStep = 0.075;
    float rCell = floor(r / rStep);

    // perf: nested trip count was 5 radial cells x 7 stars = 35; trim the
    // radial neighbourhood to 3 cells (-1..1). The 0.09 radial cull already
    // discards far rings, so the outer two cells contributed little.
    for (int dr = -1; dr <= 1; dr++) {
        float rc = rCell + float(dr);
        if (rc < 0.0) continue;
        float ringR = (rc + 0.5) * rStep;
        float radial = abs(r - ringR);
        if (radial > 0.09) continue;

        // perf: stars per ring 7 -> 4 (3x4=12 inner iters vs 35, ~2.9x)
        for (int s = 0; s < 4; s++) {
            float sf = float(s);
            float starHash = hash21(vec2(rc, sf * 13.7));
            float starPhase = starHash * 6.28318;
            // Head angle sweeps as t advances; outer rings sweep slower.
            float headAngle = starPhase + t / (1.0 + rc * 0.1);
            // Arc offset from head.
            float arcOffset = a - headAngle;
            arcOffset = mod(arcOffset + 3.14159, 6.28318) - 3.14159;
            float along = -arcOffset;

            float trailLen = trail * 0.8 + 0.15;
            float alongFade = smoothstep(-0.08, 0.02, along) *
                              smoothstep(trailLen, 0.0, along);
            // Wider radial falloff - arcs are visible across most of the ring band.
            float radialFade = exp(-radial * radial * 500.0);

            float starGlow = alongFade * radialFade;
            // Bright head - sharper, hotter point right at along~0.
            float headGlow = exp(-along * along * 800.0) * radialFade;

            vec3 starColor = tintedPalette(starHash * 0.5 + rc * 0.07 + t * 0.15);
            col += starColor * starGlow * bright * 1.4;
            col += vec3(1.0, 0.97, 0.92) * headGlow * bright * 0.9;
        }
    }

    fragColor = vec4(finalize(col), 1.0);
}
