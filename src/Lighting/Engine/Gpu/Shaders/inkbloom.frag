uniform float u_spread; // hint_range(0.3, 2.0, 0.05) = 1.0  bloom growth rate
uniform float u_curl; // hint_range(0.0, 2.0, 0.05) = 0.6  tendril turbulence
uniform float u_fade; // hint_range(0.3, 3.0, 0.05) = 0.7  lifespan falloff

// Ink in water: three cyclic drops, each growing a fbm-bordered blob from a
// fixed origin while fading with age. Additive blend of the three colours
// lets overlapping drops mix into secondary hues.
void main() {
    vec2 uv = uvCentered();
    float t = mod(u_time * u_speed * 0.2, 1000.0);
    float spr = max(0.2, u_spread);
    float curl = max(0.0, u_curl);
    float fade = max(0.3, u_fade);

    vec3 col = vec3(0.0);
    for (int i = 0; i < 3; i++) {
        float idx = float(i);
        float lifespan = 3.5;
        float ofs = idx * 0.95;
        float age = mod(t + ofs, lifespan) / lifespan;
        vec2 origin = vec2(sin(idx * 2.1) * 0.42, cos(idx * 2.3) * 0.38);
        float rad = age * spr * 1.15;
        vec2 dir = uv - origin;
        float d = length(dir);
        float ang = atan(dir.y, dir.x);
        // fbm-deformed radius -> ragged edge.
        // perf: fbm3 (3 oct) per drop; top octaves of the edge wash out under the smoothstep.
        float edgeN = fbm3(vec2(ang * 3.0 + age * 5.0, d * 7.5 + t)) - 0.5;
        float edge = d - rad + edgeN * curl * 0.32 * age;
        float blob = smoothstep(0.1, -0.03, edge);
        float life = pow(max(1.0 - age, 0.0), fade);
        blob *= life;
        col += tintedPalette(0.15 + idx * 0.28 + age * 0.08) * blob * 1.4;
    }

    fragColor = vec4(finalize(col), 1.0);
}
