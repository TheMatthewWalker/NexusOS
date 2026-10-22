// Nexus plasma core: one fragment-shader body shared by both shell spikes.
//
// scripts/spike/build_shared.py wraps it for Qt (GLSL 440 -> .qsb) and WebGL2
// (GLSL ES 3.00). The wrapper provides:
//   uniforms u_time (s), u_energy (0 idle .. 1 responding), u_awake (0..1, hover
//   or focus), u_envelope (0..1, simulated multiband response level), u_strands (0/1)
//   vec4 sampleCore(vec2 uv): the plasma asset, PREMULTIPLIED alpha.
// The result is premultiplied too. Reduced motion is the wrapper holding u_time
// still, so the frozen frame is the same picture the animation starts from.

float nxHash(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

float nxNoise(vec2 p) {
    vec2 i = floor(p), f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(nxHash(i), nxHash(i + vec2(1.0, 0.0)), u.x),
               mix(nxHash(i + vec2(0.0, 1.0)), nxHash(i + vec2(1.0, 1.0)), u.x), u.y);
}

float nxFbm(vec2 p) {
    float v = 0.0, a = 0.5;
    for (int i = 0; i < 4; i++) { v += a * nxNoise(p); p = p * 2.03 + 17.1; a *= 0.5; }
    return v;
}

// Glow from one inclined, rotating strand ring. The far half of the ring is
// dimmed so the strand reads as passing behind the sphere.
vec3 nxStrand(vec2 c, float tilt, float radius, float speed, float t, vec3 tint) {
    float ct = cos(tilt), st = sin(tilt);
    vec2 r = vec2(ct * c.x + st * c.y, -st * c.x + ct * c.y);
    r.y /= 0.32;                                    // ring seen at an incline
    float d = abs(length(r) - radius);
    float ang = atan(r.y, r.x);
    float front = 0.35 + 0.65 * smoothstep(-0.2, 0.4, sin(ang));
    float pulse = 0.55 + 0.45 * sin(ang * 3.0 - t * speed);
    return tint * exp(-d * d / 0.00018) * front * pulse;
}

vec4 nexusPlasma(vec2 uv) {
    vec2 c = uv - 0.5;
    float r = length(c);
    float flow = 0.035 + 0.05 * u_awake + 0.16 * u_energy;
    float t = u_time * flow;

    // Gas: displace the painted gas locally with two advected noise fields,
    // strongest inside the sphere, fading towards the rim. The image itself
    // never rotates as a whole.
    float body = 1.0 - smoothstep(0.25, 0.46, r);
    vec2 q = vec2(nxFbm(c * 4.0 + vec2(t, -t * 0.7)), nxFbm(c * 4.0 + vec2(-t * 0.6, t) + 9.2));
    float amp = (0.006 + 0.004 * u_awake + 0.016 * u_energy * (0.6 + 0.4 * u_envelope)) * body;
    vec2 swirl = vec2(-c.y, c.x) * 0.05 * sin(t * 2.0 + r * 9.0) * (0.25 + u_energy);
    vec4 col = sampleCore(uv + (q - 0.5) * 2.0 * amp + swirl * body);

    // Responding: the gases brighten with the simulated envelope.
    col.rgb *= 1.0 + (0.06 * u_awake + 0.22 * u_energy * u_envelope) * body;

    // Strands: additive light, kept inside the glow so the frame stays round.
    if (u_strands > 0.5) {
        float st = u_time * (0.25 + 0.6 * u_energy);
        vec3 s = nxStrand(c, 0.42, 0.33, 1.3, st, vec3(0.40, 0.80, 1.00))
               + nxStrand(c, -0.55, 0.36, 0.9, st + 2.0, vec3(0.75, 0.45, 1.00))
               + nxStrand(c, 1.20, 0.30, 1.7, st + 4.0, vec3(1.00, 0.55, 0.80));
        float a = clamp(max(s.r, max(s.g, s.b)), 0.0, 1.0) * (0.35 + 0.25 * u_awake + 0.4 * u_energy)
                * (1.0 - smoothstep(0.40, 0.49, r));
        col.rgb += s * a;           // premultiplied "over" with additive light
        col.a = max(col.a, a);
    }
    // Keep the output valid premultiplied colour (rgb <= alpha); Wayland
    // compositors blend surfaces as premultiplied.
    col.a = clamp(col.a, 0.0, 1.0);
    col.rgb = clamp(col.rgb, vec3(0.0), vec3(col.a));
    return col;
}
