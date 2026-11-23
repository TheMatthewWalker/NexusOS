#version 300 es
// Settings, Accessibility, Colour filter: a Hyprland screen shader
// (decoration:screen_shader), applied to the whole screen.
precision highp float;
in vec2 v_texcoord;
uniform sampler2D tex;
out vec4 fragColor;

// Inverted colours: light text on dark, and the other way round.
void main() {
    vec4 c = texture(tex, v_texcoord);
    fragColor = vec4(1.0 - c.rgb, c.a);
}
