# nexus-install: put NexusOS on this computer's disk.
#
# Interactive by default. Every question can be answered with a flag instead,
# which is how the VM test and the graphical installer (shell/qml/installer.qml)
# drive it:
#   nexus-install --disk /dev/vdb --user alice --password-file pw.txt --yes
#
# What it does: erases the chosen disk, makes a 1 GiB EFI partition and an
# encrypted ext4 root, writes a small flake in /etc/nixos that imports a copy
# of the NexusOS source in /etc/nixos/nexusos, and runs nixos-install.
# With --alongside it erases nothing: it adds only the root partition, in the
# disk's free space, and reuses the EFI partition already there (Windows').
# The jq and awk programs below are in single quotes on purpose: their $names are theirs.
# shellcheck disable=SC2016
set -euo pipefail

source_dir=/etc/nexusos
disk="" user="" full_name="" password_file="" host="nexus" timezone="" keyboard="" assume_yes=0 extra_module="" nvidia=auto model="" encrypt=1
alongside=0 partition="" progress=0 probe=0 log=""

usage() {
  cat <<'EOF'
Usage: nexus-install [options]
  --disk DEVICE         disk to install to, for example /dev/nvme0n1
  --alongside           keep what is on the disk and install in its free space,
                        reusing its EFI system partition (dual boot with Windows)
  --partition PART      replace one partition (for example /dev/nvme0n1p5): only it
                        is erased; the disk's EFI system partition is shared
  --user NAME           your login name (lower case letters, digits, - and _)
  --full-name "NAME"    shown on the login screen
  --password-file FILE  read the password from FILE (- reads it from standard input)
  --hostname NAME       computer name (default: nexus)
  --timezone ZONE       for example Europe/London (default: detected, else UTC)
  --keyboard LAYOUT     keyboard layout, for example gb or de (default: guessed from
                        the time zone); it also types the password that unlocks the disk
  --source DIR          NexusOS source to install (default: /etc/nexusos)
  --extra-module FILE   also include this NixOS module (used by the VM test)
  --no-nvidia           do not set up the NVIDIA driver, even if a card is found
  --model NAME:TAG      the local AI model (default: chosen for this computer)
  --no-encrypt          do not encrypt the disk (by default your password unlocks it at start-up)
  --yes                 do not ask for confirmation before changing the disk
  --progress-json       print one JSON object per step on standard output, for
                        the graphical installer; everything else goes to the log
  --log FILE            where everything is logged (default: /tmp/nexus-install.log)
  --probe-json          print the disks, what could go alongside on each and the
                        hardware scan as one JSON object; change nothing
EOF
}

# The graphical installer reads standard output, so with --progress-json or
# --probe-json it carries only JSON (on file descriptor 3); people's messages
# go to standard error, and then to the log.
json=0 reported=0 last_cmd=""
for arg in "$@"; do case "$arg" in --progress-json|--probe-json) exec 3>&1 1>&2; json=1; break ;; esac; done
emit() { [ "$json" -eq 0 ] || jq -cn "$@" >&3; }
log_tail() {  # the log's last lines, as a JSON list
  if [ -n "$log" ] && [ -r "$log" ]; then tail -n 15 "$log" | jq -Rsc 'split("\n") | map(select(. != ""))'; else echo "[]"; fi
}
fail() {
  printf '\nnexus-install: %s\n' "$*" >&2
  [ -z "$log" ] || printf 'The full log is in %s\n' "$log" >&2
  if [ "$json" -eq 1 ] && [ "$reported" -eq 0 ]; then
    reported=1
    emit --arg m "$*" --arg log "$log" --argjson tail "$(log_tail)" \
      '{event: "error", message: $m, tail: $tail} + (if $log == "" then {} else {log: $log} end)'
  fi
  exit 1
}
usage_fail() { printf 'nexus-install: %s\n' "$*" >&2; usage >&2; reported=1
  emit --arg m "$*" '{event: "error", message: $m}'; exit 2; }

seen=" "
while [ $# -gt 0 ]; do
  case "$1" in
    --disk|--partition|--user|--full-name|--password-file|--hostname|--timezone|--keyboard|--source|--extra-module|--model|--log)
      [ $# -ge 2 ] || usage_fail "$1 needs a value"
      # A flag given twice is ambiguous (which disk?): refuse it.
      case "$seen" in *" $1 "*) usage_fail "$1 was given twice" ;; esac
      seen="$seen$1 " ;;
  esac
  case "$1" in
    --disk) disk="$2"; shift 2 ;;
    --alongside) alongside=1; shift ;;
    --partition) partition="$2"; shift 2 ;;
    --user) user="$2"; shift 2 ;;
    --full-name) full_name="$2"; shift 2 ;;
    --password-file) password_file="$2"; shift 2 ;;
    --hostname) host="$2"; shift 2 ;;
    --timezone) timezone="$2"; shift 2 ;;
    --keyboard) keyboard="$2"; shift 2 ;;
    --source) source_dir="$2"; shift 2 ;;
    --extra-module) extra_module="$2"; shift 2 ;;
    --no-nvidia) nvidia=off; shift ;;
    --model) model="$2"; shift 2 ;;
    --no-encrypt) encrypt=0; shift ;;
    --yes) assume_yes=1; shift ;;
    --progress-json) progress=1; shift ;;
    --probe-json) probe=1; shift ;;
    --log) log="$2"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) usage_fail "unknown option: $1" ;;
  esac
done

# Combinations that would leave a question unanswered, or mean two things.
if [ "$probe" -eq 1 ] && [ "$progress" -eq 1 ]; then usage_fail "--probe-json and --progress-json do not go together"; fi
if [ -n "$partition" ] && [ "$alongside" -eq 1 ]; then usage_fail "--partition and --alongside do not go together: replace a partition, or use free space"; fi
if [ "$alongside" -eq 1 ] && [ -z "$disk" ]; then usage_fail "--alongside needs --disk: say which disk to install beside"; fi
if [ "$progress" -eq 1 ]; then
  [ -n "$disk$partition" ] || usage_fail "--progress-json needs --disk or --partition, --user and --password-file (it asks no questions)"
  for need in user password_file; do
    [ -n "${!need}" ] || usage_fail "--progress-json needs --disk or --partition, --user and --password-file (it asks no questions)"
  done
  [ "$assume_yes" -eq 1 ] || usage_fail "--progress-json needs --yes (it asks no questions)"
fi
[ -z "$log" ] || [ "$probe" -eq 0 ] || usage_fail "--probe-json changes nothing and keeps no log"
json=$((progress | probe))

# Everything the installer prints also goes to a log, so a failure can be
# reported in full; the installed system keeps a copy in /var/log. With
# --progress-json only the log gets it (standard output is the JSON), and the
# log is readable by the person, so the graphical installer can show it.
if [ "$probe" -eq 0 ]; then
  log="${log:-/tmp/nexus-install.log}"
  if { : >> "$log"; } 2>/dev/null; then
    [ ! -f "$log" ] || [ -L "$log" ] || chmod 644 "$log" 2>/dev/null || true
    if [ "$progress" -eq 1 ]; then exec 1>>"$log" 2>&1; else exec > >(tee -a "$log") 2>&1; fi
    echo "nexus-install started $(date -Is)" >> "$log"
  else
    log=""
  fi
fi

# --- Progress ---------------------------------------------------------------
# With --progress-json every step is one line, for example
#   {"event":"step","id":"install","n":4,"of":6,"title":"Installing NexusOS"}
# then {"event":"detail","text":"..."} lines from nixos-install, and at the end
# {"event":"done"} or {"event":"error","message":"...","log":"...","tail":[...]}.
step_n=0 step_of=6
say() { printf '\n\033[1m%s\033[0m\n' "$*"; }
step() {  # step ID TITLE
  step_n=$((step_n + 1))
  emit --arg id "$1" --arg t "$2" --argjson n "$step_n" --argjson of "$step_of" '{event: "step", id: $id, n: $n, of: $of, title: $t}'
  say "$2"
}
# A command that fails stops the install (set -e); say which, with the log's last lines.
# Installing alongside, a failed attempt also removes the partition it added,
# so the disk is as it was and trying again finds the same free space.
added_part=""
on_exit() {
  local rc=$?
  [ "$rc" -ne 0 ] || return 0
  if [ -n "$added_part" ]; then
    echo "Removing the partition this attempt added ($added_part)."
    umount -R /mnt 2>/dev/null || true
    cryptsetup close nexusos-root 2>/dev/null || true
    sfdisk --delete "$disk" "${added_part##*[!0-9]}" > /dev/null 2>&1 && partx -u "$disk" 2>/dev/null || true
  fi
  [ "$json" -eq 1 ] && [ "$reported" -eq 0 ] || return 0
  reported=1
  emit --arg m "Installing stopped: \`${last_cmd:-a step}\` failed (exit $rc)." --arg log "$log" --argjson tail "$(log_tail)" \
    '{event: "error", message: $m, tail: $tail} + (if $log == "" then {} else {log: $log} end)'
}
trap 'last_cmd=$BASH_COMMAND; printf "\nnexus-install stopped: \"%s\" failed (line %s).%s\n" "$BASH_COMMAND" "$LINENO" "${log:+ The full log is in $log}" >&2' ERR
trap on_exit EXIT
# --- end of progress

[ "$(id -u)" -eq 0 ] || fail "run me as root: sudo nexus-install"
[ -d /sys/firmware/efi ] || fail "this computer booted without UEFI. NexusOS needs UEFI: turn off legacy/CSM boot in the firmware settings and boot the installer again."
[ -f "$source_dir/flake.nix" ] || fail "no NexusOS source in $source_dir"

# --- Hardware ---------------------------------------------------------------
# Found here, written to local.nix, acted on by nix/modules/hardware.nix and
# nix/modules/nvidia.nix: nobody picks drivers by hand.
#
# An NVIDIA card needs its own driver. Laptops that also have a built-in Intel
# or AMD GPU use PRIME offload, which needs both cards' PCI bus IDs.
# lspci -Dnn lines look like:
#   0000:01:00.0 VGA compatible controller [0300]: NVIDIA Corporation AD107M [GeForce RTX 4060 Max-Q / Mobile] [10de:28a0] (rev a1)
nvidia_lines="" nvidia_bus="" intel_bus="" amd_bus="" nvidia_name="" nvidia_open=true nvidia_driver=stable
bus_id() {  # 0000:01:00.0 -> PCI:1:0:0 (decimal, as NixOS expects)
  local slot="${1#*:}" bus dev fn
  bus="${slot%%:*}"; dev="${slot#*:}"; fn="${dev#*.}"; dev="${dev%.*}"
  printf 'PCI:%d:%d:%d' "$((16#$bus))" "$((16#$dev))" "$((16#$fn))"
}
# Where the scan reads from; the tests (tests/installer) point these at recordings.
proc="${NEXUS_PROC:-/proc}" sys="${NEXUS_SYS:-/sys}"
gpu_name() { sed -E 's/^[^ ]+ [^:]+: (.*) \[[0-9a-f]{4}:[0-9a-f]{4}\].*/\1/' <<<"$1"; }
gpus="$(lspci -Dnn 2>/dev/null | grep -E '\[(0300|0302|0380)\]' || true)"
intel="$(grep -E '\[8086:[0-9a-f]{4}\]' <<<"$gpus" | head -n1 || true)"
amd="$(grep -E '\[1002:[0-9a-f]{4}\]' <<<"$gpus" | head -n1 || true)"
if [ "$nvidia" = auto ]; then
  nvidia_lines="$(grep -E '\[10de:[0-9a-f]{4}\]' <<<"$gpus" || true)"
  if [ -n "$nvidia_lines" ]; then
    first="$(head -n1 <<<"$nvidia_lines")"
    nvidia_bus="$(bus_id "${first%% *}")"
    nvidia_name="$(sed -E 's/.*\[([^]]*)\] \[10de:[0-9a-f]{4}\].*/\1/' <<<"$first")"
    device="$(grep -oE '\[10de:[0-9a-f]{4}\]' <<<"$first" | cut -c7-10)"
    # NVIDIA's open modules and current driver need Turing (device 0x1e00) or newer.
    if [ "$((16#$device))" -lt "$((16#1e00))" ]; then nvidia_open=false nvidia_driver=legacy_580; fi
    [ -n "$intel" ] && intel_bus="$(bus_id "${intel%% *}")"
    [ -n "$amd" ] && amd_bus="$(bus_id "${amd%% *}")"
  fi
fi
# An AMD GPU's own memory (amdgpu reports it; built-in ones show a small carve-out).
amd_vram_gib=0
for f in "$sys"/class/drm/card*/device/mem_info_vram_total; do
  [ -r "$f" ] || continue
  grep -q 0x1002 "$(dirname "$f")/vendor" 2>/dev/null || continue
  v=$(( $(cat "$f") / 1073741824 )); [ "$v" -gt "$amd_vram_gib" ] && amd_vram_gib=$v
done
if [ -n "$nvidia_lines" ]; then gpu=nvidia
elif [ -n "$amd" ]; then gpu=amd
elif [ -n "$intel" ]; then gpu=intel
else gpu=none
fi
case "$(grep -m1 '^vendor_id' "$proc/cpuinfo" 2>/dev/null | awk '{print $3}')" in
  GenuineIntel) cpu=intel ;; AuthenticAMD) cpu=amd ;; *) cpu=other ;;
esac
cpu_name="$(grep -m1 '^model name' "$proc/cpuinfo" 2>/dev/null | cut -d: -f2- | sed 's/^ *//')"
memory_gib=$(( ($(awk '/^MemTotal:/ {print $2}' "$proc/meminfo") + 524288) / 1048576 ))
# A battery, or a portable chassis type (SMBIOS: 8-10 portable/laptop/notebook,
# 14 sub-notebook, 31 convertible, 32 detachable).
laptop=false
if ls "$sys"/class/power_supply/BAT* > /dev/null 2>&1; then laptop=true
else
  case "$(cat "$sys/class/dmi/id/chassis_type" 2>/dev/null)" in 8|9|10|14|31|32) laptop=true ;; esac
fi
# The model nix/modules/hardware.nix chooses for this computer (kept in step with it).
if [ "$gpu" = nvidia ] || { [ "$gpu" = amd ] && [ "$amd_vram_gib" -ge 6 ]; }; then ai_model="qwen3:8b" ai_where="on the graphics card"
elif [ "$memory_gib" -ge 8 ]; then ai_model="qwen3:4b" ai_where="on the processor"
else ai_model="qwen3:1.7b" ai_where="on the processor"
fi
if [ -n "$model" ]; then
  [[ "$model" =~ ^[a-z0-9][a-z0-9._-]*:[a-z0-9._-]+$ ]] || fail "--model takes a library model as name:tag, for example qwen3:4b"
  ai_model="$model" ai_where="as asked"
fi
models_dir="${NEXUS_MODELS:-/etc/nexusos-model}/manifests/registry.ollama.ai/library"
ai_manifest="$models_dir/${ai_model%%:*}/${ai_model#*:}"
if [ -f "$ai_manifest" ]; then ai_where="$ai_where, from the USB stick"
else ai_where="$ai_where, downloaded once the computer is online"
fi

if [ -n "$nvidia_lines" ] && [ -n "$intel_bus$amd_bus" ]; then graphics="$nvidia_name, with the built-in GPU running the desktop (PRIME offload)"
elif [ -n "$nvidia_lines" ]; then graphics="$nvidia_name"
elif [ -n "$amd" ]; then graphics="$(gpu_name "$amd") (built-in drivers)"
elif [ -n "$intel" ]; then graphics="$(gpu_name "$intel") (built-in drivers)"
else graphics="basic display (no graphics processor found)"
fi
[ "$nvidia" = off ] && [ -n "$(grep -E '\[10de:' <<<"$gpus" || true)" ] && graphics="$graphics; NVIDIA card left without its driver (--no-nvidia)"
# The summary's lines, shown here and by the graphical installer.
processor_line="${cpu_name:-unknown}, ${memory_gib} GB memory"
[ "$laptop" = false ] || processor_line="$processor_line, laptop"
ai_line="$ai_model, $ai_where"
# The large mixture-of-experts model (nix/modules/large-model.nix): with 16 GB
# of memory (reported as 15), downloaded after installation in the biggest
# version this computer can hold. With a graphics card for AI it becomes the
# model Nexus answers with; otherwise it is a switch in Settings.
large_model=""
if [ "$memory_gib" -ge 15 ]; then
  if [ "$gpu" = nvidia ] || { [ "$gpu" = amd ] && [ "$amd_vram_gib" -ge 6 ]; }; then
    large_model="GPT-OSS 20B Heretic, downloaded once online, then used: experts in memory, the rest on the graphics card"
  else
    large_model="GPT-OSS 20B Heretic, downloaded once online; switch to it in Settings, Privacy and AI"
  fi
fi
# --- end of hardware scan

# --- Alongside planning -----------------------------------------------------
# Can NexusOS go into a disk's free space, beside the system already there?
# This only looks: at the partition table (sfdisk -J), the free areas
# (parted -m), what each partition holds (lsblk -J) and the free space on the
# EFI system partition. It never shrinks or moves a partition: making room is
# done in Windows, which knows its own files. The tests (tests/installer)
# replace these commands with recordings from real disks.
min_root_bytes=$((32 * 1073741824))     # NexusOS, a local model and room to update
min_esp_free=$((200 * 1048576))         # a few kernels and their initrds
esp_type=c12a7328-f81f-11d2-ba4b-00a0c93ec93b
msr_type=e3c9e316-0b5c-4db8-817d-f92df00215ae
# Sizes as Windows' Disk Management shows them (1 GB = 1024 MB), so the
# numbers match the ones people use there to make room.
gb() { local t=$(( ($1 * 10 + 536870912) / 1073741824 )); if [ "$t" -ge 100 ]; then echo "$(( (t + 5) / 10 )) GB"; else echo "$((t / 10)).$((t % 10)) GB"; fi; }
mb() { echo "$(( $1 / 1048576 )) MB"; }

esp_free_bytes() {  # free bytes on a FAT partition, mounted read-only to look if need be
  local part="$1" dir where free
  where="$(findmnt -rno TARGET -S "$part" 2>/dev/null | head -n1 || true)"
  if [ -n "$where" ]; then df -B1 --output=avail "$where" | tail -n1 | tr -d ' '; return; fi
  dir="$(mktemp -d)"
  if ! mount -t vfat -o ro "$part" "$dir" 2>/dev/null; then rmdir "$dir"; echo -1; return; fi
  free="$(df -B1 --output=avail "$dir" | tail -n1 | tr -d ' ')"
  umount "$dir"; rmdir "$dir"
  echo "$free"
}

# plan_alongside DISK sets plan_ok (1 when it can be done), plan_reason (a
# sentence for people), plan_esp, plan_esp_free, plan_start and plan_size (in
# sectors, aligned to 1 MiB), plan_free (bytes), plan_systems and plan_warnings.
plan_alongside() {
  local d="$1" table label sector first last parts esps start end s e best_s=0 best_len=0 largest=0
  plan_ok=0 plan_reason="" plan_esp="" plan_esp_free=0 plan_start=0 plan_size=0 plan_free=0 plan_systems="" plan_warnings=""
  if ! table="$(sfdisk -J "$d" 2>/dev/null)" || [ -z "$table" ] || [ "$(jq '.partitiontable.partitions // [] | length' <<<"$table")" -eq 0 ]; then
    plan_reason="This disk has no partitions, so there is nothing to install alongside. Erase it instead."; return 0
  fi
  label="$(jq -r '.partitiontable.label' <<<"$table")"
  if [ "$label" = dos ]; then
    plan_reason="This disk uses the older MBR layout (the system on it starts in legacy BIOS mode). NexusOS starts with UEFI, so it cannot share this disk: erase it, or install on another disk."; return 0
  fi
  [ "$label" = gpt ] || { plan_reason="This disk's partition table ($label) is not one NexusOS can install beside."; return 0; }
  sector="$(jq '.partitiontable.sectorsize // 512' <<<"$table")"
  first="$(jq '.partitiontable.firstlba // 34' <<<"$table")"
  last="$(jq '.partitiontable.lastlba' <<<"$table")"
  # One line per partition: node, type GUID, name, file system.
  parts="$(jq -r --argjson blk "$(lsblk -J -o PATH,FSTYPE "$d")" '
    ([$blk | .. | objects | select(has("path")) | {(.path): (.fstype // "")}] | add // {}) as $fs
    | .partitiontable.partitions[] | [.node, (.type | ascii_downcase), (.name // ""), ($fs[.node] // "")] | @tsv' <<<"$table")"
  # any CONDITION: does a partition match (awk, on those fields)?
  any() { awk -F'\t' -v msr="$msr_type" "$1 { found = 1 } END { exit !found }" <<<"$parts"; }
  # What is there now.
  if any '$2 == msr || $4 == "ntfs" || $4 == "BitLocker"'; then plan_systems="Windows"; fi
  if any '$2 == "0fc63daf-8483-4772-8e79-3d69d8477de4" || $2 == "4f68bce3-e8cd-4db1-96e7-fbcaf984b709"'; then
    plan_systems="${plan_systems:+$plan_systems and }another Linux system"
  fi
  [ -n "$plan_systems" ] || plan_systems="the system already there"
  if any '$4 == "BitLocker"'; then
    plan_warnings="Windows uses BitLocker on this disk. Have your BitLocker recovery key ready (aka.ms/myrecoverykey): Windows may ask for it once after NexusOS adds its boot menu."
  fi
  if any '$3 == "nexusos"'; then
    plan_reason="This disk already has a NexusOS partition, from an earlier install. Delete it in Windows (Disk Management, Delete volume) to start again, or erase the disk."; return 0
  fi
  mapfile -t esps < <(awk -F'\t' -v t="$esp_type" '$2 == t { print $1 }' <<<"$parts")
  if [ "${#esps[@]}" -eq 0 ]; then
    plan_reason="This disk has no EFI system partition, so nothing on it starts with UEFI. Erase it, or install on another disk."; return 0
  fi
  if [ "${#esps[@]}" -gt 1 ]; then
    plan_reason="This disk has ${#esps[@]} EFI system partitions, so it is not clear which one starts the computer. Erase it, or install on another disk."; return 0
  fi
  plan_esp="${esps[0]}"
  # The free areas, aligned to 1 MiB and kept inside the usable sectors. parted -m lines:
  #   1:300000000000B:512110173695B:212110173696B:free;
  while read -r start end; do
    s=$(( (start + 1048575) / 1048576 * 1048576 )); e=$(( (end + 1) / 1048576 * 1048576 ))
    [ "$s" -ge $((first * sector)) ] || s=$(( (first * sector + 1048575) / 1048576 * 1048576 ))
    [ "$e" -le $(((last + 1) * sector)) ] || e=$(( (last + 1) * sector / 1048576 * 1048576 ))
    if [ $((e - s)) -gt "$best_len" ]; then best_s=$s best_len=$((e - s)); fi
  done < <(parted -m -s "$d" unit B print free | awk -F: '{ sub(/;$/, "") } $5 == "free" { sub(/B$/, "", $2); sub(/B$/, "", $3); print $2, $3 }')
  largest=$best_len
  if [ "$largest" -lt "$min_root_bytes" ]; then
    if [ "$largest" -lt 1073741824 ]; then plan_reason="There is no free space on this disk."
    else plan_reason="The largest free space on this disk is $(gb "$largest"); NexusOS needs at least $(gb "$min_root_bytes")."; fi
    plan_reason="$plan_reason Make room in Windows first (Disk Management, Shrink volume), then start the installer again. The installer never shrinks partitions itself."
    return 0
  fi
  if [ "$(awk -F'\t' -v p="$plan_esp" '$1 == p { print $4 }' <<<"$parts")" != vfat ]; then
    plan_reason="The EFI system partition ($plan_esp) is not FAT formatted, so NexusOS cannot add its boot files to it."; return 0
  fi
  plan_esp_free="$(esp_free_bytes "$plan_esp")"
  if [ "$plan_esp_free" -lt 0 ]; then
    plan_reason="The EFI system partition ($plan_esp) could not be opened to check its free space."; return 0
  fi
  if [ "$plan_esp_free" -lt "$min_esp_free" ]; then
    plan_reason="The EFI system partition ($plan_esp) has $(mb "$plan_esp_free") free; NexusOS needs $(mb "$min_esp_free") there for its start-up files. The installer does not resize it: erase the disk, or install on another disk."
    return 0
  fi
  plan_ok=1 plan_start=$((best_s / sector)) plan_size=$((best_len / sector)) plan_free=$best_len
  plan_reason="NexusOS goes in the $(gb "$best_len") of free space, beside $plan_systems; nothing else on the disk changes. It shares the EFI system partition ($plan_esp, not formatted), and its start-up menu (systemd-boot) finds Windows Boot Manager by itself."
}

# Does an NTFS partition hold Windows itself (C:)? yes, no, or unknown (it could
# not be opened read-only, often because Windows' Fast startup left it locked).
holds_windows() {
  local dir answer=unknown
  dir="$(mktemp -d)"
  if mount -t ntfs3 -o ro "$1" "$dir" 2>/dev/null || mount -t ntfs -o ro "$1" "$dir" 2>/dev/null; then
    if [ -d "$dir/Windows/System32" ]; then answer=yes; else answer=no; fi
    umount "$dir"
  fi
  rmdir "$dir"
  echo "$answer"
}

# plan_partitions DISK prints, as JSON, whether NexusOS can replace one of the
# disk's partitions ({possible, reason, esp, partitions: [{path, size, fstype,
# label, what, possible, reason, warning}]}). Only that partition is formatted;
# the disk's EFI system partition is shared, as alongside. Refused: the EFI
# and Microsoft reserved partitions, Windows' recovery partition, anything in
# use, anything under 32 GB, and Windows itself (erase the disk to remove it).
plan_partitions() {
  local d="$1" table label sector parts esps esp esp_free rows="" node type name fs size plabel mounted what ok why warn
  local no='{"possible": false, "esp": "", "partitions": []}'
  if ! table="$(sfdisk -J "$d" 2>/dev/null)" || [ -z "$table" ] || [ "$(jq '.partitiontable.partitions // [] | length' <<<"$table")" -eq 0 ]; then
    jq -c --arg r "This disk has no partitions to replace. Erase it instead." '. + {reason: $r}' <<<"$no"; return 0
  fi
  label="$(jq -r '.partitiontable.label' <<<"$table")"
  if [ "$label" != gpt ]; then
    jq -c --arg r "This disk does not use the GPT layout NexusOS starts from (UEFI). Erase it, or use another disk." '. + {reason: $r}' <<<"$no"; return 0
  fi
  sector="$(jq '.partitiontable.sectorsize // 512' <<<"$table")"
  # One line per partition: node, type GUID, name, size in bytes, file system, label, mounted (1/0).
  parts="$(jq -r --argjson s "$sector" --argjson blk "$(lsblk -J -o PATH,FSTYPE,LABEL,MOUNTPOINTS "$d")" '
    ([$blk | .. | objects | select(has("path")) | {(.path): .}] | add // {}) as $b
    | .partitiontable.partitions[]
    | [.node, (.type | ascii_downcase), (.name // ""), (.size * $s), ($b[.node].fstype // ""), ($b[.node].label // ""),
       (if ([($b[.node].mountpoints // [])[] | select(. != null and . != "")] | length) > 0 then 1 else 0 end)] | @tsv' <<<"$table")"
  mapfile -t esps < <(awk -F'\t' -v t="$esp_type" '$2 == t { print $1 }' <<<"$parts")
  if [ "${#esps[@]}" -ne 1 ]; then
    jq -c --arg r "This disk has ${#esps[@]} EFI system partitions; NexusOS needs exactly one to share. Erase it, or use another disk." '. + {reason: $r}' <<<"$no"; return 0
  fi
  esp="${esps[0]}"
  if [ "$(awk -F'\t' -v p="$esp" '$1 == p { print $5 }' <<<"$parts")" != vfat ]; then
    jq -c --arg r "The EFI system partition ($esp) is not FAT formatted." '. + {reason: $r}' <<<"$no"; return 0
  fi
  esp_free="$(esp_free_bytes "$esp")"
  if [ "$esp_free" -lt "$min_esp_free" ]; then
    jq -c --arg r "The EFI system partition ($esp) has $(mb "$((esp_free < 0 ? 0 : esp_free))") free; NexusOS needs $(mb "$min_esp_free") there. Erase the disk, or use another disk." \
      '. + {reason: $r}' <<<"$no"; return 0
  fi
  # Read with a separator that is not white space, so an empty field (a partition with no name) stays a field.
  while IFS=$'\037' read -r node type name size fs plabel mounted; do
    [ "$type" != "$esp_type" ] && [ "$type" != "$msr_type" ] || continue
    ok=1 why="" warn=""
    case "$fs" in
      ntfs) what="Windows (NTFS)" ;; BitLocker) what="Windows, encrypted with BitLocker" ;;
      ext4|ext3|ext2|btrfs|xfs|f2fs) what="Linux ($fs)" ;; crypto_LUKS) what="Linux, encrypted" ;;
      vfat|exfat) what="Shared files ($fs)" ;; swap) what="Linux swap" ;; "") what="Empty or unknown" ;; *) what="$fs" ;;
    esac
    [ "$name" != nexusos ] || what="NexusOS, from an earlier install"
    if [ "$mounted" = 1 ]; then ok=0 why="It is in use (the installer runs from it, or it is mounted)."
    elif [ "$type" = de94bba4-06d1-4d40-a16a-bfd50179d6ac ]; then ok=0 why="This is Windows' recovery partition."
    elif [ "$size" -lt "$min_root_bytes" ]; then ok=0 why="It is $(gb "$size"); NexusOS needs at least $(gb "$min_root_bytes")."
    elif [ "$fs" = ntfs ]; then
      case "$(holds_windows "$node")" in
        yes) ok=0 why="This is Windows itself (C:). To replace Windows, erase the whole disk instead." ;;
        unknown) ok=0 why="The installer could not look inside it to check it is not Windows itself (Windows' Fast startup can lock it). Turn off Fast startup in Windows, or erase the disk." ;;
        *) warn="Everything on it is deleted: copy what you need from it first." ;;
      esac
    elif [ "$fs" = BitLocker ]; then
      ok=0 why="It is encrypted with BitLocker, so the installer cannot check it is not Windows itself. Remove it in Windows (Disk Management, Delete volume) to install in its space alongside Windows."
    else
      warn="Everything on it is deleted."
    fi
    rows+="$(jq -cn --arg path "$node" --argjson size "$size" --arg fstype "$fs" --arg label "${plabel:-$name}" --arg what "$what" \
      --argjson ok "$ok" --arg why "$why" --arg warn "$warn" \
      '{path: $path, size: $size, fstype: $fstype, label: $label, what: $what, possible: ($ok == 1), reason: $why, warning: $warn}')"$'\n'
  done < <(tr '\t' '\037' <<<"$parts")
  jq -sc --arg esp "$esp" '{possible: (map(select(.possible)) | length > 0), esp: $esp, partitions: .,
    reason: (if map(select(.possible)) | length > 0 then "" else "No partition on this disk can be replaced." end)}' <<<"$rows"
}
plan_json() {  # the plan for DISK as JSON, after plan_alongside
  jq -cn --arg disk "$1" --argjson ok "$plan_ok" --arg reason "$plan_reason" --arg esp "$plan_esp" \
    --argjson espFree "$plan_esp_free" --argjson free "$plan_free" --arg systems "$plan_systems" --arg warnings "$plan_warnings" \
    '{disk: $disk, possible: ($ok == 1), reason: $reason, esp: $esp, espFreeBytes: $espFree, freeBytes: $free, systems: $systems,
      warnings: ($warnings | split("\n") | map(select(. != "")))}'
}
# The disks someone could install to: whole disks with a size, not read-only,
# and whether something on them is in use (the installer's own USB stick).
disks_json() {
  lsblk -J -b -o NAME,PATH,SIZE,TYPE,MODEL,TRAN,RM,RO,MOUNTPOINTS -e 7,11 | jq -c '
    def yes: . == true or . == "1" or . == 1;
    [.blockdevices[] | select(.type == "disk" and (.size // 0) > 0 and (.ro | yes | not) and ((.name // "") | test("^(zram|loop)") | not))
     | { path, size, model: ((.model // "") | gsub("^\\s+|\\s+$"; "")), transport: (.tran // ""), removable: (.rm | yes),
         inUse: ([.. | objects | (.mountpoints // [])[] | select(. != null and . != "" and (test("^/mnt(/|$)") | not))] | length > 0) }]'
}
# --- end of alongside planning

detected_timezone() {
  local tz
  tz="$(timedatectl show -p Timezone --value 2>/dev/null || true)"
  echo "${tz:-UTC}"
}

# --- Keyboard layouts -------------------------------------------------------
# nix/installer/keyboards.json, which nix/modules/keyboard.nix reads too: each
# layout's name, its console keymap and the countries that mostly use it.
keyboards="${NEXUS_KEYBOARDS:-$source_dir/nix/installer/keyboards.json}"
zonetab="${NEXUS_ZONETAB:-/etc/zoneinfo/zone.tab}"
known_keyboard() { jq -e --arg k "$1" 'has($k)' "$keyboards" >/dev/null 2>&1; }
keyboard_name() { jq -r --arg k "$1" '.[$k].name // $k' "$keyboards"; }
keyboard_console() { jq -r --arg k "$1" '.[$k].console' "$keyboards"; }
# The time zone's country (zone.tab), then the layout most used there; else US.
guessed_keyboard() {
  local country=""
  [ ! -r "$zonetab" ] || country="$(awk -F'\t' -v z="$1" '$1 !~ /^#/ && $3 == z { print $1; exit }' "$zonetab")"
  jq -r --arg c "$country" '[to_entries[] | select(.value.countries | index($c)) | .key][0] // "us"' "$keyboards" 2>/dev/null || echo us
}
keyboards_json() { jq -c '[to_entries[] | {id: .key, name: .value.name}] | sort_by(.name)' "$keyboards"; }
# --- end of keyboard layouts

# --- Probe ------------------------------------------------------------------
# For the graphical installer: everything it shows before anything changes.
if [ "$probe" -eq 1 ]; then
  disks="$(disks_json)"
  plans="$(jq -r '.[] | select(.inUse | not) | .path' <<<"$disks" | while read -r d; do
    [ -z "$disk" ] || [ "$d" = "$disk" ] || continue
    plan_alongside "$d"; plan_json "$d"
  done | jq -sc 'map({(.disk): .}) | add // {}')"
  replace="$(jq -r '.[] | select(.inUse | not) | .path' <<<"$disks" | while read -r d; do
    [ -z "$disk" ] || [ "$d" = "$disk" ] || continue
    plan_partitions "$d" | jq -c --arg d "$d" '{($d): .}'
  done | jq -sc 'add // {}')"
  emit --argjson disks "$disks" --argjson plans "$plans" --argjson replace "$replace" --arg processor "$processor_line" --arg graphics "$graphics" \
    --arg ai "$ai_line" --arg tz "$(detected_timezone)" --argjson min "$min_root_bytes" \
    --arg kb "$(guessed_keyboard "$(detected_timezone)")" --argjson kbs "$(keyboards_json)" \
    '{event: "probe", disks: ($disks | map(. + {alongside: ($plans[.path] // null), replace: ($replace[.path] // null)})), minimumBytes: $min,
      hardware: {processor: $processor, graphics: $graphics, localAI: $ai}, timezone: $tz, keyboard: $kb, keyboards: $kbs}'
  exit 0
fi

say "NexusOS installer"
if [ -n "$partition" ]; then
  echo "This replaces one partition with NexusOS. The rest of its disk is not changed."
elif [ "$alongside" -eq 1 ]; then
  echo "This installs NexusOS in the free space of one disk, beside what is on it. Nothing is erased."
else
  echo "This erases one disk and installs NexusOS on it. Other disks are not touched."
fi

# --- Disk -------------------------------------------------------------------
if [ -n "$partition" ]; then
  [ -b "$partition" ] && [ "$(lsblk -dno TYPE "$partition")" = part ] || fail "$partition is not a partition"
  parent="/dev/$(lsblk -dno PKNAME "$partition")"
  [ -z "$disk" ] || [ "$disk" = "$parent" ] || fail "$partition is not on $disk"
  disk="$parent"
fi
if [ -z "$disk" ]; then
  echo
  echo "Disks on this computer:"
  lsblk -dpno NAME,SIZE,MODEL,TRAN -e 7,11 | sed 's/^/  /'
  echo
  read -rp "Disk to install to (for example /dev/nvme0n1): " disk
fi
[ -b "$disk" ] || fail "$disk is not a disk"
[ "$(lsblk -dno TYPE "$disk")" = disk ] || fail "$disk is a partition; give the whole disk"
# A failed attempt leaves this disk mounted at /mnt: release that first, so
# trying again works. Mounted anywhere else, the disk is really in use.
if findmnt -rno SOURCE,TARGET | awk -v d="$disk" 'index($1, d) == 1 && $2 ~ "^/mnt(/|$)"' | grep -q .; then
  echo "Unmounting what an earlier attempt left mounted at /mnt."
  umount -R /mnt || fail "could not unmount /mnt from the earlier attempt; restart the computer and try again"
fi
if findmnt -rno SOURCE | grep -q "^$disk"; then
  fail "$disk is in use (mounted). Is it the disk the installer is running from?"
fi
cryptsetup close nexusos-root 2>/dev/null || true

# Beside another system: only if the plan holds. Asked interactively when it could.
plan_ok=0 plan_reason=""
if [ -z "$partition" ] && { [ "$alongside" -eq 1 ] || [ "$assume_yes" -eq 0 ]; }; then plan_alongside "$disk"; fi
if [ -z "$partition" ] && [ "$alongside" -eq 0 ] && [ "$assume_yes" -eq 0 ]; then
  if [ "$plan_ok" -eq 1 ]; then
    echo
    echo "$disk has $plan_systems on it, and $(gb "$plan_free") of free space."
    read -rp "Install NexusOS alongside, keeping everything on the disk? [Y/n] " answer
    case "$answer" in [nN]*) ;; *) alongside=1 ;; esac
  elif [[ "$plan_reason" != "This disk has no partitions"* ]]; then
    printf '\nInstalling alongside is not possible: %s\n' "$plan_reason"
  fi
fi
if [ "$alongside" -eq 1 ]; then
  [ "$plan_ok" -eq 1 ] || fail "cannot install alongside on $disk: $plan_reason"
  [ -z "$plan_warnings" ] || printf '\nNote: %s\n' "$plan_warnings"
fi
# Replacing one partition: only if the plan allows that one.
part_esp="" part_what=""
if [ -n "$partition" ]; then
  replace="$(plan_partitions "$disk")"
  [ "$(jq -r '.possible' <<<"$replace")" = true ] || fail "cannot replace a partition on $disk: $(jq -r '.reason' <<<"$replace")"
  this="$(jq -c --arg p "$partition" '.partitions[] | select(.path == $p)' <<<"$replace")"
  [ -n "$this" ] || fail "$partition cannot be replaced (it is the EFI system or Microsoft reserved partition)"
  [ "$(jq -r '.possible' <<<"$this")" = true ] || fail "cannot replace $partition: $(jq -r '.reason' <<<"$this")"
  part_esp="$(jq -r '.esp' <<<"$replace")" part_what="$(jq -r '.what' <<<"$this")"
  plan_alongside "$disk"      # for plan_systems: what else is on the disk
  plan_esp_free="$(esp_free_bytes "$part_esp")"
fi

# --- Keyboard ---------------------------------------------------------------
# Before the password: the disk's password is typed with this layout at every
# start-up, so it is typed with it here too.
[ -n "$timezone" ] || timezone="$(detected_timezone)"
[ -e "/etc/zoneinfo/$timezone" ] || [ "$timezone" = UTC ] || fail "unknown time zone $timezone"
if [ -z "$keyboard" ]; then
  keyboard="$(guessed_keyboard "$timezone")"
  while [ "$assume_yes" -eq 0 ] && [ -z "$password_file" ]; do
    read -rp "Keyboard layout [$keyboard: $(keyboard_name "$keyboard")] (? lists them): " answer
    case "$answer" in
      "") break ;;
      "?") jq -r 'to_entries[] | "  \(.key)\t\(.value.name)"' "$keyboards" ;;
      *) if known_keyboard "$answer"; then keyboard="$answer"; break; else echo "Not one of the layouts; ? lists them."; fi ;;
    esac
  done
fi
known_keyboard "$keyboard" || fail "unknown keyboard layout $keyboard (one of: $(jq -r 'keys | join(" ")' "$keyboards"))"
if [ -z "$password_file" ] && [ -t 0 ]; then loadkeys "$(keyboard_console "$keyboard")" >/dev/null 2>&1 || true; fi

# --- Person -----------------------------------------------------------------
if [ -z "$user" ]; then
  read -rp "Your login name: " user
fi
[[ "$user" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] || fail "login names use lower case letters, digits, - and _, and start with a letter"
case "$user" in root|nixos|nobody) fail "$user is reserved; pick another name" ;; esac
if [ -z "$full_name" ] && [ -z "$password_file" ]; then
  read -rp "Your full name (optional): " full_name
fi

if [ -n "$password_file" ]; then
  # "-" is standard input, where the graphical installer writes it.
  password="$(head -n1 -- "$password_file")"
else
  while true; do
    read -rsp "Password: " password; echo
    read -rsp "Password again: " again; echo
    if [ "$password" != "$again" ]; then echo "The passwords did not match. Try again."
    elif [ ${#password} -lt 10 ]; then echo "Use at least 10 characters: it also approves system changes."
    else break; fi
  done
fi
[ ${#password} -ge 10 ] || fail "use a password of at least 10 characters"

[[ "$host" =~ ^[A-Za-z0-9][A-Za-z0-9-]{0,62}$ ]] || fail "computer names use letters, digits and -"

# --- Confirm ----------------------------------------------------------------
say "Ready to install"
if [ -n "$partition" ]; then
  echo "  Partition: $partition ($(lsblk -dno SIZE "$partition"), $part_what) — erased and replaced; the rest of $disk is not changed"
  echo "  Start-up:  the EFI partition $part_esp is shared, not formatted"
elif [ "$alongside" -eq 1 ]; then
  echo "  Disk:      $disk ($(lsblk -dno SIZE "$disk")) — alongside $plan_systems, in $(gb "$plan_free") of free space; nothing else is erased"
  echo "  Start-up:  the EFI partition $plan_esp is shared, not formatted; the boot menu lists Windows too"
else
  echo "  Disk:      $disk ($(lsblk -dno SIZE "$disk")) — everything on it will be erased"
fi
if [ "$encrypt" -eq 1 ]; then
  echo "  Encrypted: yes; your password unlocks the disk when the computer starts"
else
  echo "  Encrypted: no (--no-encrypt)"
fi
echo "  User:      $user"
echo "  Computer:  $host"
echo "  Time zone: $timezone"
echo "  Keyboard:  $(keyboard_name "$keyboard")"
echo "  Processor: $processor_line"
echo "  Graphics:  $graphics"
echo "  Local AI:  $ai_line"
[ -z "$large_model" ] || echo "  Large AI:  $large_model"
if [ "$assume_yes" -ne 1 ]; then
  echo
  if [ -n "$partition" ]; then
    read -rp "Type the partition name ($partition) to erase it and install: " answer
    [ "$answer" = "$partition" ] && answer="$disk"
  elif [ "$alongside" -eq 1 ]; then
    read -rp "Type the disk name ($disk) to install alongside: " answer
  else
    read -rp "Type the disk name ($disk) to erase it and install: " answer
  fi
  [ "$answer" = "$disk" ] || fail "not confirmed; nothing was changed"
fi
[ -f "$ai_manifest" ] || step_of=5
emit --arg disk "$disk" --argjson alongside "$alongside" --arg partition "$partition" --argjson encrypt "$encrypt" --arg log "$log" \
  '{event: "start", disk: $disk, alongside: ($alongside == 1), partition: $partition, encrypted: ($encrypt == 1), log: $log}'

# --- Partition and format ---------------------------------------------------
step prepare "Preparing $disk"
umount -R /mnt 2>/dev/null || true
cryptsetup close nexusos-root 2>/dev/null || true
if [ "$alongside" -eq 1 ]; then
  # One new partition in the free space; every other partition stays as it is.
  printf 'start=%s, size=%s, type=0FC63DAF-8483-4772-8E79-3D69D8477DE4, name=nexusos\n' "$plan_start" "$plan_size" |
    sfdisk --append --no-reread --no-tell-kernel --wipe never "$disk" > /dev/null
  root_part=""
  for _ in $(seq 1 30); do
    partx -u "$disk" 2>/dev/null || partprobe "$disk" 2>/dev/null || true
    udevadm settle
    root_part="$(sfdisk -J "$disk" | jq -r --argjson s "$plan_start" '.partitiontable.partitions[] | select(.start == $s) | .node')"
    [ -n "$root_part" ] && [ -b "$root_part" ] && break
    sleep 1
  done
  [ -n "$root_part" ] && [ -b "$root_part" ] || fail "the new partition on $disk did not appear; restart the computer and try again"
  added_part="$root_part"
  boot_part="$plan_esp"
  wipefs -af "$root_part" > /dev/null
elif [ -n "$partition" ]; then
  # Only this partition: wiped, marked as Linux and named; the rest of the disk stays as it is.
  root_part="$partition" boot_part="$part_esp"
  wipefs -af "$root_part" > /dev/null
  part_number="$(cat "/sys/class/block/$(basename "$root_part")/partition")"
  sfdisk --part-type "$disk" "$part_number" 0FC63DAF-8483-4772-8E79-3D69D8477DE4 > /dev/null
  sfdisk --part-label "$disk" "$part_number" nexusos > /dev/null
  partx -u "$disk" 2>/dev/null || true
  udevadm settle
else
  wipefs -af "$disk" > /dev/null
  parted -s "$disk" -- mklabel gpt \
    mkpart ESP fat32 1MiB 1GiB set 1 esp on \
    mkpart nexusos ext4 1GiB 100%
  # Wait until the kernel shows exactly the two new partitions.
  parts=()
  for _ in $(seq 1 30); do
    partprobe "$disk" 2>/dev/null || true
    udevadm settle
    mapfile -t parts < <(lsblk -lnpo NAME,TYPE "$disk" | awk '$2 == "part" { print $1 }')
    [ "${#parts[@]}" -eq 2 ] && [ -b "${parts[0]}" ] && [ -b "${parts[1]}" ] && break
    sleep 1
  done
  [ "${#parts[@]}" -eq 2 ] || fail "expected two partitions on $disk, found ${#parts[@]}"
  boot_part="${parts[0]}" root_part="${parts[1]}"

  # An earlier system on this disk can leave filesystem signatures inside the
  # new partitions; wipe them so nothing mistakes a partition for its old self.
  wipefs -af "$boot_part" "$root_part" > /dev/null
  mkfs.fat -F 32 -n BOOT "$boot_part" > /dev/null
fi
# Encryption: LUKS2 around the root filesystem, unlocked with the person's
# password before anything else starts. Without the password, a lost or
# stolen disk shows nothing: not the files, not Guardian's builder key.
# nixos-generate-config finds the encrypted partition and has the initrd ask.
step format "$([ "$encrypt" -eq 1 ] && echo "Encrypting and formatting" || echo "Formatting") the NexusOS partition"
root_fs="$root_part"
if [ "$encrypt" -eq 1 ]; then
  printf '%s' "$password" | cryptsetup luksFormat --type luks2 --batch-mode --label nexusos-crypt --key-file=- "$root_part"
  printf '%s' "$password" | cryptsetup open --key-file=- --allow-discards "$root_part" nexusos-root
  root_fs=/dev/mapper/nexusos-root
fi
mkfs.ext4 -q -F -L nexusos "$root_fs"
udevadm settle
[ "$(blkid -p -o value -s TYPE "$boot_part")" = vfat ] || fail "$boot_part did not format as FAT"
[ "$(blkid -p -o value -s TYPE "$root_fs")" = ext4 ] || fail "$root_fs did not format as ext4"
mount -t ext4 "$root_fs" /mnt
mkdir -p /mnt/boot
mount -t vfat -o umask=0077 "$boot_part" /mnt/boot

# --- Configuration ----------------------------------------------------------
step config "Writing the configuration"
nixos-generate-config --root /mnt > /dev/null
rm -f /mnt/etc/nixos/configuration.nix
# The NexusOS source lives inside /etc/nixos, so the machine's flake can
# refer to it by a relative path and rebuild without network access.
mkdir -p /mnt/etc/nixos/nexusos
cp -rL --no-preserve=ownership "$source_dir/." /mnt/etc/nixos/nexusos/
chmod -R u+w /mnt/etc/nixos/nexusos

nix_string() { printf '"%s"' "$(printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g; s/\$/\\$/g')"; }

modules="./hardware-configuration.nix ./local.nix"
if [ -n "$extra_module" ]; then
  [ -f "$extra_module" ] || fail "no module at $extra_module"
  cp "$extra_module" /mnt/etc/nixos/extra.nix
  modules="$modules ./extra.nix"
fi
cat > /mnt/etc/nixos/flake.nix <<EOF
# This computer's NexusOS configuration. Change local.nix, then run:
#   sudo nixos-rebuild switch --flake /etc/nixos#nexus
{
  inputs.nexusos.url = "path:./nexusos";
  outputs = { self, nexusos }: {
    nixosConfigurations.nexus = nexusos.lib.mkSystem {
      modules = [ $modules ];
    };
  };
}
EOF
cat > /mnt/etc/nixos/local.nix <<EOF
# Settings for this computer, written by nexus-install. Options are listed
# in /etc/nixos/nexusos/nix/profiles/nexusos.nix and nix/modules/desktop.nix.
{
  nexusos.user = $(nix_string "$user");
  nexusos.fullName = $(nix_string "$full_name");
  networking.hostName = $(nix_string "$host");
  time.timeZone = $(nix_string "$timezone");
  nexusos.keyboard.layout = $(nix_string "$keyboard");
}
EOF
# What the hardware scan found (nix/modules/hardware.nix), before the closing brace.
sed -i '$d' /mnt/etc/nixos/local.nix
{
  echo "  # Found by nexus-install: ${cpu_name:-processor}; $graphics"
  echo "  nexusos.hardware.cpu = \"$cpu\";"
  echo "  nexusos.hardware.gpu = \"$gpu\";"
  [ "$amd_vram_gib" -eq 0 ] || echo "  nexusos.hardware.gpuMemoryGiB = $amd_vram_gib;"
  echo "  nexusos.hardware.memoryGiB = $memory_gib;"
  echo "  nexusos.hardware.laptop = $laptop;"
  [ -z "$model" ] || echo "  services.nexus-core.ollamaModel = \"$model\";"
  if [ -n "$nvidia_lines" ]; then
    echo "  nexusos.nvidia.enable = true;"
    [ "$nvidia_open" = true ] || echo "  nexusos.nvidia.open = false;"
    [ "$nvidia_driver" = stable ] || echo "  nexusos.nvidia.driver = \"$nvidia_driver\";"
    if [ -n "$intel_bus$amd_bus" ]; then
      echo "  nexusos.nvidia.prime.nvidiaBusId = \"$nvidia_bus\";"
      [ -z "$intel_bus" ] || echo "  nexusos.nvidia.prime.intelBusId = \"$intel_bus\";"
      [ -z "$amd_bus" ] || echo "  nexusos.nvidia.prime.amdgpuBusId = \"$amd_bus\";"
    fi
  fi
  if [ "$alongside" -eq 1 ] || [ -n "$partition" ]; then
    echo "  # Installed alongside $plan_systems: the EFI partition is shared ($(mb "$plan_esp_free") free when"
    echo "  # installed), so keep only a few start-up entries there."
    echo "  boot.loader.systemd-boot.configurationLimit = 3;"
    if [[ "$plan_systems" == Windows* ]] || { [ "$alongside" -eq 1 ] && [ "$plan_systems" != "another Linux system" ]; }; then
      echo "  # Windows keeps the hardware clock in local time; so does NexusOS, so the clocks agree."
      echo "  time.hardwareClockInLocalTime = true;"
    fi
  fi
  echo "}"
} >> /mnt/etc/nixos/local.nix

# --- Install ----------------------------------------------------------------
step install "Installing NexusOS (this takes a while)"
# Everything the install needs is on the image. Online, the NixOS cache can
# still save some building; offline, give up on it quickly. (Do not empty
# the substituters: nixos-install copies from this USB stick's store through
# one, and an empty list would make it download nixpkgs from GitHub.)
offline=()
if ! curl -sfI --max-time 8 https://cache.nixos.org/nix-cache-info > /dev/null 2>&1; then
  echo "No internet connection: installing from the USB stick only."
  offline=(--option connect-timeout 3)
fi
# Lock first: locking inside nixos-install would change the flake mid-build.
nix --extra-experimental-features "nix-command flakes" flake lock "path:/mnt/etc/nixos" "${offline[@]}"
# Offline, only small files specific to this computer may need building (its
# boot files and settings). Anything bigger means a package is missing from
# the stick: say which, rather than spend hours trying to compile it.
if [ "${#offline[@]}" -gt 0 ]; then
  plan="$(nix --extra-experimental-features "nix-command flakes" build --dry-run "${offline[@]}" \
    "path:/mnt/etc/nixos#nixosConfigurations.nexus.config.system.build.toplevel" 2>&1 || true)"
  heavy="$(sed -n '/will be built/,/will be fetched/p' <<<"$plan" | grep -oE '/nix/store/[a-z0-9]{32}-[^ ]+\.drv' \
    | sed -E 's|/nix/store/[a-z0-9]{32}-||; s|\.drv$||' \
    | grep -E '^(stage0|hex0|bootstrap|gcc|binutils|glibc-2|xgcc|bash-5|python3-3|perl-5\.[0-9.]+$|rustc|cargo|llvm|clang)' | sort -u | head -20 || true)"
  if [ -n "$heavy" ]; then
    printf '%s\n' "$plan" >> "$log"
    fail "this computer's system needs packages the USB stick does not carry, and there is no internet to fetch them:
  ${heavy//$'\n'/$'\n'  }
Connect to the internet (Wi-Fi in the system panel) and try again, or report this list."
  fi
fi
if [ "$progress" -eq 1 ]; then
  # Each line to the log, and its start to the graphical installer.
  nixos-install --root /mnt --flake "path:/mnt/etc/nixos#nexus" --no-root-passwd --no-channel-copy "${offline[@]}" 2>&1 |
    tee -a "$log" | jq -R -c --unbuffered '{event: "detail", text: .[0:200]}' >&3
else
  nixos-install --root /mnt --flake "path:/mnt/etc/nixos#nexus" --no-root-passwd --no-channel-copy "${offline[@]}"
fi

# The local model, if the image carries the one chosen above: first boot
# hands it to Ollama (nexus-model-pull), so Nexus answers without
# downloading it. Only that model's manifest and files are copied.
if [ -f "$ai_manifest" ]; then
  step model "Copying the local AI model ($ai_model)"
  seed=/mnt/var/lib/nexus-model-seed
  rel="${ai_manifest#/etc/nexusos-model/}"
  mkdir -p "$seed/blobs" "$(dirname "$seed/$rel")"
  cp -L --no-preserve=ownership,mode "$ai_manifest" "$seed/$rel"
  grep -oE 'sha256:[0-9a-f]{64}' "$ai_manifest" | sort -u | while read -r digest; do
    cp -L --no-preserve=ownership,mode "/etc/nexusos-model/blobs/${digest/:/-}" "$seed/blobs/"
  done
fi

step password "Setting your password"
printf '%s:%s\n' "$user" "$password" | nixos-enter --root /mnt -c 'chpasswd'

[ -z "$log" ] || { mkdir -p /mnt/var/log && cp "$log" /mnt/var/log/nexus-install.log; }
sync
added_part=""
say "NexusOS is installed"
echo "Remove the installer USB stick, then restart the computer."
emit '{event: "done"}'
