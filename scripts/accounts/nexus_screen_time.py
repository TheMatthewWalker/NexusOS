#!/usr/bin/env python3
"""Screen time (Settings, Accounts): hours a child's account may be used, and
how long a day, as Windows' Family Safety. An administrator sets them; the
child cannot change them.

  nexus-screen-time check            PAM (account): may PAM_USER log in now? exit 1 if not
  nexus-screen-time tick             each minute, as root: count the minute, warn, sign out when over
  nexus-screen-time status [LOGIN]   JSON: limits, minutes used and left today
  nexus-screen-time set              root (nexus-account limits, which an administrator approves):
                                     {login, from, to, minutes} on stdin; no times and 0 minutes lifts them

Limits live in /var/lib/nexus/screen-time/limits/LOGIN.json, written only by
root; minutes used in /var/lib/nexus/screen-time/used/LOGIN-DATE. An account
with no limits file has no limits, so the check costs one missing file. The
check fails open on anything unexpected: a broken file never locks anyone out
of the computer, it only lifts that one child's limits.
"""
import datetime
import json
import os
import pwd
import re
import subprocess
import sys

BASE = os.environ.get("NEXUS_SCREEN_TIME_DIR", "/var/lib/nexus/screen-time")
LOGIN = re.compile(r"[a-z_][a-z0-9_-]{0,31}")
CLOCK = re.compile(r"([01]\d|2[0-3]):([0-5]\d)")
WARN_AT = 5          # minutes left when the child is told


class LimitError(Exception):
    pass


def run(command, input_text=None):
    done = subprocess.run(command, input=input_text, capture_output=True, text=True)
    return done.returncode, done.stdout, done.stderr


def minutes_of(clock):
    m = CLOCK.fullmatch(clock or "")
    if m is None:
        raise LimitError("Use times like 07:30.")
    return int(m.group(1)) * 60 + int(m.group(2))


def limits_path(login, base=BASE):
    return os.path.join(base, "limits", login + ".json")


def used_path(login, day, base=BASE):
    return os.path.join(base, "used", f"{login}-{day.isoformat()}")


def read_limits(login, base=BASE):
    """The limits, or None for none (or a file that cannot be read: fail open)."""
    if LOGIN.fullmatch(login or "") is None:
        return None
    try:
        with open(limits_path(login, base)) as stream:
            data = json.load(stream)
        out = {"from": data.get("from") or "", "to": data.get("to") or "", "minutes": int(data.get("minutes") or 0)}
        if out["from"] or out["to"]:
            if minutes_of(out["from"]) >= minutes_of(out["to"]):
                return None
        return out
    except (OSError, ValueError, TypeError, AttributeError, LimitError):
        return None


def read_used(login, day, base=BASE):
    try:
        with open(used_path(login, day, base)) as stream:
            return max(0, int(stream.read().strip() or 0))
    except (OSError, ValueError):
        return 0


def verdict(limits, used, now):
    """(allowed, minutes left today or None, why not)."""
    if limits is None:
        return True, None, ""
    left = None
    if limits["from"]:
        start, end, at = minutes_of(limits["from"]), minutes_of(limits["to"]), now.hour * 60 + now.minute
        if not start <= at < end:
            return False, 0, f"This account can be used from {limits['from']} to {limits['to']}."
        left = end - at
    if limits["minutes"] > 0:
        rest = limits["minutes"] - used
        if rest <= 0:
            return False, 0, f"Today's {limits['minutes']} minutes of screen time are used up."
        left = rest if left is None else min(left, rest)
    return True, left, ""


def status(login, now=None, base=BASE):
    now = now or datetime.datetime.now()
    limits = read_limits(login, base)
    used = read_used(login, now.date(), base)
    allowed, left, why = verdict(limits, used, now)
    return {"login": login, "limits": limits, "used": used, "left": left, "allowed": allowed, "why": why}


def check(login, now=None, base=BASE):
    try:
        s = status(login, now, base)
        if not s["allowed"]:
            print(s["why"] + " Ask a parent to change it in Settings, Accounts.")
            return 1
    except Exception:        # fail open: never lock the computer over a fault here
        pass
    return 0


def set_limits(request, people, base=BASE):
    """Write or remove a child's limits. people: login -> is an administrator."""
    login = request.get("login")
    if not isinstance(login, str) or LOGIN.fullmatch(login) is None:
        raise LimitError("That is not an account on this computer.")
    if login not in people:
        raise LimitError(f"There is no account called {login}.")
    if people[login]:
        raise LimitError("Administrators cannot have screen time limits: make the account a standard one first.")
    start, end = str(request.get("from") or ""), str(request.get("to") or "")
    try:
        minutes = int(request.get("minutes") or 0)
    except (TypeError, ValueError):
        raise LimitError("The daily limit is a number of minutes.") from None
    if minutes < 0 or minutes > 24 * 60:
        raise LimitError("The daily limit is between 0 (none) and 1440 minutes.")
    if bool(start) != bool(end):
        raise LimitError("Give both a start and an end time, or neither.")
    if start and minutes_of(start) >= minutes_of(end):
        raise LimitError("The end time comes after the start time, on the same day.")
    path = limits_path(login, base)
    if not start and minutes == 0:
        try:
            os.unlink(path)
        except FileNotFoundError:
            pass
        return {"login": login, "limits": None, "message": f"{login} has no screen time limits now."}
    os.makedirs(os.path.dirname(path), mode=0o755, exist_ok=True)
    temp = path + ".new"
    with open(temp, "w") as stream:
        json.dump({"from": start, "to": end, "minutes": minutes}, stream)
    os.chmod(temp, 0o644)
    os.replace(temp, path)
    parts = ([f"from {start} to {end}"] if start else []) + ([f"for {minutes} minutes a day"] if minutes else [])
    return {"login": login, "limits": {"from": start, "to": end, "minutes": minutes},
            "message": f"{login} can use this computer " + " and ".join(parts) + "."}


def graphical_users(runner=run):
    """Logins with a session open now (loginctl), as (login, uid)."""
    status_, out, _ = runner(["loginctl", "list-sessions", "--no-legend", "--json=short"])
    if status_ != 0:
        return []
    try:
        sessions = json.loads(out or "[]")
    except ValueError:
        return []
    seen = {}
    for s in sessions:
        if s.get("class", "user") == "user":
            seen[s.get("user")] = int(s.get("uid", -1))
    return [(u, uid) for u, uid in seen.items() if u]


def notify(login, uid, text, runner=run):
    runner(["runuser", "-u", login, "--", "env", f"DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/{uid}/bus",
            "notify-send", "--app-name=Screen time", "--urgency=critical", "Screen time", text])


def tick(now=None, base=BASE, runner=run):
    """Count a minute for each limited child signed in; warn near the end; sign them out after."""
    now = now or datetime.datetime.now()
    done = []
    for login, uid in graphical_users(runner):
        limits = read_limits(login, base)
        if limits is None:
            continue
        used = read_used(login, now.date(), base) + 1
        os.makedirs(os.path.join(base, "used"), mode=0o755, exist_ok=True)
        with open(used_path(login, now.date(), base), "w") as stream:
            stream.write(str(used))
        allowed, left, why = verdict(limits, used, now)
        if not allowed:
            notify(login, uid, why + " Signing out now: save your work.", runner)
            runner(["loginctl", "terminate-user", login])
            done.append((login, "out"))
        elif left is not None and left == WARN_AT:
            notify(login, uid, f"{WARN_AT} minutes of screen time left: save your work.", runner)
            done.append((login, "warned"))
    # Old days' counts are not needed.
    try:
        for name in os.listdir(os.path.join(base, "used")):
            if not name.endswith(now.date().isoformat()):
                os.unlink(os.path.join(base, "used", name))
    except OSError:
        pass
    return done


def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv
    command = argv[0] if argv else ""
    if command == "check":
        return check(os.environ.get("PAM_USER", ""))
    if command == "tick":
        tick()
        return 0
    if command == "set":
        if os.geteuid() != 0:
            print(json.dumps({"error": "Screen time is set in Settings, Accounts, by an administrator."}))
            return 1
        import grp
        try:
            admins = set(grp.getgrnam("wheel").gr_mem)
        except KeyError:
            admins = set()
        people = {e.pw_name: e.pw_name in admins for e in pwd.getpwall() if 1000 <= e.pw_uid < 60000}
        try:
            request = json.loads(sys.stdin.read(4096) or "{}")
            if not isinstance(request, dict):
                raise LimitError("Malformed request")
            print(json.dumps(set_limits(request, people)))
            return 0
        except (LimitError, ValueError) as error:
            print(json.dumps({"error": str(error) if isinstance(error, LimitError) else "Malformed request"}))
            return 1
    if command == "status":
        login = argv[1] if len(argv) > 1 else pwd.getpwuid(os.getuid()).pw_name
        print(json.dumps(status(login)))
        return 0
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
