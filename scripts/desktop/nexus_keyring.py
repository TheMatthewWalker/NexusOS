#!/usr/bin/env python3
"""Keep the login keyring's password the same as the login password.

The login keyring (gnome-keyring: saved passwords for browsers, mail, Wi-Fi
and Nexus's cloud keys) is unlocked at login with the login password. When
the password changes in Settings, Accounts, the keyring's must change with
it, or the next login cannot unlock it. Runs as the person, while the keyring
is unlocked; the old and new password come on standard input (two lines),
never on a command line.

  nexus-keyring-password    (stdin: old password, new password)

It uses gnome-keyring's own call for this (ChangeWithMasterPassword), over a
plain Secret Service session on the person's own session bus.
"""
import asyncio
import json
import sys

from dbus_next import Message, MessageType, Variant
from dbus_next.aio import MessageBus

DEST = "org.gnome.keyring"
SERVICE = "/org/freedesktop/secrets"
LOGIN = "/org/freedesktop/secrets/collection/login"


def secret(session, password):
    return [session, b"", password.encode(), "text/plain"]


async def change(old, new, bus=None):
    bus = bus or await MessageBus().connect()
    reply = await bus.call(Message(destination=DEST, path=SERVICE, interface="org.freedesktop.Secret.Service",
                                   member="OpenSession", signature="sv", body=["plain", Variant("s", "")]))
    if reply.message_type == MessageType.ERROR:
        return {"error": "The keyring is not running."}
    session = reply.body[1]
    reply = await bus.call(Message(destination=DEST, path=SERVICE,
                                   interface="org.gnome.keyring.InternalUnsupportedGuiltRiddenInterface",
                                   member="ChangeWithMasterPassword", signature="o(oayays)(oayays)",
                                   body=[LOGIN, secret(session, old), secret(session, new)]))
    if reply.message_type == MessageType.ERROR:
        return {"error": "The keyring's password could not be changed: " + (reply.error_name or "")}
    return {"message": "The keyring's password changed too."}


def main():
    lines = sys.stdin.read().split("\n")
    if len(lines) < 2 or not lines[0] or not lines[1]:
        print(json.dumps({"error": "Give the old and the new password, one a line."}))
        return 2
    result = asyncio.run(change(lines[0], lines[1]))
    print(json.dumps(result))
    return 1 if "error" in result else 0


if __name__ == "__main__":
    sys.exit(main())
