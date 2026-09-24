uniform float u_density; // hint_range(12.0, 80.0, 1.0) = 28.0  rain column density
uniform float u_length; // hint_range(0.03, 0.5, 0.01) = 0.3  streak length
uniform float u_splash; // hint_range(0.0, 1.0, 0.02) = 0.85  puddle reflection intensity

void main() {
    vec2 uv = uv01();
    float t = mod(u_time * u_speed * 0.7, 1000.0);
    float cols = max(8.0, u_density);
    float len = clamp(u_length, 0.02, 0.6);
    float splash = clamp(u_splash, 0.0, 1.0);

    vec3 col = vec3(0.01, 0.005, 0.02);
    // Rain streaks.
    float x = floor(uv.x * cols);
    for (int layer = 0; layer < 3; layer++) {
        float fl = float(layer);
        float xOff = fl * 0.33;
        float xi = floor((uv.x + xOff) * cols);
        float colRand = hash21(vec2(xi, 17.0 + fl * 43.0));
        float fall = t * (1.5 + colRand * 2.5 + fl * 0.8);
        float head = fract(fall + colRand);
        float dy = uv.y - head;
        // Streak: single falloff from the head trailing upward.
        float streak = smoothstep(0.0, -len * 1.6, dy);
        // Neon tip glow.
        float tip = exp(-dy * dy * 320.0) * 1.1;
        float xLocal = fract((uv.x + xOff) * cols) - 0.5;
        float xMask = smoothstep(0.14, 0.0, abs(xLocal));
        vec3 tint = tintedPalette(colRand * 0.6 + fl * 0.1);
        float depth = 1.0 - fl * 0.22;
        col += tint * (streak * 0.75 + tip) * xMask * depth;
    }
    // Puddle reflection at the bottom: mirror the rain with a fade.
    float puddleY = 1.0 - uv.y; // reflected y
    float puddleMask = smoothstep(0.70, 1.0, uv.y) * splash;
    // Re-use rain pattern at reflected y, darkened.
    float reflX = floor(uv.x * cols);
    float reflRand = hash21(vec2(reflX, 17.0));
    float reflFall = t * (1.5 + reflRand * 2.5);
    float reflHead = fract(reflFall + reflRand);
    float reflDy = puddleY - reflHead;
    float reflStreak = smoothstep(0.0, -len * 1.6, reflDy);
    float reflXLocal = fract(uv.x * cols) - 0.5;
    float reflXMask = smoothstep(0.18, 0.0, abs(reflXLocal));
    vec3 reflTint = tintedPalette(reflRand * 0.6);
    col += reflTint * reflStreak * 0.35 * reflXMask * puddleMask;
    // Wet ground shimmer.
    col += vec3(0.03, 0.02, 0.045) * puddleMask * (0.5 + 0.5 * vnoise(vec2(uv.x * 40.0, t)));
    fragColor = vec4(finalize(col), 1.0);
}
