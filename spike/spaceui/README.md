# SpaceUI shell spike

These are throwaway prototypes that compare a native shell with a web shell
for SpaceUI. They are not the product. Results and recommendation:
[docs/design/spaceui-spike.md](../../docs/design/spaceui-spike.md).

- `shared/`: code both shells use unchanged:
  - the plasma shader body;
  - the orbit, emergence and easing maths (`suggest.mjs`, with Node tests);
  - the layout and icons.
  `scripts/spike/build_shared.py` generates the tokens, the shader wrappers
  and the 512 px core asset (git-ignored).
- `native/`: spike A, a Quickshell (QML) shell. `native/shared` is a symlink,
  because Quickshell refuses imports from outside its config directory.
- `web/`: spike B, a WebKitGTK page (`index.html`, `app.mjs`) plus its GTK
  layer-shell host (`host.py`).

Run either shell headlessly with the harness:

```sh
nix develop .#spaceui-spike --command python3 scripts/spike/build_shared.py
nix develop .#spaceui-spike --command python3 scripts/spike/harness.py native out/spike/native
```
