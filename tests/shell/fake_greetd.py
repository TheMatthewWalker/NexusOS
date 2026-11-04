"""A stand-in for greetd's IPC socket, for driving greeter.qml without a real login.

It speaks greetd's protocol (a native-endian u32 length, then JSON) and plays
PAM the way greetd with nix/patches/greetd-expired-password.patch and
pam_unix do: "Password:", then for an expired password pam_unix's notice and
"Current password:", "New password:", "Retype new password:". Every auth
message, informational ones too, waits for a post_auth_message_response, as
in greetd. What happened is kept in `events` (never the passwords).
"""
import json
import socket
import struct
import threading


class FakeGreetd:
    def __init__(self, path, user, password, expired=False):
        self.path, self.user, self.password, self.expired = path, user, password, expired
        self.events = []
        self.started = None
        self._sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self._sock.bind(path)
        self._sock.listen(4)
        threading.Thread(target=self._serve, daemon=True).start()

    def close(self):
        self._sock.close()

    # The conversation for one session: yields (type, text), receives the answers.
    def _pam(self, username):
        answer = yield ("secret", "Password: ")
        if username != self.user or answer != self.password:
            return "pam_authenticate: AUTH_ERR"
        if self.expired:
            self.events.append("expired")
            yield ("error", "You are required to change your password immediately (administrator enforced).")
            current = yield ("secret", "Current password: ")
            if current != self.password:
                yield ("error", "Authentication token manipulation error")
                return "pam_chauthtok: AUTHTOK_ERR"
            new = yield ("secret", "New password: ")
            again = yield ("secret", "Retype new password: ")
            if new != again:
                yield ("error", "Sorry, passwords do not match.")
                return "pam_chauthtok: AUTHTOK_ERR"
            if new == self.password:
                yield ("error", "Password unchanged")
                return "pam_chauthtok: AUTHTOK_ERR"
            self.password, self.expired = new, False
            self.events.append("changed")
        return None

    def _serve(self):
        while True:
            try:
                conn, _ = self._sock.accept()
            except OSError:
                return
            threading.Thread(target=self._client, args=(conn,), daemon=True).start()

    def _client(self, conn):
        def send(obj):
            data = json.dumps(obj).encode()
            conn.sendall(struct.pack("=I", len(data)) + data)

        def step(conv, value):
            try:
                kind, text = conv.send(value)
                send({"type": "auth_message", "auth_message_type": kind, "auth_message": text})
                return conv
            except StopIteration as done:
                if done.value is None:
                    self.events.append("authenticated")
                    send({"type": "success"})
                else:
                    self.events.append("refused " + done.value)
                    send({"type": "error", "error_type": "auth_error", "description": done.value})
                return None

        conv = None
        with conn:
            while True:
                head = conn.recv(4, socket.MSG_WAITALL)
                if len(head) < 4:
                    return
                (size,) = struct.unpack("=I", head)
                request = json.loads(conn.recv(size, socket.MSG_WAITALL))
                kind = request.get("type")
                if kind == "create_session":
                    self.events.append("create_session " + request.get("username", ""))
                    conv = self._pam(request.get("username", ""))
                    conv = step(conv, None)
                elif kind == "post_auth_message_response" and conv is not None:
                    conv = step(conv, request.get("response"))
                elif kind == "start_session":
                    self.started = request.get("cmd")
                    self.events.append("start_session")
                    send({"type": "success"})
                elif kind == "cancel_session":
                    conv = None
                    self.events.append("cancel_session")
                    send({"type": "success"})
                else:
                    send({"type": "error", "error_type": "error", "description": "unexpected " + str(kind)})
