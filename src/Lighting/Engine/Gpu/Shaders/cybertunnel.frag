uniform float u_rings; // hint_range(2.0, 20.0, 1.0) = 6.0  rings per unit depth
uniform float u_spokes; // hint_range(0.0, 24.0, 1.0) = 8.0  radial wall lines
uniform float u_glow; // hint_range(0.2, 3.0, 0.05) = 1.2  edge bloom

// Diamond tunnel: concentric rotated squares racing out of a vanishing point,
// each ring lit a step further along the palette so the walls read as a
// rainbow ladder. Closed form, no loops. Depth is capped rather than left at
// 1/d so the rings stop compressing into moire at the centre; the core bloom
// covers the cap.
void main() {
    vec2 q = uvCentered();
    float t = mod(u_time * u_speed * 0.25, 1000.0);
    float d = max(abs(q.x) + abs(q.y), 1e-3);
    float depth = min(1.0 / d, 26.0);
    float rings = clamp(u_rings, 2.0, 20.0);
    float glow = clamp(u_glow, 0.2, 3.0);

    float z = depth * 0.35 - t;
    float ring = fract(z * rings);
    float edge = min(ring, 1.0 - ring) * 2.0;
    // Ring phase runs as 1/d, so a constant screen-space line needs a phase
    // width scaled by the analytic gradient, here 0.0084 * rings * depth^2.
    // Without it the near rings are hairlines and the far ones are solid
    // quadrants.
    float thin = clamp(0.70 * rings * depth * depth * 0.012, 0.02, 0.90);
    float line = smoothstep(thin, 0.0, edge);

    float a = atan(q.y, q.x) / 6.28318 + 0.5;
    float sn = clamp(u_spokes, 0.0, 24.0);
    float sp = fract(a * sn);
    // Angular width is constant, so a spoke fans into a beam at the frame
    // edge unless it is narrowed by radius.
    float sw = 0.10 / (1.0 + d * 3.0);
    float spoke = smoothstep(sw, 0.0, min(sp, 1.0 - sp)) * step(0.5, sn);

    // Far rings fade so the frame does not turn into a solid wall of lines.
    float fade = 1.0 / (1.0 + depth * 0.10);
    vec3 hue = tintedPalette(floor(z * rings) * 0.13 + t * 0.05);

    vec3 col = vec3(0.02, 0.02, 0.04);
    // Wall shading between rings: without it the tunnel is a few lines on
    // black instead of lit panels receding to the vanishing point.
    col += hue * (0.06 + 0.10 * ring) * fade;
    col += hue * line * (1.4 * glow) * fade;
    col += hue * spoke * 0.45 * fade * smoothstep(0.04, 0.5, d);
    // Vanishing-point bloom.
    col += tintedPalette(t * 0.05 + 0.5) * exp(-d * d * 34.0) * 1.8 * glow;
    col += vec3(0.10, 0.12, 0.20) * exp(-d * d * 4.0);

    fragColor = vec4(finalize(col), 1.0);
}
