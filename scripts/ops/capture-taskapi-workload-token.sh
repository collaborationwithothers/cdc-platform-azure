#!/usr/bin/env bash
# Hari runs this helper only through the bounded token-capture runbook.
set +x
set -euo pipefail
if [ "$#" -ne 1 ] || { [ "$1" != prepare ] && [ "$1" != capture ]; }; then
  echo 'FAIL: expected prepare or capture; never pass token material as arguments' >&2
  exit 1
fi
python3 - "$1" "$(cd "$(dirname "$0")" && pwd)/inspect-taskapi-token.sh" <<'PY'
import base64
import json
import os
import re
import select
import shlex
import subprocess
import sys
import time

# Only this reusable program is written inside the container. Coordinates
# arrive after READY, over stdin with terminal echo disabled, not in its file.
remote = '''#!/usr/bin/env python3
import json, sys, termios
from urllib.parse import urlencode
from urllib.request import build_opener, ProxyHandler, HTTPRedirectHandler, Request
class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *args):
        return None
try:
    if sys.stdin.isatty():
        settings = termios.tcgetattr(sys.stdin)
        settings[3] &= ~(termios.ECHO | termios.ECHONL)
        termios.tcsetattr(sys.stdin, termios.TCSANOW, settings)
    print("READY", flush=True)
    form = json.loads(sys.stdin.readline())
    form["api-version"] = "2018-02-01"
    url = "http://169.254.169.254/metadata/identity/oauth2/token?" + urlencode(form)
    with build_opener(ProxyHandler({}), NoRedirect()).open(
            Request(url, headers={"Metadata": "true"}), timeout=30) as response:
        token = json.load(response).get("access_token")
    if not isinstance(token, str) or not token or any(c.isspace() for c in token):
        raise ValueError()
    print(token, flush=True)
except Exception:
    sys.exit("FAIL: metadata token request failed; response suppressed")
'''
executable = "/tmp/taskapi-token"


def fail(message):
    raise SystemExit("FAIL: " + message)


def capture():
    for name in ("resource_group", "container_group", "workload_client_id", "taskapi_resource"):
        if not os.environ.get(name, "").strip():
            fail(name + " is missing; rerun the runbook preconditions")
    client = os.environ["workload_client_id"]
    resource = os.environ["taskapi_resource"]
    if not re.fullmatch(r"[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", client):
        fail("workload_client_id is invalid; value suppressed")
    if not resource.startswith("api://") or re.search(r"\s", resource):
        fail("taskapi_resource is invalid; value suppressed")
    command = ["az", "container", "exec", "--resource-group", os.environ["resource_group"],
               "--name", os.environ["container_group"], "--exec-command", executable,
               "--only-show-errors"]
    with subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                          stderr=subprocess.DEVNULL, bufsize=0) as process:
        try:
            deadline = time.monotonic() + 60
            ready = b""
            while not ready.endswith(b"\n"):
                remaining = deadline - time.monotonic()
                if remaining <= 0 or not select.select([process.stdout], [], [], remaining)[0]:
                    fail("container exec readiness timed out; no coordinates sent")
                byte = os.read(process.stdout.fileno(), 1)
                if not byte or len(ready) > 16:
                    fail("container exec did not become ready; output suppressed")
                ready += byte
            if ready.strip() != b"READY":
                fail("container exec readiness was invalid; output suppressed")
            token, _ = process.communicate(
                (json.dumps({"resource": resource, "client_id": client}) + "\n").encode(), timeout=60)
            if process.returncode:
                fail("container exec failed; output suppressed")
        finally:
            if process.poll() is None:
                process.kill()
                process.wait()
    inspected = subprocess.run(["/bin/bash", sys.argv[2]], input=token,
                               capture_output=True, timeout=30)
    if inspected.returncode:
        fail("workload token inspector rejected the exec output; no summary published")
    sys.stdout.buffer.write(inspected.stdout)


try:
    if sys.argv[1] == "prepare":
        encoded = base64.b64encode(remote.encode()).decode()
        bootstrap = ("import base64,pathlib,time; p=pathlib.Path(" + repr(executable) + "); "
                     "p.write_bytes(base64.b64decode(" + repr(encoded) + ")); "
                     "p.chmod(0o700); time.sleep(3600)")
        print("python3 -c " + shlex.quote(bootstrap))
    else:
        capture()
except (OSError, ValueError, subprocess.TimeoutExpired):
    fail("workload capture command failed or timed out; output suppressed")
except KeyboardInterrupt:
    fail("workload capture cancelled; no summary published")
PY
