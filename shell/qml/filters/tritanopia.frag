#version 300 es
// Settings, Accessibility, Colour filter: a Hyprland screen shader
// (decoration:screen_shader), applied to the whole screen.
precision highp float;
in vec2 v_texcoord;
uniform sampler2D tex;
out vec4 fragColor;

// Colours made easier to tell apart for blue-blind (tritanopia) sight (daltonize): what
// that sight loses is simulated (Machado, Oliveira and Fernandes 2009, full
// severity), and the difference is moved into colours that are still seen.
// Greys and white are unchanged.
vec3 seen(vec3 c) {
    return vec3(dot(vec3(1.255528, -0.076749, -0.178779), c),
                dot(vec3(-0.078411, 0.930809, 0.147602), c),
                dot(vec3(0.004733, 0.691367, 0.303900), c));
}
void main() {
    vec4 c = texture(tex, v_texcoord);
    vec3 lost = c.rgb - seen(c.rgb);
    vec3 shift = vec3(lost.r + 0.7 * lost.b, lost.g + 0.7 * lost.b, 0.0);   // lost blue into red and green
    fragColor = vec4(clamp(c.rgb + shift, 0.0, 1.0), c.a);
}
