# Installing NexusOS (alpha)

NexusOS is an alpha. Install it on a spare computer or a virtual machine,
not on the machine you rely on: the installer erases the whole disk you pick.

## What you need

- A 64-bit Intel or AMD computer that boots with **UEFI**. Legacy/CSM boot is not supported.
- At least 8 GB of RAM and a 64 GB disk. A local model also needs 4 GB or more of free disk space.
- A USB stick of 8 GB or more, or 16 GB if the image carries the local model (step 1). Writing the image erases the stick.
- No internet connection is needed to install: everything is on the stick. Without the model on the stick, the installed system downloads it once it is online.

## 1. Build the image

On any Linux machine with Nix installed and flakes turned on:

```sh
git clone https://github.com/TheMatthewWalker/NexusOS
cd NexusOS
nix run .#lock-model      # optional: put the local AI model (about 2 GB) on the image
nix build .#iso
ls result/iso/            # nexusos-26.05-live-x86_64-linux.iso
```

The build takes a while the first time; most of it is downloaded from the NixOS cache.

`nix run .#lock-model` pins the default model, `llama3.2:3b`, by writing `nix/models/llama3.2-3b.json`: the list of the model's files with their SHA-256 checksums, from the Ollama registry. The image build then downloads the model and Nix checks every file against that list. Commit the file to keep it. Without it, the image is about 2 GB smaller and the model downloads after installation.

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
   - your login name, full name and password;
   - then you type the disk name again to confirm it may be erased.

   It then partitions the disk, writes the configuration and installs NexusOS from the stick. That takes 5–30 minutes, depending on your disk.
4. When it says **NexusOS is installed**, remove the stick and restart.

On the first start, NexusOS asks for your password.

![The installed system's login screen, then the NexusOS desktop](evidence/img/M7-installed-desktop.png)
 If the stick carried the local model, Nexus can answer straight away. Otherwise NexusOS downloads the model in the background once it is online (about 2 GB); until then, Nexus says it is unavailable and apps work as normal.

## Wi-Fi

Hover over the system panel (top right). With Wi-Fi on, the quick controls list nearby networks, strongest first, with your current and saved networks at the top.

- Click a network to connect. A secured one opens a password box; press Enter or **Connect**. **Show** reveals what you typed.
- NetworkManager keeps the password, so a saved network reconnects by itself and with one click.
- Click the connected network for **Disconnect** and **Forget** (Forget deletes the saved password).
- Work or school networks that need a username or certificate are not set up from the panel yet: use `nmtui` in a terminal (Ctrl+Alt+T).

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
| Disk | A GPT table with a 1 GiB EFI system partition (`BOOT`) and an ext4 root (`nexusos`) |
| Source | The NexusOS source from the stick is copied to `/etc/nixos/nexusos` |
| Configuration | `/etc/nixos/flake.nix` imports NexusOS. `/etc/nixos/local.nix` holds your user name, computer name and time zone. `/etc/nixos/hardware-configuration.nix` is generated for your hardware |
| Boot loader | systemd-boot, keeping the last 20 system versions. Pick an older one at start-up if an update goes wrong |

## Changing the system afterwards

Edit `/etc/nixos/local.nix` (for example `nexusos.autologin = true;`), then run:

```sh
sudo nixos-rebuild switch --flake /etc/nixos#nexus
```

The options are documented in `nix/profiles/nexusos.nix` and `nix/modules/desktop.nix` (under `/etc/nixos/nexusos`).

## Not in this alpha

- Disk encryption, dual boot, and choosing partitions by hand. The installer always uses a whole disk.
- A graphical installer. The installer runs in a terminal.
- Guardian (AI-proposed system changes) is not switched on in installed systems yet. It needs a builder signing key to be set up on each machine.
- Settings and Files apps of its own. Thunar is the file manager, and settings are changed in `local.nix`.

## How the installer is tested

`nix build .#checks.x86_64-linux.nexus-installer` runs the whole install in a UEFI virtual machine without network access. The test:
1. installs onto a blank disk;
2. boots that disk;
3. checks it is NexusOS with the right computer name;
4. checks the user exists with their password set;
5. checks the desktop, Core and Ollama are installed;
6. checks the machine can evaluate its own configuration.
