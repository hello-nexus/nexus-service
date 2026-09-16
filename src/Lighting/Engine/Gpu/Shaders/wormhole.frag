uniform float u_depth; // hint_range(0.4, 3.0, 0.05) = 1.2  tunnel recede rate
uniform float u_rings; // hint_range(2.0, 16.0, 1.0) = 5.0  circular band count
uniform float u_twist; // hint_range(-2.0, 2.0, 0.05) = 0.6  spiral strength

// 2D wormhole illusion: map radius to depth via 1/r, so samples near the
// center appear "far". Twist rotates with depth so walls corkscrew. Two
// band patterns (radial + angular) multiply to give checker-like wall.
void main() {
    vec2 uv = uvCentered();
    float t = u_time * u_speed * 0.55;
    float depth = max(0.3, u_depth);
    float rings = max(2.0, u_rings);
    float twist = u_twist;

    float r = length(uv);
    float a = atan(uv.y, uv.x);
    // Inverse radius -> apparent depth into the tunnel.
    float tz = 1.0 / max(r, 0.02);
    // Scroll depth forward.
    float z = tz * depth * 0.25 - t;
    // Spiral angle offset: twist increases with depth.
    a += twist * tz * 0.12 + t * 0.25;

    float bands = 0.5 + 0.5 * sin(z * 6.28318);
    float angPattern = 0.5 + 0.5 * sin(a * rings);
    float wall = mix(bands, bands * angPattern, 0.7);

    // Colour cycles with depth so the tunnel appears to rush through hues.
    vec3 col = tintedPalette(z * 0.12 + t * 0.05);
    col *= 0.15 + 0.85 * wall;

    // Central bright core pulling the eye into the vanishing point.
    float core = smoothstep(0.0, 0.2, r);
    col *= core;
    col += vec3(1.0, 0.92, 0.78) * pow(1.0 - core, 3.5) * 0.7;
    fragColor = vec4(finalize(col * 1.25), 1.0);
}
