"""Deterministic provider and risk route lookup."""

from dataclasses import dataclass
from .config import load_config


@dataclass(frozen=True)
class Route:
    provider: str
    risk: str
    stage: str
    model: str
    effort: str
    short_plan: bool = False


def resolve_route(provider: str, risk: str, stage: str, config=None) -> Route:
    provider, risk, stage = provider.lower(), risk.lower(), stage.lower().replace("-", "_")
    try:
        model, effort, short = (config or load_config()).routes[provider, risk, stage]
    except KeyError as exc:
        raise ValueError(f"no pinned route for {provider}/{risk}/{stage}") from exc
    return Route(provider, risk, stage, model, effort, short)
