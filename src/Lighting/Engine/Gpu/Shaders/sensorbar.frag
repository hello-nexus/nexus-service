#version 330 core
out vec4 fragColor;
uniform vec2  u_resolution;
uniform float u_time;
uniform float u_speed;      // hint_range(-2.0, 2.0, 0.02) = 1.0
uniform float u_hue;        // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_colorize;   // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_saturation; // hint_range(0.0, 4.0, 0.01) = 1.0
uniform float u_contrast;   // hint_range(0.0, 4.0, 0.01) = 1.0
uniform float u_sensorbar_level_s; // sensor {"category":"quick","sensor":"summary/cpu-usage","min":0,"max":100}
uniform float u_sensorbar_edge; // hint_range(0.0, 0.5, 0.01) = 0.08

vec3 hsv2rgb(vec3 c) {
  vec3 p = abs(fract(c.xxx + vec3(1.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0);
  return c.z * mix(vec3(1.0), clamp(p - 1.0, 0.0, 1.0), c.y);
}

void main() {
  vec2 uv = gl_FragCoord.xy / u_resolution;
  float height = uv.y;
  float level = clamp(u_sensorbar_level_s, 0.0, 1.0);
  float edge = max(u_sensorbar_edge, 0.001);
  float lit = 1.0 - smoothstep(level - edge * 0.5, level + edge * 0.5, height);
  float hue = fract(0.33 * (1.0 - level) + u_hue);
  vec3 col = hsv2rgb(vec3(hue, clamp(u_saturation, 0.0, 1.0), 1.0)) * lit;
  col = clamp((col - 0.5) * u_contrast + 0.5, 0.0, 1.0) * step(0.001, lit);
  fragColor = vec4(col, 1.0);
}
