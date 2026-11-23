#version 300 es
// Settings, Accessibility, Colour filter: a Hyprland screen shader
// (decoration:screen_shader), applied to the whole screen.
precision highp float;
in vec2 v_texcoord;
uniform sampler2D tex;
out vec4 fragColor;

// Shades of grey (luminance, Rec. 709).
void main() {
    vec4 c = texture(tex, v_texcoord);
    float y = dot(c.rgb, vec3(0.2126, 0.7152, 0.0722));
    fragColor = vec4(vec3(y), c.a);
}
