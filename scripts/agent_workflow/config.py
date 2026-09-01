"""Strict loading of the one pinned routing configuration."""

from dataclasses import dataclass
from pathlib import Path
import tomllib

MODELS = {"gpt-5.6-sol", "gpt-5.6-luna", "claude-opus-5", "claude-sonnet-5"}
EFFORTS = {"medium", "high", "xhigh"}
PROVIDERS = {"codex", "claude"}
RISKS = {"mechanical", "standard", "high"}
STAGES = {"plan", "implement", "native_review", "cross_review"}


@dataclass(frozen=True)
class WorkflowConfig:
    version: int
    models: dict[str, str]
    routes: dict[tuple[str, str, str], tuple[str, str, bool]]


def load_config(path: str | Path | None = None) -> WorkflowConfig:
    source = Path(path or Path(__file__).parents[2] / ".agents/agent-workflow.toml")
    try:
        raw = tomllib.loads(source.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, tomllib.TOMLDecodeError) as exc:
        raise ValueError(f"invalid workflow configuration: {exc}") from exc
    if raw.get("version") != 1 or not isinstance(raw.get("models"), dict) or set(raw["models"]) != {"sol", "luna", "opus", "sonnet"}:
        raise ValueError("workflow configuration must declare version 1 and all four model pins")
    models = raw["models"]
    if any(value not in MODELS for value in models.values()) or len(set(models.values())) != 4:
        raise ValueError("workflow configuration contains an alias, fallback, or duplicate model")
    try:
        routes = {}
        for provider in PROVIDERS:
            for risk in RISKS:
                entries = raw["routes"][provider][risk]
                if set(entries) != STAGES:
                    raise ValueError(f"missing route for {provider}/{risk}")
                for stage, entry in entries.items():
                    if not isinstance(entry, dict) or set(entry) - {"model", "effort", "short_plan"} or entry["model"] not in MODELS or entry["effort"] not in EFFORTS or ("short_plan" in entry and not isinstance(entry["short_plan"], bool)):
                        raise ValueError(f"invalid route for {provider}/{risk}/{stage}")
                    routes[provider, risk, stage] = (entry["model"], entry["effort"], bool(entry.get("short_plan", False)))
    except (KeyError, TypeError) as exc:
        raise ValueError(f"incomplete workflow routes: {exc}") from exc
    return WorkflowConfig(1, dict(models), routes)
