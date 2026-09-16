uniform float u_aHue; // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_aSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_aVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bHue; // hint_range(0.0, 1.0, 0.01) = 0.15
uniform float u_bSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_cHue; // hint_range(0.0, 1.0, 0.01) = 0.55
uniform float u_cSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_cVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_dHue; // hint_range(0.0, 1.0, 0.01) = 0.8
uniform float u_dSat; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_dVal; // hint_range(0.0, 1.0, 0.01) = 1.0

// Four corner colours blended bilinearly - the widest colour spread of the set.
void main() {
    vec2 p = uv01();
    vec3 ca = hsv2rgb(vec3(u_aHue, u_aSat, u_aVal));
    vec3 cb = hsv2rgb(vec3(u_bHue, u_bSat, u_bVal));
    vec3 cc = hsv2rgb(vec3(u_cHue, u_cSat, u_cVal));
    vec3 cd = hsv2rgb(vec3(u_dHue, u_dSat, u_dVal));
    vec3 top = mix(ca, cb, p.x);
    vec3 bot = mix(cc, cd, p.x);
    fragColor = vec4(mix(top, bot, p.y), 1.0);
}
