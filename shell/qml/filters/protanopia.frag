#version 300 es
// Settings, Accessibility, Colour filter: a Hyprland screen shader
// (decoration:screen_shader), applied to the whole screen.
precision highp float;
in vec2 v_texcoord;
uniform sampler2D tex;
out vec4 fragColor;

// Colours made easier to tell apart for red-blind (protanopia) sight (daltonize): what
// that sight loses is simulated (Machado, Oliveira and Fernandes 2009, full
// severity), and the difference is moved into colours that are still seen.
// Greys and white are unchanged.
vec3 seen(vec3 c) {
    return vec3(dot(vec3(0.152286, 1.052583, -0.204868), c),
                dot(vec3(0.114503, 0.786281, 0.099216), c),
                dot(vec3(-0.003882, -0.048116, 1.051998), c));
}
void main() {
    vec4 c = texture(tex, v_texcoord);
    vec3 lost = c.rgb - seen(c.rgb);
    vec3 shift = vec3(0.0, 0.7 * lost.r + lost.g, 0.7 * lost.r + lost.b);   // lost red into green and blue
    fragColor = vec4(clamp(c.rgb + shift, 0.0, 1.0), c.a);
}
