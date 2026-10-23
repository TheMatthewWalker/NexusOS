#version 440
layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;
    mat4 uRot;
    vec3 uPal;
    vec2 uSize;
    vec2 uCenter;
    float uCoreR, uTime, uEnergy, uBeat, uVoice, uListen, uThink, uRipple, uAwake, uSteps;
};
layout(binding = 1) uniform sampler2D uNoise;

// Qt items cannot sample 3D textures, so the 64^3 field is packed as 8x8 tiles
// of 64x64, each with a 1-texel wrapped border so bilinear filtering never
// bleeds between tiles. Two fetches give the same smooth noise as the study.
float n3(vec3 p) {
    vec3 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    vec2 xy = mod(i.xy, 64.0) + f.xy;
    float z0 = mod(i.z, 64.0), z1 = mod(i.z + 1.0, 64.0);
    vec2 a = vec2(mod(z0, 8.0), floor(z0 / 8.0)) * 66.0 + 1.5 + xy;
    vec2 b = vec2(mod(z1, 8.0), floor(z1 / 8.0)) * 66.0 + 1.5 + xy;
    return mix(texture(uNoise, a / 528.0).r, texture(uNoise, b / 528.0).r, f.z);
}
vec3 rot(vec3 v) { return (vec4(v, 0.0) * uRot).xyz; }
// Nexus "Nova" core: the approved live 3D core (docs/design/spaceui/studies/nexus-core-nova.html),
// as one fragment-shader body for the shell. shell/tools/build.py wraps it for Qt.
//
// The wrapper provides:
//   uniforms uTime, uEnergy, uBeat, uVoice, uListen, uThink, uRipple, uAwake, uSteps (floats), uPal (vec3)
//   float n3(vec3 p)    smooth value noise from the shared 64^3 field (same values as the study)
//   vec3  rot(vec3 v)   the inverse core rotation (view space -> core space)
// novaCore() takes the pixel position (for per-pixel hashes), the position in core units
// (+y up, core radius 1) and a fade for screen-wide effects at the surface edge. It returns
// premultiplied colour: alpha is the gas's coverage, and light added on top (heart, flare,
// sparks) may exceed alpha. That is additive light, which premultiplied blending composites
// correctly over the dark desktop.

const float TAU = 6.2831853;
const mat3 OCT = mat3(0.00, 0.80, 0.60, -0.80, 0.36, -0.48, -0.60, -0.48, 0.64);

float fbm(vec3 p) {
    float s = 0.0, a = 0.5;
    for (int i = 0; i < 4; i++) { s += a * n3(p); p = OCT * p * 2.03; a *= 0.5; }
    return s / 0.9375;
}
float hash12(vec2 p) { vec3 q = fract(vec3(p.xyx) * 0.1031); q += dot(q, q.yzx + 33.33); return fract((q.x + q.y) * q.z); }

vec3 pal(vec3 nova, vec3 plasma, vec3 arc) { return nova * uPal.x + plasma * uPal.y + arc * uPal.z; }
vec3 heartC() { return pal(vec3(0.82, 0.90, 1.00), vec3(1.00, 0.86, 0.68), vec3(0.82, 0.96, 1.00)); }
vec3 tendrilC(float k) {
    return mix(pal(vec3(1.00, 0.45, 0.12), vec3(1.00, 0.32, 0.70), vec3(0.25, 0.65, 1.00)),
               pal(vec3(1.00, 0.86, 0.62), vec3(1.00, 0.82, 0.92), vec3(0.86, 0.97, 1.00)), k);
}
vec3 fireC(float k) {
    return mix(pal(vec3(0.90, 0.20, 0.05), vec3(0.80, 0.18, 0.75), vec3(0.08, 0.40, 1.00)),
               pal(vec3(1.00, 0.62, 0.30), vec3(1.00, 0.55, 0.88), vec3(0.55, 0.88, 1.00)), k);
}
vec3 gasC(float n) {
    return mix(pal(vec3(0.16, 0.24, 0.95), vec3(0.10, 0.42, 1.00), vec3(0.04, 0.18, 0.72)),
               pal(vec3(0.56, 0.24, 0.95), vec3(0.55, 0.24, 1.00), vec3(0.18, 0.62, 1.00)), smoothstep(0.45, 0.75, n));
}
vec3 ringTint(int k) {
    return k == 0 ? pal(vec3(0.72, 0.64, 1.00), vec3(0.30, 0.85, 1.00), vec3(0.40, 0.86, 1.00))
                  : pal(vec3(0.48, 0.62, 1.00), vec3(0.72, 0.45, 1.00), vec3(0.30, 0.78, 1.00));
}
vec3 ringNormal(int k, float t) {
    float fk = float(k);
    float a = 1.15 + fk * 1.1 + 0.25 * sin(t * 0.07 + fk * 2.0);
    float b = fk * 2.4 + t * (0.05 + 0.02 * fk);
    return normalize(vec3(sin(a) * cos(b), cos(a), sin(a) * sin(b)));
}

vec4 novaCore(vec2 frag, vec2 p, float edgeFade) {
    float rp = length(p);
    vec3 acc = vec3(0.0);
    float T = 1.0;

    if (rp < 1.75) {
        vec3 ro = rot(vec3(0.0, 0.0, 4.0));
        vec3 rd = rot(normalize(vec3(p * 0.258, -1.0)));

        // Two thin luminous rings, composited at their true depth inside the march.
        vec3 ringCol[2]; float ringT[2]; float pending[2];
        for (int k = 0; k < 2; k++) {
            vec3 n = ringNormal(k, uTime);
            float dn = dot(rd, n);
            float tp = abs(dn) > 1e-4 ? -dot(ro, n) / dn : -1.0;
            vec3 x = ro + rd * tp;
            float d = abs(length(x) - (1.10 + 0.22 * float(k)));
            vec3 u = normalize(cross(n, abs(n.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0)));
            float a = atan(dot(x, cross(n, u)), dot(x, u));
            float spin = uTime * (0.30 + 0.15 * float(k)) * (k == 1 ? -1.0 : 1.0) * (1.0 + 1.2 * uEnergy);
            float arc = smoothstep(-0.2, 0.9, sin(a + spin));
            float pkt = exp(-pow(mod(a - uTime * (1.2 + 0.5 * float(k)), TAU) - 3.14159, 2.0) * 30.0);
            float w = 0.007 + 0.005 * uListen + 0.003 * uEnergy;
            float g = exp(-d * d / (w * w)) * arc * (0.45 + 1.0 * uListen + 0.6 * uEnergy + 0.3 * uAwake)
                    + exp(-d * d / (w * w * 0.5)) * pkt * 1.6 * arc
                    + exp(-d * d / (w * w * 40.0)) * 0.035;
            ringCol[k] = ringTint(k) * g;
            ringT[k] = tp;
            pending[k] = tp > 0.0 ? 1.0 : 0.0;
        }

        float b = dot(ro, rd);
        float h = b * b - (dot(ro, ro) - 1.55 * 1.55);
        if (h > 0.0) {
            h = sqrt(h);
            float t0 = max(-b - h, 0.0), t1 = -b + h;
            int steps = int(clamp(uSteps, 8.0, 64.0));   // fewer samples for the small launcher core
            float dt = (t1 - t0) / float(steps);
            float t = t0 + dt * hash12(frag + fract(uTime) * 61.0);
            float breathe = 1.0 + 0.03 * uBeat + 0.06 * uVoice;
            for (int i = 0; i < 64; i++) {
                if (i >= steps) break;
                for (int k = 0; k < 2; k++) {
                    if (pending[k] > 0.5 && ringT[k] < t) { acc += T * ringCol[k]; pending[k] = 0.0; }
                }
                vec3 s = ro + rd * t;
                float r = length(s) / breathe;
                if (r < 1.34) {
                    // Slow differential swirl: inner gas turns faster than outer gas.
                    float ang = uTime * (0.06 + 0.22 * uEnergy) * (1.6 - r);
                    float ca = cos(ang), sa = sin(ang);
                    vec3 w3 = vec3(ca * s.x - sa * s.z, s.y, sa * s.x + ca * s.z) / breathe;
                    vec3 wd = w3 / max(length(w3), 1e-4);
                    float tw = uTime * (0.05 + 0.08 * uEnergy);
                    vec2 warp = vec2(fbm(w3 * 1.5 + vec3(0.0, tw, 0.0)), fbm(w3 * 1.5 + vec3(5.2, -tw, 1.3)));
                    float n = fbm(w3 * 2.0 + vec3(warp, warp.x) * 1.5);

                    // Tendrils streaming out of the heart.
                    float sr = fbm(wd * 5.5 + vec3(warp * 0.6, 0.0) + vec3(0.0, 0.0, -r * 0.9 + uTime * (0.04 + 0.12 * uEnergy)));
                    float tendril = pow(smoothstep(0.48, 0.84, sr), 2.0) * (1.0 - smoothstep(0.12, 0.84, r));
                    vec3 em = tendrilC(1.0 - smoothstep(0.0, 0.55, r)) * tendril * (2.0 + 2.5 * uEnergy + 2.0 * uVoice) * (0.45 / (r + 0.16));

                    // The fiery shell: lacy ridges on a wobbling sphere, dark between the filaments.
                    float shellR = 0.86 + (n - 0.5) * 0.16 + 0.04 * uVoice;
                    float band = exp(-pow((r - shellR) / 0.065, 2.0));
                    float ridge = 1.0 - abs(2.0 * fbm(wd * 4.2 + vec3(warp, 0.3) * 0.9) - 1.0);
                    float lace = pow(ridge, 16.0) + 0.35 * pow(1.0 - abs(2.0 * n3(wd * 11.0 + warp.x * 3.0) - 1.0), 12.0);
                    float fil = band * lace;
                    em += fireC(clamp(lace, 0.0, 1.0)) * fil * (4.5 + 4.0 * uEnergy + 3.5 * uVoice + 1.2 * uAwake);

                    float smoke = smoothstep(0.58, 0.86, n) * (1.0 - smoothstep(0.55, 0.95, r));
                    em += pal(vec3(0.85, 0.68, 0.85), vec3(0.75, 0.55, 0.95), vec3(0.55, 0.78, 1.0)) * smoke * 0.10;

                    float wisp = smoothstep(0.46, 0.78, n) * smoothstep(0.55, 0.92, r) * (1.0 - smoothstep(1.05, 1.34, r));
                    em += gasC(n) * wisp * (1.1 + 0.9 * uEnergy + 0.4 * uAwake);

                    em += heartC() * exp(-r * r * 55.0) * (2.4 + 3.0 * uBeat + 2.0 * uVoice + 1.0 * uThink);
                    if (uRipple < 2.5) em += fireC(0.8) * exp(-pow((r - uRipple * 1.6) * 7.0, 2.0)) * exp(-uRipple * 2.2) * 2.5;

                    acc += T * em * dt;
                    T *= exp(-(wisp * 0.9 + fil * 0.6 + smoke * 0.3 + 0.01) * dt * 3.0);
                }
                t += dt;
                if (T < 0.015) break;
            }
        }
        for (int k = 0; k < 2; k++) acc += T * ringCol[k] * pending[k];
    }

    vec3 col = 1.0 - exp(-acc * 1.05);

    // Lens: bloom, an anamorphic streak and soft rays around the heart.
    float fl = 0.55 + 0.9 * uBeat + 0.7 * uVoice + 0.4 * uThink + 0.2 * uAwake;
    vec3 flareC = pal(vec3(0.55, 0.70, 1.00), vec3(1.00, 0.75, 0.62), vec3(0.50, 0.85, 1.00));
    float bloom = 0.45 * exp(-rp * 8.0) + 0.10 * exp(-rp * 2.4);
    float streak = (exp(-abs(p.y) * 55.0) * exp(-abs(p.x) * 0.55) * 0.45
                  + exp(-abs(p.y) * 170.0) * exp(-abs(p.x) * 1.8) * 0.8) * edgeFade;
    float ang = atan(p.y, p.x);
    float rays = pow(n3(vec3(cos(ang) * 7.0, sin(ang) * 7.0, uTime * 0.15)), 5.0) * 3.0 * exp(-rp * 1.8) * smoothstep(0.02, 0.2, rp);
    col += (flareC * (bloom + streak + rays * 0.22) + vec3(1.0) * exp(-rp * 28.0) * 0.8) * fl;

    // Star dust gathered around the core.
    vec2 cell = floor(frag / 4.0);
    vec2 cp = (cell + vec2(hash12(cell + 1.1), hash12(cell + 2.3))) * 4.0;
    float near = exp(-max(rp - 0.9, 0.0) * 1.5) * smoothstep(0.55, 1.0, rp) * edgeFade;
    float spark = step(1.0 - 0.07 * near, hash12(cell + 7.3)) * exp(-dot(frag - cp, frag - cp) * 0.6);
    col += pal(vec3(0.72, 0.76, 1.0), vec3(0.85, 0.72, 1.0), vec3(0.60, 0.86, 1.0)) * spark * (0.45 + 0.35 * sin(uTime * 3.0 + hash12(cell) * 40.0));

    return vec4(min(col, vec3(1.0)), 1.0 - T);
}

void main() {
    vec2 frag = qt_TexCoord0 * uSize;
    vec2 p = vec2(frag.x - uCenter.x, uCenter.y - frag.y) / uCoreR;
    vec2 e = min(frag, uSize - frag) / (0.18 * uSize);
    fragColor = novaCore(frag, p, clamp(min(e.x, e.y), 0.0, 1.0)) * qt_Opacity;
}
