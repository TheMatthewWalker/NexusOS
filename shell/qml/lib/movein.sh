#!/bin/sh
# Move in (MoveIn.qml): looks for the usual folders on an old drive or backup
# folder, and copies one of them into the home folder. The old drive is not
# trusted: its user-folder names, symlinks and special files could otherwise
# steer a copy into ~/.bashrc, ~/.ssh or ~/.config/autostart. So:
#  - destinations come only from a fixed list (Documents, Pictures, Music,
#    Videos, Downloads, Desktop) under $HOME; the drive never names one;
#  - a per-person subfolder (Documents/<name>) is one plain path component:
#    no "/", no leading ".", no control characters, never "." or "..";
#  - symlinked folders on the drive are skipped, and a source must resolve
#    inside the chosen root; a symlinked destination is refused;
#  - rsync skips symlinks, devices and special files, keeps no owner, group
#    or permissions, and never overwrites what is already there.
# Where the people's folders are: Users/<name> (Windows, and a Mac's data
# volume), else home/<name> (an old Linux drive); always the first of the two
# that is a real folder, so scan and copy agree. A Mac drive opened by hand
# with apfs-fuse has the volume under root/ (next to private-dir/): that
# root/ is used instead, when it is a real folder.
#
#   movein.sh scan ROOT
#     NUL-terminated records: "os<TAB>Windows|Mac|Linux",
#     "item<TAB>user<TAB>folder<TAB>bytes" (user is empty for ROOT/<folder>),
#     "private<TAB>user<TAB>folder" for a folder this account cannot read (a
#     Mac or Linux home folder keeps its old owner), and "unreadable" when
#     the volume itself cannot be listed (an encrypted Mac volume).
#   movein.sh copy ROOT USER FOLDER PER_USER
#     Copies <people>/USER/FOLDER (or ROOT/FOLDER when USER is empty) into
#     $HOME/<destination>, plus /USER when PER_USER is 1.
set -u

FOLDERS="Documents Desktop Downloads Pictures Music Videos Movies"

die() { printf 'movein: %s\n' "$1" >&2; exit "${2:-2}"; }

# One path component from the drive, safe to show and to use as a folder name.
valid_name() {
    case "$1" in
        ''|.*|*/*|*[[:cntrl:]]*) return 1 ;;
    esac
    [ "${#1}" -le 255 ]
}

# The home folder a source folder goes into; Movies becomes Videos.
destination_name() {
    case "$1" in
        Documents|Desktop|Downloads|Pictures|Music|Videos) printf '%s' "$1" ;;
        Movies) printf 'Videos' ;;
        *) return 1 ;;
    esac
}

# A real directory, not a symlink to one.
real_dir() { [ -d "$1" ] && [ ! -L "$1" ]; }

# A folder this account may list and read.
readable() { [ -r "$1" ] && [ -x "$1" ]; }

# The volume itself: ROOT, or ROOT/root for an apfs-fuse mount.
volume_of() {
    if real_dir "$1/root" && [ -d "$1/private-dir" ]; then printf '%s/root' "$1"; else printf '%s' "$1"; fi
}

# The folder that holds the people's folders, relative to the volume, or nothing.
people_of() {
    if real_dir "$1/Users"; then printf 'Users'
    elif real_dir "$1/home"; then printf 'home'; fi
}

size_of() { s=$(du -sb -- "$1" 2>/dev/null | cut -f1); printf '%s' "${s:-0}"; }

# One folder's record: its size, or that it is private to its old owner.
record() {
    if readable "$3"; then printf 'item\t%s\t%s\t%s\0' "$1" "$2" "$(size_of "$3")"
    else printf 'private\t%s\t%s\0' "$1" "$2"; fi
}

scan() {
    [ -d "$1" ] || die "missing"
    r=$(volume_of "$1")
    ls -A -- "$r" >/dev/null 2>&1 || { printf 'unreadable\0'; return 0; }
    if [ -d "$r/Windows/System32" ] || { [ -d "$r/Users" ] && [ -d "$r/ProgramData" ]; }; then printf 'os\tWindows\0'
    elif [ -d "$r/System/Library" ] || { [ -d "$r/Users" ] && { [ -d "$r/Applications" ] || [ -d "$r/Library" ]; }; }; then printf 'os\tMac\0'
    elif [ -f "$r/etc/os-release" ] || { [ -d "$r/home" ] && [ -d "$r/etc" ]; }; then printf 'os\tLinux\0'; fi
    p=$(people_of "$r")
    if [ -n "$p" ]; then
        for u in "$r/$p"/*; do
            n=${u##*/}
            real_dir "$u" && valid_name "$n" || continue
            case "$n" in Public|Default|'Default User'|'All Users'|defaultuser0|Shared|Guest|lost+found) continue ;; esac
            for f in $FOLDERS; do
                real_dir "$u/$f" && record "$n" "$f" "$u/$f"
            done
        done
    fi
    for f in $FOLDERS; do
        real_dir "$r/$f" && record "" "$f" "$r/$f"
    done
    return 0
}

# Makes (or checks) one destination directory; refuses a symlink or a file.
ensure_dir() {
    [ -L "$1" ] && die "refusing: $1 is a symlink"
    if [ -e "$1" ]; then [ -d "$1" ] || die "refusing: $1 is not a folder"
    else mkdir -- "$1" || die "could not make $1"; fi
}

copy() {
    r=$1 user=$2 folder=$3 per_user=$4
    case "$r" in /*) ;; *) die "the source must be an absolute path" ;; esac
    case "$r" in *[[:cntrl:]]*) die "the source path has control characters" ;; esac
    [ -d "$r" ] || die "missing"
    r=$(volume_of "$r")
    name=$(destination_name "$folder") || die "not a folder Nexus copies: $folder"
    if [ -n "$user" ]; then
        valid_name "$user" || die "not a valid user folder name"
        p=$(people_of "$r")
        [ -n "$p" ] && real_dir "$r/$p/$user" || die "refusing: the user folder is a symlink or missing"
        src="$r/$p/$user/$folder"
    else
        src="$r/$folder"
    fi
    real_dir "$src" || die "refusing: $folder is a symlink or missing"
    readable "$src" || die "$folder belongs to the old computer's own account and cannot be read here"
    rr=$(realpath -e -- "$r") && rs=$(realpath -e -- "$src") || die "missing"
    [ "$rr" = / ] || case "$rs/" in "$rr"/*) ;; *) die "refusing: $folder is outside the chosen place" ;; esac

    home=${HOME:-}
    case "$home" in /*) ;; *) die "HOME is not set" ;; esac
    real_dir "$home" || die "refusing: the home folder is a symlink or missing"
    dest="$home/$name"
    ensure_dir "$dest"
    if [ "$per_user" = 1 ] && [ -n "$user" ]; then dest="$dest/$user"; ensure_dir "$dest"; fi

    exec rsync -r -t --ignore-existing --no-links --no-devices --no-specials \
        --no-perms --no-owner --no-group --chmod=Du=rwx,Fu=rw --info=progress2 \
        -- "$rs/" "$dest/"
}

case "${1:-}" in
    scan) [ $# -eq 2 ] || die "usage: movein.sh scan ROOT"; scan "$2" ;;
    copy) [ $# -eq 5 ] || die "usage: movein.sh copy ROOT USER FOLDER PER_USER"; copy "$2" "$3" "$4" "$5" ;;
    *) die "usage: movein.sh scan ROOT | copy ROOT USER FOLDER PER_USER" ;;
esac
