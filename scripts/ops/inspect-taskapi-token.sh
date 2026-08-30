#!/usr/bin/env bash
#
# Reads one task-api JSON Web Token from standard input and prints only the
# claim-shape summary approved for issue 266. Decoding the payload is inspection
# only. It does not validate the token signature or prove who issued the token.
#
# Usage: printf '%s\n' "$TOKEN" | inspect-taskapi-token.sh

set +x
set -euo pipefail

fail() { echo "FAIL: $*" >&2; exit 1; }

if [ "$#" -ne 0 ]; then
  fail "inspect-taskapi-token.sh accepts a token only through standard input; do not place a token in a command argument"
fi

python3 -c '
import base64
import binascii
import json
import sys


def reject():
    print(
        "FAIL: standard input is not a well-formed compact JWT with a JSON object payload",
        file=sys.stderr,
    )
    raise SystemExit(1)


def optional_string(payload, name):
    value = payload.get(name)
    if value is not None and not isinstance(value, str):
        reject()
    return value


try:
    compact_token = sys.stdin.read().strip()
    if not compact_token:
        reject()
    segments = compact_token.split(".")
    if len(segments) != 3 or not all(segments):
        reject()

    encoded_payload = segments[1]
    padding = "=" * (-len(encoded_payload) % 4)
    decoded_payload = base64.b64decode(
        encoded_payload + padding,
        altchars=b"-_",
        validate=True,
    )
    payload = json.loads(decoded_payload)
    if not isinstance(payload, dict):
        reject()

    version = optional_string(payload, "ver")
    identity_type = optional_string(payload, "idtyp")
    subject = optional_string(payload, "sub")
    object_id = optional_string(payload, "oid")

    scope_value = payload.get("scp")
    if scope_value is not None and not isinstance(scope_value, str):
        reject()
    scopes = scope_value.split() if scope_value else []

    roles_value = payload.get("roles", [])
    if not isinstance(roles_value, list) or not all(isinstance(role, str) for role in roles_value):
        reject()
except (binascii.Error, UnicodeDecodeError, json.JSONDecodeError, ValueError):
    reject()

print("inspection: JWT payload decoded without signature validation")
print("token_version: " + json.dumps(version, ensure_ascii=True))
print("idtyp: " + json.dumps(identity_type, ensure_ascii=True))
for claim in ("tid", "oid", "azp", "appid"):
    print(f"{claim}_present: {str(claim in payload).lower()}")
print("scp: " + json.dumps(scopes, ensure_ascii=True))
print("roles: " + json.dumps(roles_value, ensure_ascii=True))
print(f"sub_equals_oid: {str(subject is not None and object_id is not None and subject == object_id).lower()}")
'
