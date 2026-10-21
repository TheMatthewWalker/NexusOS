import QtQuick

// The Nexus plasma core: the approved asset animated by the shared shader.
// If the shader cannot run, the still asset is shown instead.
Item {
    id: core
    property real energy: 0       // 0 idle .. 1 responding
    property real awake: 0        // hover or focus
    property real envelope: 0     // simulated response level
    property bool reduced: false  // reduced motion freezes the gas
    property bool strands: true
    property real time: 0
    signal frame()

    Behavior on awake { NumberAnimation { duration: 400 } }
    Behavior on energy { NumberAnimation { duration: 500 } }

    Image {
        id: asset
        source: "shared/nexus-plasma-512.png"
        visible: false
        mipmap: true
        smooth: true
    }
    ShaderEffect {
        id: fx
        anchors.fill: parent
        property variant source: asset
        property real u_time: core.time
        property real u_energy: core.energy
        property real u_awake: core.awake
        property real u_envelope: core.envelope
        property real u_strands: core.strands ? 1 : 0
        fragmentShader: "plasma.frag.qsb"
        visible: status !== ShaderEffect.Error
    }
    Image {
        anchors.fill: parent
        source: asset.source
        mipmap: true
        visible: fx.status === ShaderEffect.Error
    }
    FrameAnimation {
        running: !core.reduced && core.visible
        onTriggered: { core.time += frameTime; core.frame() }
    }
}
