#version 300 es
// Settings, Accessibility, Colour filter: a Hyprland screen shader
// (decoration:screen_shader), applied to the whole screen.
precision highp float;
in vec2 v_texcoord;
uniform sampler2D tex;
out vec4 fragColor;

// Colours made easier to tell apart for green-blind (deuteranopia) sight (daltonize): what
// that sight loses is simulated (Machado, Oliveira and Fernandes 2009, full
// severity), and the difference is moved into colours that are still seen.
// Greys and white are unchanged.
vec3 seen(vec3 c) {
    return vec3(dot(vec3(0.367322, 0.860646, -0.227968), c),
                dot(vec3(0.280085, 0.672501, 0.047413), c),
                dot(vec3(-0.011820, 0.042940, 0.968881), c));
}
void main() {
    vec4 c = texture(tex, v_texcoord);
    vec3 lost = c.rgb - seen(c.rgb);
    vec3 shift = vec3(0.0, 0.7 * lost.r + lost.g, 0.7 * lost.r + lost.b);   // lost red into green and blue
    fragColor = vec4(clamp(c.rgb + shift, 0.0, 1.0), c.a);
}
