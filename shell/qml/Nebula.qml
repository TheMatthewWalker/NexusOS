import QtQuick

// A soft nebula behind the shell's floating surfaces (the Nexus core and
// chat, the orbital controls), so their text stays legible over a bright
// window. A dark cloud in the middle gives the contrast; violet, blue and a
// little amber at its edges give it the shape of gas around a star, and it
// fades to nothing, so there is no hard panel edge. Painted once per size
// (Canvas): it costs nothing while the shell is idle, which matters because
// the local model shares the GPU.
Canvas {
    id: nebula
    property int seed: 7
    // 0..1: how dark the middle gets. Raise it for busier backgrounds.
    property real depth: 0.82
    renderStrategy: Canvas.Cooperative
    onWidthChanged: requestPaint()
    onHeightChanged: requestPaint()
    onDepthChanged: requestPaint()

    function rng(s) {
        let state = s >>> 0;
        return () => { state = (state * 1664525 + 1013904223) >>> 0; return state / 4294967296 };
    }
    function blob(ctx, cx, cy, rx, ry, rgb, alpha) {
        ctx.save();
        ctx.translate(cx, cy);
        ctx.scale(1, ry / rx);
        const g = ctx.createRadialGradient(0, 0, 0, 0, 0, rx);
        g.addColorStop(0, "rgba(" + rgb + "," + alpha + ")");
        g.addColorStop(0.55, "rgba(" + rgb + "," + (alpha * 0.45) + ")");
        g.addColorStop(1, "rgba(" + rgb + ",0)");
        ctx.fillStyle = g;
        ctx.beginPath();
        ctx.arc(0, 0, rx, 0, Math.PI * 2);
        ctx.fill();
        ctx.restore();
    }
    onPaint: {
        const ctx = getContext("2d");
        ctx.reset();
        const w = width, h = height, r = rng(seed);
        // The dark heart, slightly wider than tall.
        blob(ctx, w / 2, h / 2, w * 0.5, h * 0.48, "4,6,18", depth);
        blob(ctx, w / 2, h / 2, w * 0.36, h * 0.34, "6,9,26", depth * 0.6);
        // Coloured wisps around it.
        const hues = ["124,107,255", "76,123,255", "156,143,255", "124,107,255", "214,104,64", "60,90,200"];
        // Each wisp ends inside the canvas, so the cloud has no straight edge.
        for (let i = 0; i < 9; i++) {
            const a = r() * Math.PI * 2, d = 0.14 + r() * 0.16;
            const dx = Math.abs(Math.cos(a)) * d, dy = Math.abs(Math.sin(a)) * d;
            const rx = Math.min(0.12 + r() * 0.16, 0.48 - dx), ry = Math.min(0.1 + r() * 0.14, 0.48 - dy);
            blob(ctx, w / 2 + Math.cos(a) * w * d, h / 2 + Math.sin(a) * h * d,
                 w * rx, h * ry, hues[i % hues.length], 0.12 + r() * 0.12);
        }
        // A few stars caught in the gas.
        for (let i = 0; i < 26; i++) {
            const a = r() * Math.PI * 2, d = Math.sqrt(r()) * 0.46;
            ctx.fillStyle = "rgba(201,212,255," + (0.2 + r() * 0.5) + ")";
            const s = r() < 0.85 ? 1 : 1.6;
            ctx.fillRect(w / 2 + Math.cos(a) * w * d, h / 2 + Math.sin(a) * h * d, s, s);
        }
    }
}
