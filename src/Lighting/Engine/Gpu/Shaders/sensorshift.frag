#version 330 core
out vec4 fragColor;
uniform vec2  u_resolution;
uniform float u_time;
uniform float u_speed;      // hint_range(-2.0, 2.0, 0.02) = 1.0
uniform float u_hue;        // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_colorize;   // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_saturation; // hint_range(0.0, 4.0, 0.01) = 1.0
uniform float u_contrast;   // hint_range(0.0, 4.0, 0.01) = 1.0
const float PI = 3.14159265;
const float S_SPEED = 1.0;
const float S_HUE = 0.0;
const float S_SAT = 1.0;
const float S_CON = 1.0;
uniform float u_hot_level; // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_hot_level_m; // hint_range(-1.0, 1.0, 0.01) = 1.0
uniform float u_hot_level_s; // sensor {"category":"quick","sensor":"summary/cpu-temp","min":30,"max":90}
uniform float u_col_low; // hint_range(0.0, 0.95, 0.01) = 0.0
uniform float u_col_shade; // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_col_aHue; // hint_range(0.0, 1.0, 0.01) = 0.63
uniform float u_col_aSat; // hint_range(0.0, 1.0, 0.01) = 0.85
uniform float u_col_aVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_col_bHue; // hint_range(0.0, 1.0, 0.01) = 0.42
uniform float u_col_bSat; // hint_range(0.0, 1.0, 0.01) = 0.89
uniform float u_col_bVal; // hint_range(0.0, 1.0, 0.01) = 0.95
uniform float u_col_cHue; // hint_range(0.0, 1.0, 0.01) = 0.14
uniform float u_col_cSat; // hint_range(0.0, 1.0, 0.01) = 0.9
uniform float u_col_cVal; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_col_dHue; // hint_range(0.0, 1.0, 0.01) = 0.01
uniform float u_col_dSat; // hint_range(0.0, 1.0, 0.01) = 0.9
uniform float u_col_dVal; // hint_range(0.0, 1.0, 0.01) = 1.0
vec2 uvCentered() {
  vec2 u = gl_FragCoord.xy / u_resolution;
  u.y = 1.0 - u.y;
  u = u * 2.0 - 1.0;
  u.x *= u_resolution.x / u_resolution.y;
  return u;
}
vec3 hsv2rgb(vec3 c) {
  vec4 K = vec4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
  vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
  return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
}
vec3 finalize(vec3 col, float hue) {
  float luma = dot(max(col, 0.0), vec3(0.2126, 0.7152, 0.0722));
  vec3 blended = mix(col, luma * hsv2rgb(vec3(hue, 1.0, 1.0)) * 1.4, clamp(u_colorize, 0.0, 1.0));
  float bl = dot(max(blended, 0.0), vec3(0.2126, 0.7152, 0.0722));
  blended = mix(vec3(bl), blended, clamp(u_saturation * S_SAT, 0.0, 4.0));
  blended = (blended - 0.5) * clamp(u_contrast * S_CON, 0.0, 4.0) + 0.5;
  return max(blended, 0.0);
}
void main() {
  vec2 p0 = uvCentered();
  vec2 p = p0;
  float t = mod(u_time, 3600.0) * u_speed * S_SPEED;
  float hue = u_hue + S_HUE;
  float v = 0.0;
  vec3 col = vec3(0.0);
  { float f = 0.0;
    f = (clamp(u_hot_level + u_hot_level_m * 1.0 * u_hot_level_s, 0.0, 1.0));
    v = f; }
  {
    vec3 c1 = hsv2rgb(vec3((u_col_aHue), (u_col_aSat), (u_col_aVal))); vec3 c2 = hsv2rgb(vec3((u_col_bHue), (u_col_bSat), (u_col_bVal)));
    vec3 c3 = hsv2rgb(vec3((u_col_cHue), (u_col_cSat), (u_col_cVal))); vec3 c4 = hsv2rgb(vec3((u_col_dHue), (u_col_dSat), (u_col_dVal)));
    float k = clamp((v - (u_col_low)) / (1.0 - (u_col_low)), 0.0, 1.0);
    vec3 g = mix(c1, c2, smoothstep(0.0, 0.3333, k));
    g = mix(g, c3, smoothstep(0.3333, 0.6667, k));
    g = mix(g, c4, smoothstep(0.6667, 1.0, k));
    col = g * mix(1.0, v, (u_col_shade));
  }
  fragColor = vec4(finalize(col, hue), 1.0);
}