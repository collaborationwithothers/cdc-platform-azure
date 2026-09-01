import unittest
import tempfile
from unittest.mock import patch

from .config import load_config
from .risk import Risk, apply_floor, risk_floor
from .routing import resolve_route
from .state import WorkflowState, load_schema, transition, validate_artifact


SHA = "a" * 40


class RoutingTests(unittest.TestCase):
    def test_every_route_is_pinned_and_matrix_matches_spec(self):
        config = load_config()
        self.assertEqual(set(config.models.values()), {"gpt-5.6-sol", "gpt-5.6-luna", "claude-opus-5", "claude-sonnet-5"})
        for provider in ("codex", "claude"):
            for risk in ("mechanical", "standard", "high"):
                for stage in ("plan", "implement", "native_review", "cross_review"):
                    route = resolve_route(provider, risk, stage, config)
                    self.assertIn(route.model, config.models.values())
                    self.assertIn(route.effort, ("medium", "high", "xhigh"))
        self.assertEqual(resolve_route("codex", "mechanical", "plan").model, "gpt-5.6-luna")
        self.assertEqual(resolve_route("codex", "high", "implement").effort, "high")
        self.assertEqual(resolve_route("claude", "standard", "native_review").model, "claude-opus-5")
        with self.assertRaises(ValueError):
            resolve_route("codex", "default", "plan")

    def test_invalid_pin_fails_closed(self):
        with tempfile.NamedTemporaryFile(mode="w", suffix=".toml") as file:
            file.write("version=1\n[models]\nsol='default'\nluna='gpt-5.6-luna'\nopus='claude-opus-5'\nsonnet='claude-sonnet-5'\n")
            file.flush()
            with self.assertRaises(ValueError):
                load_config(file.name)


class RiskTests(unittest.TestCase):
    def test_mechanical_requires_complete_small_non_live_ticket(self):
        ticket = {"fully_specified": True, "small_local_deterministic": True, "verification": "unit"}
        self.assertEqual(risk_floor(ticket).level, Risk.mechanical)
        self.assertEqual(risk_floor({"fully_specified": True}).level, Risk.standard)

    def test_high_triggers_cannot_be_lowered(self):
        for trigger in ("identity", "schema migration", "concurrency", "governance"):
            result = risk_floor({"description": trigger, "fully_specified": True, "small_local_deterministic": True})
            self.assertEqual(result.level, Risk.high, trigger)
        self.assertEqual(risk_floor({"labels": ["needs-live-test"]}).level, Risk.high)
        self.assertEqual(risk_floor({"paths": [".github/workflows/check.yml"]}).level, Risk.high)
        self.assertEqual(apply_floor(Risk.high, "mechanical"), Risk.high)


class ArtifactTests(unittest.TestCase):
    def setUp(self):
        self.route = {"provider": "codex", "model": "gpt-5.6-sol", "effort": "high"}

    def test_schemas_and_exact_sha_are_required(self):
        stage = {"schema": "agent-workflow-stage-result/v1", "version": 1, "head": SHA, "route": self.route, "result": {"ok": True}}
        self.assertEqual(validate_artifact("stage-result", stage), stage)
        self.assertEqual(load_schema("checkpoint")["$id"], "agent-workflow-checkpoint/v1")
        with self.assertRaises(ValueError):
            validate_artifact("stage-result", {**stage, "head": "main"})
        with self.assertRaises(ValueError):
            validate_artifact("stage-result", {**stage, "token": "never"})

    def test_checkpoint_requires_durable_fields_and_rejects_forbidden_fields(self):
        checkpoint = {"schema": "agent-workflow-checkpoint/v1", "version": 1, "revision": 1, "issue": 305, "session": "s1", "stage": "native-review", "head": SHA, "route": self.route, "result": {}}
        self.assertEqual(validate_artifact("checkpoint", checkpoint), checkpoint)
        with self.assertRaises(ValueError):
            validate_artifact("checkpoint", {**checkpoint, "process_id": 2})
        with self.assertRaises(ValueError):
            validate_artifact("checkpoint", {**checkpoint, "result": {"prompt": "no"}})
        with self.assertRaises(ValueError):
            validate_artifact("checkpoint", {key: value for key, value in checkpoint.items() if key != "result"})


class StateTests(unittest.TestCase):
    def test_kernel_is_inactive_and_does_not_start_processes(self):
        with patch("subprocess.run") as run:
            load_config()
            risk_floor({"description": "unit"})
            resolve_route("codex", "standard", "plan")
            transition("preflight", "select")
        run.assert_not_called()

    def test_lifecycle_reaches_review_and_repair(self):
        state = WorkflowState("preflight", SHA)
        for event in ("select", "claim", "plan complete", "implement start", "evidence ready", "review"):
            state = transition(state, event)
        self.assertEqual(state, "native-review")
        self.assertEqual(transition(state, "request changes"), "repair-response")

    def test_normal_and_high_review_paths(self):
        state = WorkflowState("native-review", SHA, "standard")
        self.assertEqual(transition(state, "APPROVE"), "awaiting-hari")
        self.assertEqual(transition(WorkflowState("native-review", SHA, "high"), "APPROVE"), "cross-review")
        self.assertEqual(transition(WorkflowState("repair-response", SHA, "high"), "DISPUTED"), "cross-review")
        self.assertEqual(transition(WorkflowState("repair-response", SHA, "standard"), "DISPUTED"), "arbitration")
        self.assertEqual(transition(WorkflowState("repair-response", SHA, "standard"), "DISPUTED_CROSS_REVIEW"), "needs-hari")
        self.assertEqual(transition(WorkflowState("cross-review", SHA, "high"), "APPROVE_UNRESOLVED_NATIVE"), "needs-hari")

    def test_repairs_invalidate_old_sha_and_stop_after_two_cycles(self):
        state = WorkflowState("repair-response", SHA, "standard")
        repaired = transition(state, "FIXED", head="b" * 40)
        self.assertEqual((repaired, repaired.repair_count), ("evidence-ready", 1))
        with self.assertRaises(ValueError):
            transition(repaired, "REVIEW", head=SHA)
        stopped = transition(WorkflowState("repair-response", "b" * 40, "standard", 2), "REPAIR_FIXED", head="c" * 40)
        self.assertEqual(stopped, "needs-hari")

    def test_invalid_transitions_fail_closed(self):
        with self.assertRaises(ValueError):
            transition("preflight", "APPROVE")


if __name__ == "__main__":
    unittest.main()
