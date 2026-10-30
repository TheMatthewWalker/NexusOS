# Installing NexusOS (alpha)

NexusOS is an alpha. Install it on a spare computer or a virtual machine,
not on the machine you rely on: the installer erases the whole disk you pick.

## What you need

- A 64-bit Intel or AMD computer that boots with **UEFI**. Legacy/CSM boot is not supported.
- At least 8 GB of RAM and a 64 GB disk. A local model also needs 6 GB or more of free disk space. For fast answers, an NVIDIA or AMD graphics card with 8 GB of video memory or more.
- A USB stick of 8 GB or more, or 16 GB if the image carries the local model (step 1). Writing the image erases the stick.
- No internet connection is needed to install: everything is on the stick. Without the model on the stick, the installed system downloads it once it is online.

## 1. Build the image

On any Linux machine with Nix installed and flakes turned on:

```sh
git clone https://github.com/TheMatthewWalker/NexusOS
cd NexusOS
nix run .#lock-model                 # optional: the AI model for computers with a graphics card (about 5 GB)
nix run .#lock-model -- qwen3:4b     # optional: the one for computers without (about 2.5 GB)
nix build .#iso
ls result/iso/            # nexusos-26.05-live-x86_64-linux.iso
```

The build takes a while the first time; most of it is downloaded from the NixOS cache.

`nix run .#lock-model` pins a model by writing `nix/models/<name>-<tag>.json` (for example `qwen3-8b.json`): the list of the model's files with their SHA-256 checksums, from the Ollama registry. The image build then downloads every pinned model and Nix checks each file against that list. Commit the files to keep them. The installer copies only the model it chose for the computer (see "Hardware" below); a model that is not on the image downloads after installation, once the computer is online.

## 2. Write it to a USB stick

Find the stick's device name with `lsblk`. It is usually `/dev/sdX`; check the size to be sure. Then:

```sh
sudo dd if=result/iso/nexusos-*.iso of=/dev/sdX bs=4M status=progress conv=fsync
```

On Windows or macOS, a tool such as balenaEtcher or Rufus (in "DD image" mode) does the same.

## 3. Try it live

Boot the computer from the stick (usually F12, F11, F8 or Esc during start-up opens the boot menu). Choose **NexusOS live**. If the screen stays black on a computer with an NVIDIA card, restart and choose **NexusOS live (NVIDIA)** instead.

The live desktop starts by itself, with a terminal called **Install NexusOS**:

![The live NexusOS desktop with the Install NexusOS terminal](evidence/img/M7-live-installer.png)

- Close that terminal to look around. Nothing in the live session is kept.
- Super+Space opens Nexus.
- The local model does not run in the live session, so Nexus answers only after installation. Everything else works.

## 4. Install

1. Optional: connect to the internet. Hover over the system panel (top right). With Wi-Fi on, it lists nearby networks: click one, type its password and press Enter. A cable or your phone's USB tethering also works. Online, the install can take a little less time; it does not need it.
2. In the **Install NexusOS** terminal, press Enter. If you closed it, press Super+Return (or Ctrl+Alt+T) for a new terminal and run `sudo nexus-install`.
3. The installer asks for:
   - the disk to install to (it lists the disks with their sizes and models);
   - your login name, full name and password (at least 10 characters: it also unlocks the disk and approves system changes);
   - then you type the disk name again to confirm it may be erased.

   It then partitions the disk, writes the configuration and installs NexusOS from the stick. That takes 5–30 minutes, depending on your disk.
4. When it says **NexusOS is installed**, remove the stick and restart.

Each start, NexusOS asks for your password twice: first to unlock the encrypted disk (a text prompt, before anything else runs), then on the login screen.

The disk is encrypted (LUKS2) so a lost or stolen computer shows nothing without the password. If you change your login password later, the disk keeps the old one until you change it too: `sudo cryptsetup luksChangeKey /dev/disk/by-label/nexusos-crypt`. To install without encryption, run `sudo nexus-install --no-encrypt`.

![The installed system's login screen, then the NexusOS desktop](evidence/img/M7-installed-desktop.png)
 If the stick carried the local model, Nexus can answer straight away. Otherwise NexusOS downloads the model in the background once it is online (about 5 GB); until then, Nexus says it is unavailable and apps work as normal.

## Wi-Fi

Open **Settings, Network**: click the Wi-Fi icon in the top bar, the Network planet in the orbital controls (bottom right), or press Super+I. With Wi-Fi on, it lists nearby networks, strongest first, with your current and saved networks at the top.

- Click a network to connect. A secured one opens a password box; press Enter or **Connect**. **Show** reveals what you typed.
- NetworkManager keeps the password, so a saved network reconnects by itself and with one click.
- Click the connected network for **Disconnect** and **Forget** (Forget deletes the saved password).
- Work or school networks that need a username or certificate are not set up from Settings yet: **Open nmtui** on the same page, or run `nmtui` in a terminal (Ctrl+Alt+T).

## Using the desktop

- **Windows** float, like on Windows or macOS: drag the title bar to move, drag an edge to resize, and use the buttons on the left of the title bar to close, minimise or maximise. A minimised window comes back from its planet around the Nexus core at the bottom. **Settings, Desktop** switches to tiling, where windows fill the screen side by side; Super+T switches a single window.
- **Settings** (Super+I) has Network, Bluetooth, Sound, Display, Power, Desktop, Privacy and AI, and About. Type in its search box to find a setting, or to ask Nexus.
- **Shortcuts**: Super+Space Nexus, Super+Return terminal, Super+E files, Super+Q close, Super+M maximise, Super+F fullscreen, Super+1 to 5 workspaces, Super+L lock.

## Teaching Nexus about you

Nexus reads a built-in guide to NexusOS with every question, plus any notes you write. Put text or Markdown files in `~/.config/nexusos/assistant/` (**Settings, Privacy and AI, Open folder**): who you are, what you use the computer for, how you like answers. They are read each time, so a change takes effect on the next question. Nothing is uploaded or used to retrain the model.

Nexus can also do things for you, but only from a fixed list of actions, never by running commands. Looking something up (your computer's state, its health, your files, your apps, today's activity) happens at once. Anything that changes something (opening an app, night light or volume, installing an app from Flathub, an automation, a picture) appears under the answer as a card saying what it will do: nothing happens until you press **Do it**, and changes to your settings can be undone in **Settings, Timeline**. Changes to the system itself (software for every account, services) are sent to Guardian with **Review the change**, and happen only once you approve them there. A cloud model, if you turn one on, is never given your files or your activity.

## Hardware

The installer looks at the computer and sets it up; you do not pick drivers. The summary before the install shows what it found:

```
  Processor: Intel(R) Core(TM) i5-1235U, 16 GB memory, laptop
  Graphics:  Intel Iris Xe Graphics (built-in drivers)
  Local AI:  qwen3:4b, on the processor, from the USB stick
```

| Found | What NexusOS does |
|---|---|
| Intel or AMD built-in graphics | The kernel and Mesa drivers, with video decoding (VA-API) for both makers |
| Intel laptop | Intel's thermal daemon, so the processor does not throttle hard |
| NVIDIA card | Its driver, below |
| AMD card with 6 GB of its own memory or more | The local model runs on it (Vulkan) |
| A graphics card for AI (NVIDIA, or AMD as above) | Local model `qwen3:8b` |
| No such card, 8 GB of memory or more | `qwen3:4b` on the processor: about twice as fast there as the 8B model |
| Less than 8 GB of memory | `qwen3:1.7b` |
| No graphics card for AI and less than 16 GB of memory | Image generation is off (it needs about 8 GB of memory and a 7 GB download) |

What it found is in `/etc/nixos/local.nix` under `nexusos.hardware`. To use another model, run `sudo nexus-install --model qwen3:8b`, or later set `services.nexus-core.ollamaModel` in `local.nix` and rebuild.

## NVIDIA graphics

The installer finds an NVIDIA card and sets up its driver; the summary before the install shows what it found ("Graphics: …"). You do not need to do anything. It sets:

| Computer | What NexusOS uses |
|---|---|
| Laptop with an Intel or AMD built-in GPU as well | PRIME offload: the desktop runs on the built-in GPU, which saves battery. Run an app on the NVIDIA GPU with `nvidia-offload <app>` |
| NVIDIA card only (most desktops, or a laptop with its MUX switch set to the dedicated GPU) | The NVIDIA card runs the desktop |
| GTX 900 or 10 series (older than Turing) | The 580 driver series with NVIDIA's closed kernel modules, the last that supports these cards |

The local model runs on the NVIDIA GPU through Ollama's Vulkan backend. The settings are in `/etc/nixos/local.nix` under `nexusos.nvidia`; to switch the model to CUDA (often faster, but it compiles Ollama on your computer, which takes a long time), add `nexusos.nvidia.ollamaBackend = "cuda";` and rebuild. To install without the NVIDIA driver, run `sudo nexus-install --no-nvidia`.

Building the image (step 1) downloads NVIDIA's driver from nvidia.com and builds it, so the install does not have to.

## In a virtual machine

Windows keeps the Super (Windows) key for itself, so Super shortcuts do not reach NexusOS in a VM or remote desktop on Windows. Open a terminal with **Ctrl+Alt+T** instead, and open Nexus by clicking the core at the bottom of the screen.

## What the installer does

| Step | Detail |
|---|---|
| Disk | A GPT table with a 1 GiB EFI system partition (`BOOT`) and an ext4 root (`nexusos`) inside LUKS2 encryption (`nexusos-crypt`), unlocked with your password |
| Source | The NexusOS source from the stick is copied to `/etc/nixos/nexusos` |
| Configuration | `/etc/nixos/flake.nix` imports NexusOS. `/etc/nixos/local.nix` holds your user name, computer name and time zone. `/etc/nixos/hardware-configuration.nix` is generated for your hardware |
| Boot loader | systemd-boot, keeping the last 20 system versions. Pick an older one at start-up if an update goes wrong |

## Changing the system afterwards

Edit `/etc/nixos/local.nix` (for example `nexusos.autologin = true;`), then run:

```sh
sudo nixos-rebuild switch --flake /etc/nixos#nexus
```

The options are documented in `nix/profiles/nexusos.nix` and `nix/modules/desktop.nix` (under `/etc/nixos/nexusos`).

### Updates

Flathub apps update by themselves every day. The system itself (the kernel, Firefox, Chromium, the desktop) gets security updates through Guardian: once a week, when the computer is online and has at least 10 GiB free, it looks for newer NixOS 26.05 packages, builds and tests them on this computer, and the top bar says "Security updates are ready". The System change window lists what changes (for example Linux 6.18.56 → 6.18.60); approve with your password and restart. The update is tried once at that restart, and if the computer does not start properly it goes back to the previous version by itself. Ask Nexus "check for updates" to look now.

Only after a confirmed restart is `/etc/nixos/flake.lock` updated, so `nixos-rebuild` afterwards builds on the new packages. NexusOS's own files under `/etc/nixos/nexusos` are not changed by updates. To turn the weekly check off, set `nexusos.updates.enable = false;` in `local.nix`.

## Not in this alpha

- Dual boot, and choosing partitions by hand. The installer always uses a whole disk.
- A graphical installer. The installer runs in a terminal.
- A Files app of its own: Thunar is the file manager. System-wide settings (software, services) are changed in `local.nix`.
- Nexus applying settings for you: it explains and points to the right page, and you make the change.

## How the installer is tested

`nix build .#checks.x86_64-linux.nexus-installer` runs the whole install in a UEFI virtual machine without network access. The test:
1. installs onto a blank disk;
2. boots that disk;
3. checks it is NexusOS with the right computer name;
4. checks the user exists with their password set;
5. checks the desktop, Core and Ollama are installed;
6. checks the machine can evaluate its own configuration.
