import QtQuick

// The approved "Nova" core (docs/design/spaceui/studies/nexus-core-nova.html),
// drawn by the shared shader in shared/nova-core.glsl. The item can be larger
// than the core so the lens flare has room; centre and radius place the core.
Item {
    id: core
    property string mode: "idle"         // idle | listening | thinking | speaking
    property real voice: 0               // 0..1, follows the reply being spoken
    property bool reduced: false
    property bool hovered: false
    property point centre: Qt.point(width / 2, height / 2)
    property real radius: Math.min(width, height) / 4
    property vector3d palette: Qt.vector3d(1, 0, 0)   // Nova
    property int steps: 56          // raymarch samples; the small launcher core needs far fewer
    property int idleFps: 0         // 0 = every frame; otherwise the frame rate while idle and not hovered
    signal frame()

    function ripple() { live.ripple = 0 }
    function turn(dx, dy) {
        live.yaw += dx * 2.2 / Math.max(120, radius * 2);
        live.pitch = Math.max(-1.2, Math.min(1.2, live.pitch + dy * 2.2 / Math.max(120, radius * 2)));
    }

    readonly property var targets: ({
        idle:      { energy: 0.22, listen: 0, think: 0, bpm: 54 },
        listening: { energy: 0.45, listen: 1, think: 0, bpm: 72 },
        thinking:  { energy: 0.80, listen: 0, think: 1, bpm: 92 },
        speaking:  { energy: 0.55, listen: 0, think: 0, bpm: 66 }
    })

    QtObject {
        id: live
        property real time: 0
        property real phase: 0
        property real energy: 0.22
        property real listen: 0
        property real think: 0
        property real bpm: 54
        property real voice: 0
        property real awake: 0
        property real yaw: 0.4
        property real pitch: -0.25
        property real ripple: 9
        property real beat: 0.3
        property real pending: 0
    }

    function rotation(yaw, pitch) {
        const cy = Math.cos(yaw), sy = Math.sin(yaw), cp = Math.cos(pitch), sp = Math.sin(pitch);
        return Qt.matrix4x4(cy, sy * sp, sy * cp, 0,
                            0,  cp,      -sp,     0,
                            -sy, cy * sp, cy * cp, 0,
                            0,  0,       0,       1);
    }

    Image {
        id: noise
        source: "shared/nova-noise-atlas.png"
        visible: false
        smooth: true
        mipmap: false
    }
    ShaderEffect {
        id: fx
        anchors.fill: parent
        property variant uNoise: noise
        property matrix4x4 uRot: core.rotation(live.yaw, live.pitch)
        property vector3d uPal: core.palette
        property size uSize: Qt.size(width, height)
        property point uCenter: core.centre
        property real uCoreR: core.radius
        property real uTime: live.time
        property real uEnergy: live.energy
        property real uBeat: live.beat
        property real uVoice: live.voice
        property real uListen: live.listen
        property real uThink: live.think
        property real uRipple: live.ripple
        property real uAwake: live.awake
        property real uSteps: core.steps
        fragmentShader: "nova.frag.qsb"
        blending: true
    }

    // Reduced motion: no animation loop at all; state changes take effect at once.
    onModeChanged: if (core.reduced) snap()
    onVoiceChanged: if (core.reduced) snap()
    Component.onCompleted: if (core.reduced) snap()
    function snap() {
        const t = core.targets[core.mode] || core.targets.idle;
        live.energy = t.energy; live.listen = t.listen; live.think = t.think; live.bpm = t.bpm;
        live.voice = core.voice; live.beat = 0.3; live.ripple = 9;
    }

    FrameAnimation {
        running: core.visible && !core.reduced
        onTriggered: {
            // While idle, a small core can tick slower than the display: it saves
            // power and nobody can tell the difference at launcher size.
            live.pending += frameTime;
            const calm = core.idleFps > 0 && core.mode === "idle" && !core.hovered && live.listen < 0.01;
            if (calm && live.pending < 1 / core.idleFps) return;
            const dt = Math.min(0.05, live.pending);
            live.pending = 0;
            const t = core.targets[core.mode] || core.targets.idle;
            const ease = (a, b, rate) => a + (b - a) * (1 - Math.exp(-rate * dt));
            live.energy = ease(live.energy, t.energy, 3);
            live.listen = ease(live.listen, t.listen, 3);
            live.think = ease(live.think, t.think, 3);
            live.bpm = ease(live.bpm, t.bpm, 3);
            live.voice = ease(live.voice, core.voice, core.voice > live.voice ? 30 : 9);
            live.awake = ease(live.awake, core.hovered ? 1 : 0, 4);
            live.time += dt;
            live.phase += dt * live.bpm / 60;
            live.yaw += dt * (0.12 + 0.25 * live.energy);
            live.ripple += dt;
            const ph = live.phase % 1;
            live.beat = Math.exp(-Math.pow(ph / 0.045, 2)) + 0.55 * Math.exp(-Math.pow((ph - 0.2) / 0.05, 2));
            core.frame();
        }
    }
}
