"""Fail-closed workflow state transitions and artifact validation."""

from dataclasses import dataclass, replace
import json
from pathlib import Path
import re

ROOT = Path(__file__).parent
FORBIDDEN = {"local_path", "local_paths", "process_id", "pid", "conversation_id", "prompt", "transcript", "credential", "token", "environment"}
SHA = re.compile(r"^[0-9a-f]{40}$")
STATES = {"preflight", "selected", "claimed", "planned", "implementing", "evidence-ready", "native-review", "cross-review", "repair-response", "arbitration", "needs-hari", "awaiting-hari", "cancelled", "failed"}


@dataclass(frozen=True)
class WorkflowState:
    state: str
    head: str | None = None
    risk: str = "standard"
    repair_count: int = 0

    def __eq__(self, other):
        return self.state == (other.state if isinstance(other, WorkflowState) else other)


def _event(value: str) -> str:
    event = value.upper().replace("-", "_").replace(" ", "_")
    return {"FIXED": "REPAIR_FIXED", "REQUEST_CHANGES": "REQUEST_CHANGES"}.get(event, event)


def transition(current: WorkflowState | str, event: str, *, head: str | None = None, current_head: str | None = None, risk: str | None = None) -> WorkflowState:
    old = current if isinstance(current, WorkflowState) else WorkflowState(current, current_head, risk or "standard")
    if old.state not in STATES:
        raise ValueError(f"unknown workflow state: {old.state}")
    if head is not None and (old.head or current_head) and head != (old.head or current_head) and _event(event) not in {"REPAIR_FIXED", "IMPLEMENTED"}:
        raise ValueError("stale exact-SHA artifact")
    event = _event(event)
    next_state = {
        ("preflight", "SELECT"): "selected", ("selected", "CLAIM"): "claimed",
        ("claimed", "PLAN_COMPLETE"): "planned", ("planned", "IMPLEMENT_START"): "implementing",
        ("implementing", "EVIDENCE_READY"): "evidence-ready", ("evidence-ready", "REVIEW"): "native-review",
        ("native-review", "APPROVE"): "awaiting-hari", ("native-review", "REQUEST_CHANGES"): "repair-response",
        ("cross-review", "APPROVE"): "awaiting-hari", ("cross-review", "APPROVE_UNRESOLVED_NATIVE"): "needs-hari", ("cross-review", "REQUEST_CHANGES"): "repair-response",
        ("repair-response", "ACCEPTED_NO_FIX"): "native-review", ("repair-response", "ACCEPTED_NO_FIX_CROSS"): "cross-review", ("repair-response", "REPAIR_FIXED"): "evidence-ready",
        ("repair-response", "DISPUTED"): "arbitration", ("repair-response", "DISPUTED_CROSS_REVIEW"): "needs-hari", ("arbitration", "UPHOLD"): "repair-response",
        ("arbitration", "REJECT"): "native-review", ("arbitration", "NEEDS_HARI"): "needs-hari",
        ("repair-response", "NEEDS_HARI"): "needs-hari",
        ("implementing", "CANCEL"): "cancelled", ("native-review", "CANCEL"): "cancelled", ("cross-review", "CANCEL"): "cancelled",
        ("implementing", "FAIL"): "failed", ("native-review", "FAIL"): "failed", ("cross-review", "FAIL"): "failed",
    }.get((old.state, event))
    is_high = old.risk == "high" or getattr(old.risk, "name", "").lower() == "high"
    if old.state == "native-review" and event == "APPROVE" and is_high:
        next_state = "cross-review"
    if old.state == "repair-response" and event == "DISPUTED" and is_high:
        next_state = "cross-review"
    if old.state == "repair-response" and event == "REPAIR_FIXED":
        if head is None or (old.head and head == old.head):
            raise ValueError("repair must advance to a new exact SHA")
        count = old.repair_count + 1
        if count > 2:
            return replace(old, state="needs-hari", head=head, repair_count=count)
        return replace(old, state="evidence-ready", head=head, repair_count=count)
    if next_state is None:
        raise ValueError(f"invalid transition: {old.state} + {event}")
    if old.state == "native-review" and event == "APPROVE" and not is_high:
        next_state = "awaiting-hari"
    return replace(old, state=next_state, head=head or old.head, risk=risk or old.risk)


def _check(value, required, allowed):
    if not isinstance(value, dict):
        raise ValueError("artifact must be an object")
    missing = set(required) - set(value)
    if missing:
        raise ValueError(f"artifact missing required fields: {sorted(missing)}")
    forbidden = {key for key in value if key.lower() in FORBIDDEN}
    if forbidden:
        raise ValueError(f"artifact contains forbidden durable fields: {sorted(forbidden)}")
    if isinstance(value.get("result"), dict):
        nested = {key for key in value["result"] if key.lower() in FORBIDDEN}
        if nested:
            raise ValueError(f"result contains forbidden durable fields: {sorted(nested)}")
    unknown = set(value) - set(allowed)
    if unknown:
        raise ValueError(f"artifact contains unknown fields: {sorted(unknown)}")
    if not SHA.fullmatch(value["head"]):
        raise ValueError("head must be a 40-character lowercase exact SHA")
    route = value["route"]
    if not isinstance(route, dict) or set(route) != {"provider", "model", "effort"}:
        raise ValueError("route must contain exactly provider, model, and effort")
    if route["provider"] not in {"codex", "claude"} or route["model"] not in {"gpt-5.6-sol", "gpt-5.6-luna", "claude-opus-5", "claude-sonnet-5"} or route["effort"] not in {"medium", "high", "xhigh"}:
        raise ValueError("route contains an unsupported provider, model, or effort")
    if not isinstance(value["version"], int) or value["version"] != 1:
        raise ValueError("version must be 1")


def validate_stage_result(value: dict) -> dict:
    _check(value, {"schema", "version", "head", "route", "result"}, {"schema", "version", "head", "route", "result"})
    if value["schema"] != "agent-workflow-stage-result/v1":
        raise ValueError("invalid stage-result schema version")
    return value


def validate_checkpoint(value: dict) -> dict:
    required = {"schema", "version", "revision", "issue", "session", "stage", "head", "route", "result"}
    allowed = required | {"initiating_provider", "mode", "risk", "branch", "repair_count", "review_urls", "updated_at"}
    _check(value, required, allowed)
    if value["schema"] != "agent-workflow-checkpoint/v1" or not isinstance(value["revision"], int) or value["revision"] < 1:
        raise ValueError("invalid checkpoint schema or revision")
    return value


def validate_artifact(kind: str, value: dict) -> dict:
    if kind in {"stage-result", "stage_result"}:
        return validate_stage_result(value)
    if kind == "checkpoint":
        return validate_checkpoint(value)
    raise ValueError(f"unknown artifact kind: {kind}")


def load_schema(kind: str) -> dict:
    name = "stage-result.schema.json" if kind in {"stage-result", "stage_result"} else "checkpoint.schema.json" if kind == "checkpoint" else None
    if not name:
        raise ValueError(f"unknown schema: {kind}")
    return json.loads((ROOT / "schemas" / name).read_text(encoding="utf-8"))
