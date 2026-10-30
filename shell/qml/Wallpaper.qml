import QtQuick
import Quickshell
import Quickshell.Wayland

// The deep-space desktop background (approved dark theme, 22 Oct). It is drawn
// once per output and then never repaints, so it costs nothing while idle.
// A picture chosen with "Use as wallpaper" (Images) is kept as
// ~/.config/nexusos/wallpaper and shown instead.
Variants {
    id: wallpapers
    property string picture: "file://" + (Quickshell.env("XDG_CONFIG_HOME") || (Quickshell.env("HOME") + "/.config")) + "/nexusos/wallpaper"
    property int version: 0
    function set(path) {
        Quickshell.execDetached(["sh", "-c", "mkdir -p \"$(dirname \"$2\")\" && ln -sf \"$1\" \"$2\"", "sh", path, picture.slice(7)]);
        reload.restart();
    }
    property Timer reload: Timer { interval: 400; onTriggered: wallpapers.version++ }
    model: Quickshell.screens
    PanelWindow {
        required property var modelData
        screen: modelData
        anchors { top: true; bottom: true; left: true; right: true }
        exclusionMode: ExclusionMode.Ignore
        WlrLayershell.layer: WlrLayer.Background
        WlrLayershell.namespace: "nexus-wallpaper"
        color: "#04050C"

        Image {
            id: picture
            anchors.fill: parent; z: 1
            source: wallpapers.picture + "?v=" + wallpapers.version
            cache: false; asynchronous: true
            fillMode: Image.PreserveAspectCrop
            visible: status === Image.Ready
        }
        Canvas {
            anchors.fill: parent
            renderStrategy: Canvas.Immediate
            onPaint: {
                const ctx = getContext("2d");
                const w = width, h = height;
                const g = ctx.createRadialGradient(w * 0.5, h * 0.45, 0, w * 0.5, h * 0.45, Math.max(w, h) * 0.75);
                g.addColorStop(0, "#0B0F24");
                g.addColorStop(0.55, "#06081A");
                g.addColorStop(1, "#020309");
                ctx.fillStyle = g;
                ctx.fillRect(0, 0, w, h);
                // Deterministic star field: the same sky on every boot.
                let seed = 1234567;
                const rand = () => { seed = (seed * 1664525 + 1013904223) >>> 0; return seed / 4294967296 };
                const count = Math.round(w * h / 2600);
                for (let i = 0; i < count; i++) {
                    const x = rand() * w, y = rand() * h, r = rand() < 0.92 ? 0.6 : 1.2;
                    const a = 0.25 + rand() * 0.55;
                    ctx.fillStyle = `rgba(${200 + rand() * 40}, ${210 + rand() * 30}, 255, ${a})`;
                    ctx.beginPath();
                    ctx.arc(x, y, r, 0, Math.PI * 2);
                    ctx.fill();
                }
            }
        }
    }
}
