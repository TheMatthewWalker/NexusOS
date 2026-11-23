import { test } from "node:test";
import assert from "node:assert/strict";
import * as P from "./privacy.mjs";

const node = props => ({ properties: props });

test("who uses the microphone, camera and screen", () => {
  const u = P.inUse([
    node({ "media.class": "Stream/Input/Audio", "application.name": "Firefox" }),
    node({ "media.class": "Stream/Input/Audio", "application.name": "Firefox" }),
    node({ "media.class": "Stream/Input/Video", "application.name": "Zoom" }),
    node({ "media.class": "Stream/Output/Audio", "application.name": "Spotify" }),
    node({ "media.class": "Audio/Sink", "node.name": "speakers" })
  ]);
  assert.deepEqual(u, { microphone: ["Firefox"], camera: ["Zoom"], screen: false });
  assert.equal(P.describe(u), "Microphone in use by Firefox. Camera in use by Zoom");
  const shared = P.inUse([node({ "media.class": "Video/Source", "node.name": "xdph-streaming-1" }),
                          node({ "media.class": "Stream/Input/Video", "application.name": "Discord" })]);
  assert.deepEqual(shared, { microphone: [], camera: [], screen: true });
  assert.deepEqual(P.inUse([node({ "media.class": "Stream/Input/Audio", "application.name": "nexus-voice" })], "nexus-voice").microphone, []);
  assert.deepEqual(P.inUse(null), { microphone: [], camera: [], screen: false });
});

test("the player to show and what it plays", () => {
  const a = { identity: "Spotify", isPlaying: false, trackTitle: "Song", trackArtist: "Band" };
  const b = { identity: "Firefox", isPlaying: true, trackTitle: "Video", trackArtist: "" };
  assert.equal(P.pick([a, b]), b);
  assert.equal(P.pick([a]), a);
  assert.equal(P.pick([]), null);
  assert.equal(P.nowPlaying(a), "Song · Band");
  assert.equal(P.nowPlaying({ identity: "VLC", trackTitle: "" }), "VLC");
});
