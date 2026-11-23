import { test } from "node:test";
import assert from "node:assert/strict";
import * as N from "./notifications.mjs";

test("cards appear unless Do not disturb is on or the centre is open; critical ones always", () => {
  assert.equal(N.showCard(false, false, false), true);
  assert.equal(N.showCard(false, true, false), false);
  assert.equal(N.showCard(false, false, true), false);
  assert.equal(N.showCard(true, true, true), true);
});

test("a card stays for the app's timeout within reason, else six seconds", () => {
  assert.equal(N.cardTimeout(0), 6000);
  assert.equal(N.cardTimeout(-1), 6000);
  assert.equal(N.cardTimeout(undefined), 6000);
  assert.equal(N.cardTimeout(10), 10000);
  assert.equal(N.cardTimeout(0.5), 2000);
  assert.equal(N.cardTimeout(600), 30000);
});

test("the default action runs on a click; the others are buttons, at most three", () => {
  const actions = [{ identifier: "default", text: "Open" }, { identifier: "reply", text: "Reply" },
                   { identifier: "a", text: "" }, { identifier: "b", text: "B" }, { identifier: "c", text: "C" }, { identifier: "d", text: "D" }];
  assert.equal(N.defaultAction(actions).identifier, "default");
  assert.equal(N.defaultAction([]), null);
  assert.deepEqual(N.buttons(actions).map(a => a.identifier), ["reply", "b", "c"]);
});

test("bodies are plain text: tags removed, entities decoded, nothing interpreted", () => {
  assert.equal(N.plain("<b>New</b> mail from <a href='x'>Ada</a> &amp; Bob"), "New mail from Ada & Bob");
  assert.equal(N.plain(null), "");
  assert.equal(N.appName(""), "An app");
  assert.equal(N.iconSource("mail-unread"), "");
  assert.equal(N.iconSource("/usr/share/x.png"), "file:///usr/share/x.png");
});
