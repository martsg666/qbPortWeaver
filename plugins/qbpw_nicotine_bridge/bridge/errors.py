# SPDX-License-Identifier: GPL-3.0-or-later
"""The bridge's error vocabulary.

Every failure the API can report is an ``ApiError`` carrying an HTTP status and a stable
machine-readable code. The code is what qbPortWeaver branches on; the message is for the
user and may be reworded freely.
"""


class ApiError(Exception):
    """A failure with a stable code, an HTTP status, and a human-readable message."""

    def __init__(self, status, code, message):
        super().__init__(message)
        self.status = status
        self.code = code
        self.message = message

    def payload(self):
        return {"ok": False, "error": {"code": self.code, "message": self.message}}


def bad_request(message):
    return ApiError(400, "bad_request", message)


def unauthorized():
    return ApiError(401, "unauthorized", "Missing or invalid bearer token.")


def forbidden_origin():
    return ApiError(403, "forbidden_origin", "Cross-origin requests are not accepted.")


def not_found(path):
    return ApiError(404, "not_found", f"No such endpoint: {path}")


def method_not_allowed(method, path):
    return ApiError(405, "method_not_allowed", f"{method} is not allowed on {path}")


def port_locked_by_cli(cli_port):
    return ApiError(
        409, "port_locked_by_cli",
        f"Nicotine+ was started with --port {cli_port}, which overrides the configured port for the "
        "lifetime of the process. Remove that option from the shortcut and restart Nicotine+ to let "
        "the port be managed."
    )


def port_in_use(port):
    return ApiError(409, "port_in_use", f"Port {port} is already in use by another application.")


def unsupported(what):
    return ApiError(
        501, "unsupported",
        f"This version of Nicotine+ does not expose {what} in the way the bridge expects. "
        "Update the qbPortWeaver Bridge plugin."
    )


def core_not_ready():
    return ApiError(503, "core_not_ready", "Nicotine+ is still starting up.")


def shutting_down():
    return ApiError(503, "shutting_down", "Nicotine+ is shutting down.")


def main_thread_timeout():
    return ApiError(
        504, "main_thread_timeout",
        "Nicotine+'s main thread did not respond in time. It may be busy scanning shares."
    )
