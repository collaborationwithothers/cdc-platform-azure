#!/usr/bin/env bash
# Capture task-api user tokens in memory; print only inspector summaries.
# Hari runs this after the live preconditions in verify-taskapi-token-claims.md.
set +x
set -euo pipefail
if [ "$#" -ne 0 ]; then
  echo 'FAIL: capture takes no arguments; use the documented environment inputs' >&2
  exit 1
fi

python3 - "$(cd "$(dirname "$0")" && pwd)/inspect-taskapi-token.sh" <<'PY'
import json
import os
import re
import subprocess
import sys
import time
from urllib.parse import urlencode, urlsplit


def fail(message):
    raise SystemExit("FAIL: " + message)


def request(endpoint, form, timeout=30):
    result = subprocess.run(
        ["curl", "-q", "--silent", "--show-error", "--connect-timeout", "10",
         "--max-time", str(timeout), "--request", "POST", "--write-out", "\n%{http_code}",
         "--header", "Content-Type: application/x-www-form-urlencoded",
         "--data-binary", "@-", authority + endpoint],
        input=urlencode(form), text=True, capture_output=True, timeout=timeout + 5)
    if result.returncode:
        fail("device authorization transport failed; response suppressed")
    try:
        body, status = result.stdout.rsplit("\n", 1)
        response = json.loads(body)
        if not isinstance(response, dict):
            raise ValueError()
        return response, int(status)
    except (ValueError, UnicodeError):
        fail("device authorization response is not a JSON object")


def reject_error(response):
    allowed = {"invalid_request", "invalid_client", "unauthorized_client", "invalid_scope",
               "invalid_resource", "access_denied", "server_error", "temporarily_unavailable",
               "authorization_declined", "bad_verification_code", "expired_token"}
    error = response.get("error")
    safe_error = error if isinstance(error, str) and error in allowed else "unrecognized"
    codes = response.get("error_codes", [])
    safe_codes = [code for code in codes if type(code) is int] if isinstance(codes, list) else []
    fail("device authorization error=" + safe_error + " error_codes=" + json.dumps(safe_codes))


def validate_device(response):
    for name in ("device_code", "user_code", "verification_uri"):
        if not isinstance(response.get(name), str) or not response[name].strip():
            fail("device authorization response has missing or invalid fields")
    for name in ("interval", "expires_in"):
        if type(response.get(name)) is not int or response[name] <= 0:
            fail("device authorization response has missing or invalid fields")
    try:
        uri = urlsplit(response["verification_uri"])
    except ValueError:
        fail("device authorization sign-in instructions are invalid")
    if (uri.scheme != "https" or uri.netloc not in ("microsoft.com", "www.microsoft.com", "login.microsoftonline.com")
            or not re.fullmatch(r"[A-Z0-9-]{4,32}", response["user_code"])):
        fail("device authorization sign-in instructions are invalid")


def capture(scopes, stage):
    started = time.monotonic()
    response, http_status = request("devicecode", {
        "client_id": os.environ["user_client_id"],
        "scope": os.environ["taskapi_resource"] + "/" + scopes})
    if "error" in response or http_status != 200:
        reject_error(response)
    validate_device(response)
    deadline = started + min(response["expires_in"], 900)
    interval = response["interval"]
    form = {"client_id": os.environ["user_client_id"], "device_code": response["device_code"],
            "grant_type": "urn:ietf:params:oauth:grant-type:device_code"}
    print(f"Sign-in {stage}/2 ({scopes}): open {response['verification_uri']} and enter {response['user_code']}.",
          file=sys.stderr, flush=True)
    del response
    while True:
        if time.monotonic() + interval >= deadline:
            fail("device authorization expired; rerun capture")
        time.sleep(interval)
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            fail("device authorization expired; rerun capture")
        response, http_status = request("token", form, min(30, remaining))
        if time.monotonic() >= deadline:
            fail("device authorization expired; rerun capture")
        if http_status == 400 and response.get("error") == "authorization_pending":
            continue
        if "error" in response or http_status != 200:
            reject_error(response)
        token = response.get("access_token")
        if not isinstance(token, str) or not token.strip():
            fail("token response has no usable access token")
        # The access token goes only through the inspector pipe, never argv.
        # Any ID or refresh token in the response is ignored, not inspected.
        inspected = subprocess.run(["/bin/bash", sys.argv[1]], input=token,
                                   text=True, capture_output=True, timeout=30)
        if inspected.returncode:
            fail("token inspector rejected the access token; no summaries published")
        return inspected.stdout.rstrip()


try:
    for name in ("tenant_id", "user_client_id", "taskapi_resource"):
        if not os.environ.get(name, "").strip():
            fail(name + " is missing; rerun the runbook preconditions")
    for name in ("tenant_id", "user_client_id"):
        if not re.fullmatch(r"[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", os.environ[name]):
            fail(name + " is not a valid identifier; value suppressed")
    if not os.environ["taskapi_resource"].startswith("api://") or re.search(r"\s", os.environ["taskapi_resource"]):
        fail("taskapi_resource is invalid; value suppressed")
    authority = "https://login.microsoftonline.com/" + os.environ["tenant_id"] + "/oauth2/v2.0/"
    profile = capture("Tasks.Write openid profile", 1)
    plain = capture("Tasks.Write openid", 2)
    print("**Delegated Tasks.Write openid profile:**\n```text\n" + profile + "\n```\n")
    print("**Delegated Tasks.Write openid (without profile):**\n```text\n" + plain + "\n```")
except (OSError, UnicodeError, subprocess.TimeoutExpired):
    fail("capture command failed or timed out; details suppressed")
except KeyboardInterrupt:
    fail("capture cancelled; no summaries published")
PY
