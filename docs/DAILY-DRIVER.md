# NexusOS as a daily driver: what it needs to replace Windows

**Goal:** a computer someone can use happily every day instead of Windows:
for home, school, office work, gaming, creative work and software
development, without needing to know it is Linux.

This list is the working plan. It is meant to be long: some items will take
a long time, and some depend on other projects. Work goes through it in
batches (one branch and pull request per batch; a 0.x release after each),
and each item is ticked here when it is built and tested. **1.0.0 only when
the owner agrees it is ready.**

**Status:** ✅ done · 🟡 partly · ⬜ missing · 🔍 not verified (may already work; check first)
**Priority:** **P1** blocks daily use for many people · **P2** expected of a modern OS · **P3** nice to have

---

## 1. Hardware that just works

| Item | P | Status | Notes |
|---|---|---|---|
| Bluetooth turned on (bluez, audio, input devices) | P1 | ✅ | On at start-up, battery levels (0.2) |
| Bluetooth audio codecs (AAC, aptX, LDAC) and headset profiles | P2 | 🔍 | PipeWire picks them with bluez on; check with real headphones |
| Touchpad: tap to click, natural scrolling, two-finger scroll, speed | P1 | ✅ | Settings, Mouse and touchpad (0.2) |
| Touchpad gestures (swipe between workspaces, overview) | P2 | 🟡 | Three-finger swipe between workspaces (0.2); overview gesture to add |
| Mouse: speed, acceleration, left-handed, scroll direction | P1 | ✅ | Settings, Mouse and touchpad (0.2) |
| On-screen indicators for volume, brightness, caps lock, media keys | P1 | 🟡 | Volume, mute, microphone, brightness and media keys (0.2); caps lock to add |
| Keyboard backlight control | P3 | ⬜ | brightnessctl, kbd_backlight |
| Webcam works in apps and browsers (portals, PipeWire camera) | P1 | 🔍 | Test in Firefox and a video-call site |
| Fingerprint login and unlock (fprintd) | P2 | ⬜ | Settings, Accounts; PAM for login, lock, sudo/polkit |
| Touchscreen, screen rotation on convertibles, on-screen keyboard | P3 | ⬜ | iio-sensor-proxy, squeekboard/wvkbd |
| Graphics tablets and pens (Wacom) | P3 | ⬜ | libwacom, Settings |
| Thunderbolt devices and docks authorised (bolt) | P2 | ⬜ | |
| Battery charge limit (80%) for laptop battery health | P2 | ⬜ | Where firmware supports it |
| Hibernate (suspend to disk) | P3 | ⬜ | Needs swap sized to RAM, resume from an encrypted disk |
| Power profiles (performance / balanced / saver) | P1 | ✅ | Settings, Power |
| Sleep, screen off, lid | P1 | ✅ | |
| Multi-monitor, docks, projectors | P1 | ✅ | Settings, Display (with revert) |
| Printers and scanners | P1 | ✅ | Found by themselves |
| Firmware updates | P2 | ✅ | fwupd, Settings, Updates |
| NVIDIA, AMD, Intel graphics | P1 | 🟡 | NVIDIA driver and PRIME offload built; hardware checks outstanding |
| Hardware video decoding (VA-API) in browsers and players | P2 | ⬜ | Battery life and smooth video |
| Supported-hardware list, tested laptops | P1 | ⬜ | Needs real machines |
| Mobile broadband (WWAN), NFC | P3 | ⬜ | ModemManager is present |

## 2. The desktop

| Item | P | Status | Notes |
|---|---|---|---|
| Launcher, windows, workspaces, overview | P1 | ✅ | Nexus shell |
| Taskbar-like view of open apps; Alt+Tab | P1 | 🟡 | Alt+Tab cycles windows (0.2); a visual switcher to add |
| System tray icons (Steam, Discord, Nextcloud, VPN apps) | P1 | ✅ | Top bar, with menus (0.3) |
| Media controls (play/pause/next, now playing) from any app | P2 | ✅ | Media keys (0.2); now playing in the top bar (0.3) |
| Per-app volume and output device | P2 | 🔍 | Settings, Sound |
| Screenshots: whole screen, window, area; Super+Shift+S like Windows | P1 | ✅ | Super+Shift+S, Print, Alt+Print; copied and saved (0.2) |
| Screen recording | P2 | ⬜ | wf-recorder or OBS; portal for apps |
| Clipboard history (Super+V on Windows) | P2 | ✅ | Super+Shift+V (Super+V is push-to-talk) (0.3) |
| Emoji and symbol picker (Super+. on Windows) | P2 | ✅ | Super+. (0.3) |
| Windows-like shortcuts: Super+D show desktop, Super+E files, Ctrl+Shift+Esc task manager, Super+arrows snap | P1 | ✅ | (0.2, 0.3) |
| Keyboard shortcuts page (see and change them) | P2 | ⬜ | |
| Light and dark mode, followed by GTK, Qt and Flatpak apps | P1 | 🔍 | Theme consistency across toolkits |
| Fonts: good defaults, Microsoft-compatible metrics (Carlito, Caladea, Liberation), emoji, CJK | P1 | ✅ | (0.2) |
| Icon theme, cursor theme, sound theme consistent | P2 | 🟡 | |
| Wallpapers and a picker | P3 | 🟡 | |
| Notifications and Do not disturb | P1 | ✅ | |
| Lock screen, login screen | P1 | ✅ | |
| Night light | P2 | ✅ | |
| Desktop widgets (weather, calendar in the top bar) | P3 | ⬜ | |

## 3. Files

| Item | P | Status | Notes |
|---|---|---|---|
| File manager with thumbnails, drives, trash | P1 | ✅ | Thunar |
| USB drives open by themselves; NTFS and exFAT | P1 | ✅ | NTFS and exFAT support (0.2); check on hardware |
| BitLocker drives (read Windows encrypted drives) | P2 | ⬜ | cryptsetup bitlk |
| Network shares: Windows (SMB) and NAS; browse and connect | P1 | ✅ | Settings, Network: type \\\\server\\share or browse (0.3) |
| Share a folder with other computers | P3 | ⬜ | Samba, off by default |
| Cloud drives: OneDrive, Google Drive, Dropbox, Nextcloud | P1 | ⬜ | rclone mounts or GNOME Online Accounts; Nextcloud client |
| Phone integration: files, notifications, texts (KDE Connect) | P2 | ✅ | KDE Connect (0.3) |
| Phone file transfer over USB (MTP), iPhone photos | P2 | 🔍 | libmtp present; ifuse for iPhone |
| Backups | P1 | ✅ | Settings, Backups |
| Search | P1 | ✅ | Super+S |
| Archives (zip, 7z, rar) | P1 | 🟡 | File Roller; check 7z and rar |

## 4. Internet and communication

| Item | P | Status | Notes |
|---|---|---|---|
| Browsers (Firefox, Chromium) | P1 | ✅ | |
| DRM video (Netflix, Spotify web): Widevine | P1 | 🔍 | Firefox downloads it; Chromium needs it built in |
| Password keyring (Secret Service) for browsers and apps | P1 | ✅ | gnome-keyring unlocked at login, follows password changes, Seahorse (0.2) |
| Email, calendar and contacts (Thunderbird), with Microsoft 365 and Google | P1 | ⬜ | Preinstall or offer in Welcome |
| Online accounts (sign in to Google, Microsoft once for mail, calendar, files) | P2 | ⬜ | GNOME Online Accounts |
| Video calls: Teams, Zoom, Meet (camera, mic, screen sharing) | P1 | 🔍 | Screen sharing needs xdg-desktop-portal-hyprland; test |
| Chat apps (WhatsApp, Discord, Slack, Signal, Telegram) | P2 | 🟡 | Flathub; offered in Welcome bundles |
| VPN: WireGuard, OpenVPN, corporate (Cisco AnyConnect, GlobalProtect, Fortinet) | P1 | ✅ | Settings, Network: from a file, or the connection editor (0.3) |
| Work and school Wi-Fi (802.1X, eduroam) in Settings | P1 | ✅ | The connection editor from Settings (0.3) |
| Wi-Fi hotspot | P3 | ⬜ | |
| Proxy settings | P3 | ⬜ | |
| Install a website as an app (PWA) | P2 | ⬜ | Chromium/Firefox web apps in the launcher |

## 5. Office, school and productivity

| Item | P | Status | Notes |
|---|---|---|---|
| Office suite (LibreOffice) | P1 | ✅ | |
| Microsoft Office documents look right (fonts, templates) | P1 | ⬜ | See fonts |
| Microsoft 365 / Google Docs in the browser, as apps | P1 | ⬜ | PWA |
| PDF: read, fill forms, sign, annotate, merge | P1 | 🟡 | Papers reads; add form filling and editing |
| Notes, to-do, calendar apps | P2 | ⬜ | |
| Text recognition (OCR) for scans and screenshots | P3 | ⬜ | Tesseract; local AI |
| Spell checking in many languages | P2 | ⬜ | hunspell dictionaries per chosen language |

## 6. Media and creative

| Item | P | Status | Notes |
|---|---|---|---|
| Video and music players with codecs | P1 | ✅ | VLC |
| Photo library (import from camera/phone, albums) | P2 | ⬜ | Shotwell / digiKam via Flathub |
| Image editing (GIMP, Krita), vector (Inkscape) | P2 | 🟡 | Flathub; Creative bundle |
| Video editing (Kdenlive, DaVinci Resolve) | P2 | 🟡 | Flathub |
| Audio recording and editing; music production (low-latency audio) | P3 | ⬜ | PipeWire JACK, realtime |
| Streaming and recording (OBS) | P2 | 🟡 | Flathub; needs screen-capture portal |
| Spotify and other media apps | P2 | 🟡 | Flathub |

## 7. Gaming

| Item | P | Status | Notes |
|---|---|---|---|
| Steam with Proton (most Windows games) | P1 | 🟡 | Flathub Steam in the Gaming bundle; native `programs.steam` has 32-bit graphics and udev for controllers |
| Epic, GOG (Heroic), other launchers (Lutris) | P2 | 🟡 | Flathub |
| Game controllers (Xbox, PlayStation, Switch), wireless dongles | P1 | ⬜ | udev rules, xpadneo/xone |
| Game mode (performance, notifications quiet) | P2 | ✅ | Shell game mode |
| GameMode / gamescope / MangoHud | P3 | ⬜ | |
| VRR / FreeSync / G-Sync, HDR | P3 | ⬜ | Hyprland supports VRR; HDR is early |
| Anti-cheat games (some never work: tell people which) | P2 | ⬜ | A clear page on what works (ProtonDB) |
| Discord with screen sharing | P2 | 🟡 | Portal |

## 8. Running Windows software

| Item | P | Status | Notes |
|---|---|---|---|
| Open a .exe or .msi: run it with Wine (Bottles), or explain the alternative | P1 | ⬜ | Double-click handler; Nexus suggests the Linux equivalent first |
| Windows in a virtual machine (for apps that need it) | P2 | 🟡 | Virtual machines can be turned on (0.3); a guided Windows set-up to add |
| Program equivalents ("what do I use instead of X") | P1 | 🟡 | Move in matches old programs to apps; extend |
| Dual boot with Windows, with the clocks agreeing | P1 | ✅ | |

## 9. System tools

| Item | P | Status | Notes |
|---|---|---|---|
| Task manager: apps and processes, CPU/memory/disk/GPU, end a frozen app | P1 | 🟡 | Ctrl+Shift+Esc: apps, processes, CPU, memory, End task (0.3); disk and GPU to add |
| Startup apps (what runs at login) | P2 | ⬜ | Settings |
| Disk and partition tools | P2 | ✅ | GNOME Disks |
| System information, health check | P1 | ✅ | About, Health |
| Logs, for when something goes wrong | P3 | 🟡 | Nexus reads the journal |
| Storage cleanup | P1 | ✅ | Settings, Storage |
| Date, time, time zone | P1 | ✅ | |
| Users and passwords | P1 | ✅ | |
| Parental controls, screen time, child accounts | P2 | ⬜ | malcontent; app limits |
| Guest session | P3 | ⬜ | |
| Reset / reinstall without losing files | P2 | ⬜ | Earlier versions cover most cases; a guided reset for the rest |
| Remote help (let a friend see the screen and help) | P2 | ⬜ | RustDesk or a portal-based viewer |

## 10. Security and privacy

| Item | P | Status | Notes |
|---|---|---|---|
| Disk encryption | P1 | ✅ | LUKS2 |
| Unlock the disk with the TPM (no second password) | P2 | ⬜ | systemd-cryptenroll; keep the password as a fallback |
| Secure Boot | P2 | ✅ | Own keys, guided |
| Automatic security updates | P1 | ✅ | Guardian |
| Firewall, with a simple Settings page | P2 | 🟡 | NixOS firewall on; no page |
| Camera, microphone and screen-sharing in use: indicators | P1 | ✅ | Top bar, with the app's name (0.3) |
| App permissions (Flatpak: files, camera, location) | P2 | ⬜ | A Settings page over portals/Flatseal |
| Passkeys and security keys (FIDO2, YubiKey) | P2 | 🔍 | udev rules for U2F |
| Password manager | P2 | 🟡 | Browsers; Bitwarden/KeePassXC on Flathub |
| Malware scanning of downloads (optional) | P3 | ⬜ | ClamAV on demand |

## 11. Software and updates

| Item | P | Status | Notes |
|---|---|---|---|
| App store (Flathub), with updates | P1 | ✅ | Apps window |
| System updates with rollback, channels | P1 | ✅ | |
| AppImage files run by double-click | P2 | ✅ | (0.2) |
| .deb packages (Ubuntu/Debian software) | P2 | ✅ | `apt` in a container |
| Drivers for unusual hardware (Broadcom Wi-Fi, etc.) found and offered | P2 | ⬜ | Hardware scan offers them through Guardian |

## 12. Developer tools

| Item | P | Status | Notes |
|---|---|---|---|
| Terminal, editor (VSCodium), git | P1 | ✅ | (0.2) |
| SSH: keys, agent, connect to servers; SSH server off by default | P1 | ✅ | Settings, Developer; keyring agent (0.3) |
| Docker-compatible containers, `docker` and `docker compose` commands | P1 | ✅ | On rootless Podman, socket and DOCKER_HOST per person (0.2, 0.3) |
| Language toolchains in a project (Python, Node, Rust, Go, Java, .NET, C/C++) | P1 | 🟡 | `nexus-dev init` and direnv (0.3); Nexus setting a project up to add |
| Python and Node global tools that work like elsewhere (pip, npm -g) | P1 | ⬜ | nix-ld so downloaded binaries and pip wheels run |
| Run downloaded Linux programs (nix-ld) | P1 | ✅ | (0.2) |
| JetBrains IDEs, Android Studio | P2 | 🟡 | Flathub; adb and fastboot installed (0.3) |
| Microcontrollers and serial devices (Arduino, ESP32): serial port access | P2 | ✅ | (0.3) |
| Virtual machines (KVM, QEMU, virt-manager) | P2 | ✅ | Settings, Developer, through Guardian (0.3) |
| Kubernetes and cloud CLIs (kubectl, aws, az, gcloud) | P3 | ⬜ | Through project shells |
| Databases for development (PostgreSQL, MySQL, Redis) | P3 | ⬜ | Containers |
| Local AI coding help | P2 | ✅ | Code (Super+C) |
| GitHub/GitLab sign-in for git (credential helper) | P2 | 🟡 | SSH keys (0.3); HTTPS credential helper to add |
| man pages and documentation offline | P3 | 🟡 | |

## 13. Accessibility

| Item | P | Status | Notes |
|---|---|---|---|
| Screen reader | P1 | ✅ | Orca, Super+Alt+S |
| Text size, high contrast, larger pointer | P1 | ✅ | |
| Magnifier (zoom) | P1 | ⬜ | Hyprland cursor zoom |
| Sticky keys, slow keys, bounce keys | P2 | ⬜ | |
| Colour filters for colour blindness | P3 | ⬜ | Screen shader |
| Live captions for any audio (local speech recognition) | P2 | ⬜ | Whisper on device |
| Voice control | P2 | ✅ | |

## 14. Languages

| Item | P | Status | Notes |
|---|---|---|---|
| Language, formats, time zone, keyboard layouts | P1 | ✅ | |
| Whole shell translated (not only Settings' navigation) | P2 | 🟡 | Framework and three languages started |
| Chinese, Japanese, Korean input (fcitx5) | P2 | ⬜ | |
| Right-to-left languages (Arabic, Hebrew) in the shell | P3 | ⬜ | |
| Nexus answers in the person's language | P2 | 🟡 | Follows the language you write in |

## 15. Work and school computers

| Item | P | Status | Notes |
|---|---|---|---|
| Join a company or school directory (Active Directory, Entra ID) | P3 | ⬜ | SSSD |
| Managed settings for an organisation | P3 | ⬜ | |
| Microsoft 365 apps, Teams, OneDrive | P1 | ⬜ | Browser apps and OneDrive sync |

## 16. Reliability and support

| Item | P | Status | Notes |
|---|---|---|---|
| Earlier versions in the boot menu; automatic fallback | P1 | ✅ | |
| Crash notices and reports | P2 | ✅ | |
| User guide and help | P1 | ✅ | Settings, Help |
| Low-memory protection (zram, systemd-oomd) | P1 | ✅ | (0.2) |
| Real-hardware installs, offline, with encryption | P1 | ⬜ | Needs real machines |
| Independent security review | P1 | ⬜ | Needs a reviewer |

---

## Batches

Each batch is one branch and one pull request, so CI runs once per batch. When
its checks pass it is merged and released as the next 0.x version (minor for
new features, patch for fixes only).

| Batch | Contents | Release |
|---|---|---|
| 1 | Bluetooth on; password keyring; touchpad and mouse settings; on-screen indicators and media keys; screenshots and Windows-like shortcuts; fonts; developer and system basics | 0.2.0 |
| 2 | Task manager; system tray; media controls; clipboard history and emoji picker; camera/mic indicators | 0.3.0 (with 3) |
| 3 | Network shares, phone integration, VPN and work Wi-Fi in Settings (cloud drives moved to a later batch) | 0.3.0 |
| 4 | Developer basics: project toolchains, rootless containers, SSH keys, serial boards, VMs (nix-ld came in 0.2) | 0.3.0 |
| 5 | Windows software: .exe handling with Bottles, Windows VM, program equivalents; Steam native, controllers | |
| 6 | Email/calendar and online accounts; cloud drives (OneDrive, Google Drive, Nextcloud); PWAs; PDF forms; spell checking | |
| 7 | Accessibility: magnifier, keyboard aids, colour filters, live captions | |
| 8 | Parental controls, TPM unlock, app permissions, remote help, reset | |
| … | The rest of the list, by priority | |
