uniform float u_density; // hint_range(3.0, 30.0, 1.0) = 12.0  grid line density
uniform float u_pulse; // hint_range(0.0, 2.5, 0.05) = 1.2  pulse flow intensity
uniform float u_glow; // hint_range(0.2, 3.0, 0.05) = 1.2  line glow thickness
uniform float u_spread; // hint_range(0.0, 1.5, 0.05) = 0.75  hue walk horizon -> viewer, 1 = one full wheel

// 80s outrun neon grid: perspective ground plane scrolling toward the
// camera under a sunset horizon with a classic banded sun. Horizontal
// lines compress near the horizon and spread out near the viewer,
// giving the "road to infinity" feel. The whole grid moves every frame
// so the thumbnail already shows flow.
//
// uvCentered() puts y = -1 at the top and y = +1 at the bottom in this
// pipeline (we flip y in uv01 before centering), so y < 0 is sky and
// y > 0 is ground.

void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.5;
    float dens = clamp(u_density, 3.0, 30.0);
    float pulse = clamp(u_pulse, 0.0, 2.5);
    float glow = clamp(u_glow, 0.2, 3.0);
    float spread = clamp(u_spread, 0.0, 1.5);

    // Hue walk over the whole canvas: the bottom edge sits `spread` past
    // the horizon, the top of the sky half a spread behind it. The root
    // spends the range on the perspective-packed rows near the horizon.
    float hueShift = spread * sign(uv.y) * sqrt(abs(uv.y)) * (uv.y < 0.0 ? 0.5 : 1.0);

    // Sky: deep violet at top fading to warm magenta at the horizon.
    vec3 sky = mix(tintedPalette(0.72 + hueShift) * 0.06,
                   tintedPalette(0.95 + hueShift) * 0.35,
                   smoothstep(-1.0, 0.0, uv.y));
    vec3 col = sky;

    // Retro banded sun above the horizon.
    if (uv.y < 0.02) {
        vec2 sunP = vec2(uv.x, uv.y + 0.25);
        float sunR = length(sunP);
        float sunDisc = smoothstep(0.38, 0.0, sunR);
        // Horizontal bands that scroll upward across the sun face.
        float sunBands = smoothstep(0.45, 0.55, fract(sunP.y * 12.0 + t * 0.4));
        col += tintedPalette(0.03 + hueShift) * sunDisc * sunBands * 1.3;
    }

    // Horizon glow band: gaussian peak along y = 0.
    col += tintedPalette(hueShift) * exp(-uv.y * uv.y * 40.0) * 0.55;

    // Ground plane grid scrolling toward the camera.
    if (uv.y > 0.0) {
        float gy = uv.y;
        // Depth factor: large near the horizon (tiny gy), small near camera.
        float depth = 0.7 / max(gy, 0.02);

        // World-space grid coords. Lines move toward the camera as -t.
        float gridX = uv.x * depth * dens * 0.15;
        float gridZ = depth * dens * 0.22 - t * 1.8;

        float gfx = abs(fract(gridX) - 0.5);
        float gfz = abs(fract(gridZ) - 0.5);
        float fall = 30.0 / glow;
        float lineX = exp(-gfx * fall);
        float lineZ = exp(-gfz * fall);

        // Fade near the horizon so the alias stack at the vanishing
        // point doesn't turn into a solid bar.
        float fade = smoothstep(0.0, 0.08, gy);
        float lines = max(lineX, lineZ) * fade;
        float cross = lineX * lineZ * fade;

        // Pulses flowing down the horizontal lines (toward viewer).
        float pulsePhase = smoothstep(0.85, 1.0, fract(gridZ * 0.12));
        float flow = pulsePhase * lineZ * fade * pulse;

        vec3 tint = tintedPalette(0.88 + hueShift);      // magenta neon at the horizon
        vec3 crossTint = tintedPalette(0.56 + hueShift); // cyan intersections

        col += tint * lines;
        col += crossTint * cross * 2.8;
        col += tint * flow * 1.6;
    }

    fragColor = vec4(finalize(col), 1.0);
}
