## Get NexusOS

The installer image is several GB, more than GitHub allows in one file, so it is published in parts. **Get NexusOS** downloads them, checks each one and joins them into one `.iso` for you:

- **Windows:** download **Get-NexusOS.cmd** below and double-click it. The image is saved in your Downloads folder.
- **Linux or Mac:** in a terminal,
  ```
  curl -fsSL https://raw.githubusercontent.com/TheMatthewWalker/NexusOS/main/scripts/release/get-nexusos.sh | sh
  ```

Then write the `.iso` to a USB stick of 8 GB or more with [Rufus](https://rufus.ie) or [balenaEtcher](https://etcher.balena.io), and start the computer from it. Full steps: [docs/INSTALL.md](https://github.com/TheMatthewWalker/NexusOS/blob/main/docs/INSTALL.md).

To join the parts by hand instead, see README.txt.

Computers already running NexusOS are offered this release by Guardian.
