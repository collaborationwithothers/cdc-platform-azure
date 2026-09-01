"""Pure deterministic minimum-risk calculation."""

from dataclasses import dataclass
from enum import IntEnum


class Risk(IntEnum):
    MECHANICAL = 0
    STANDARD = 1
    HIGH = 2
    mechanical = MECHANICAL
    standard = STANDARD
    high = HIGH


HIGH_WORDS = {
    "authentication", "authorization", "identity", "permissions", "secrets",
    "data loss", "schema migration", "destructive", "recovery", "concurrency",
    "ordering", "durability", "side-effect retries", "side effect retries", "failure recovery",
    "governance", "model routing", "orchestration", "cross-area", "cross area", "undeclared paths",
}


@dataclass(frozen=True)
class RiskResult:
    level: Risk
    reasons: tuple[str, ...]

    @property
    def name(self) -> str:
        return self.level.name


def _text(ticket: dict) -> str:
    return " ".join(str(value) for value in ticket.values()).lower().replace("_", " ")


def risk_floor(ticket: dict) -> RiskResult:
    """Return the minimum risk; model-provided promotion is never accepted here."""
    reasons: list[str] = []
    text = _text(ticket)
    labels = {str(label).lower() for label in ticket.get("labels", [])}
    for word in sorted(HIGH_WORDS):
        if word in text:
            reasons.append(word)
    paths = [str(path) for path in ticket.get("paths", [])]
    declared = {str(path) for path in ticket.get("declared_paths", [])}
    if declared and set(paths) - declared:
        reasons.append("undeclared paths")
    if any(path == "infra" or path.startswith(("infra/", ".github/workflows/")) for path in paths) or "needs-live-test" in labels:
        reasons.append("deployed workflow or live verification")
    if len(ticket.get("areas", [])) > 1:
        reasons.append("cross-area")
    if ticket.get("deployed_behavior") or ticket.get("review_conflict"):
        reasons.append("deployed behavior or reviewer conflict")
    if ticket.get("undeclared_paths") or ticket.get("diff_above_policy"):
        reasons.append("undeclared paths or oversized diff")
    if not reasons and ticket.get("fully_specified") and ticket.get("small_local_deterministic") and ticket.get("verification") != "live" and not ticket.get("architecture_decision"):
        return RiskResult(Risk.mechanical, ())
    return RiskResult(Risk.high if reasons else Risk.standard, tuple(dict.fromkeys(reasons)))


minimum_risk = risk_floor


def apply_floor(floor: Risk | RiskResult, proposed: Risk | str) -> Risk:
    """Promote a proposed level to the deterministic floor, never demote it."""
    minimum = floor.level if isinstance(floor, RiskResult) else Risk(floor)
    candidate = Risk[proposed.upper()] if isinstance(proposed, str) else Risk(proposed)
    return max(minimum, candidate)
