uniform float u_streaks; // hint_range(1.0, 16.0, 1.0) = 6.0  number of streaks
uniform float u_width; // hint_range(0.03, 0.3, 0.005) = 0.13  head radius / trail thickness
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.42;
    vec3 col = vec3(0.0);
    int streaks = int(clamp(u_streaks, 1.0, 16.0));
    float w = max(0.03, u_width);
    for (int i = 0; i < 16; i++) {
        if (i >= streaks) break;
        float fi = float(i);
        float ang = fi * 1.147 + 0.5;
        // De-synchronise the streaks: each has its own period so they
        // don't march across the screen in lockstep.
        float period = 1.0 + fract(fi * 0.317) * 0.8;
        float phase = fract((t / period) + fi * 0.37);
        vec2 dir = vec2(cos(ang), sin(ang));
        vec2 head = -dir * 2.6 + dir * (phase * 5.2);
        vec2 delta = uv - head;
        float along = dot(delta, dir);
        float perp  = length(delta - dir * along);

        // Bright round head at along=0 with a bloom falloff.
        float headCore  = exp(-(along * along) / (w * w * 0.35)) * exp(-(perp * perp) / (w * w * 0.35));
        float headGlow  = exp(-(along * along) * 6.0) * smoothstep(w * 3.5, 0.0, perp);

        // Tapered trail behind the head (-along > 0). Exponential along
        // the travel axis + soft edge perpendicular.
        float trailAlong = smoothstep(0.0, 0.015, -along) * exp(-max(-along, 0.0) * 1.8);
        float trailW     = mix(w, w * 0.2, smoothstep(0.0, 0.9, -along)) + 0.006;
        float trail      = trailAlong * smoothstep(trailW, 0.0, perp);

        vec3 tint      = tintedPalette(fi * 0.13 + t * 0.05);
        vec3 headTint  = mix(tint, vec3(1.0), 0.55);
        col += tint       * trail    * 2.2;
        col += tint       * headGlow * 1.2;
        col += headTint   * headCore * 3.0;
    }
    fragColor = vec4(finalize(col), 1.0);
}
