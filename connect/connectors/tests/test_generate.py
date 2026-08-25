import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


CONNECTORS_DIR = Path(__file__).resolve().parents[1]
FIXTURES_DIR = Path(__file__).resolve().parent
MANIFEST = FIXTURES_DIR / "tenant-manifest.json"
GOLDEN = FIXTURES_DIR / "connector-configs.golden"


class ConnectorGeneratorTests(unittest.TestCase):
    def test_three_tenants_match_the_golden_snapshot(self) -> None:
        with tempfile.TemporaryDirectory() as output_dir:
            result = subprocess.run(
                [
                    sys.executable,
                    str(CONNECTORS_DIR / "generate.py"),
                    "--manifest",
                    str(MANIFEST),
                    "--sql-server-fqdn",
                    "sql.lexfield.test",
                    "--bootstrap-servers",
                    "kafka:9092",
                    "--output-dir",
                    output_dir,
                ],
                check=False,
                capture_output=True,
                text=True,
            )

            self.assertEqual("", result.stderr)
            self.assertEqual(0, result.returncode)
            generated = snapshot(Path(output_dir))
            self.assertEqual(GOLDEN.read_text(), generated)

    def test_isolation_changes_only_the_router_target(self) -> None:
        manifest = json.loads(MANIFEST.read_text())
        shared = manifest[2] | {"streamIsolated": False}
        isolated = manifest[2]

        shared_config = render_one(shared)
        isolated_config = render_one(isolated)
        differences = {
            key: (shared_config[key], isolated_config[key])
            for key in shared_config
            if shared_config[key] != isolated_config[key]
        }

        self.assertEqual(
            {
                "transforms.outbox.route.topic.replacement": (
                    "workflow-transitions",
                    "workflow-transitions-lexfield-003",
                )
            },
            differences,
        )

    def test_configs_use_the_verified_stock_chain_without_secrets(self) -> None:
        for tenant in json.loads(MANIFEST.read_text()):
            config = render_one(tenant)
            serialized = json.dumps(config)

            self.assertEqual("outbox,tenantHeader", config["transforms"])
            self.assertEqual("Id", config["transforms.outbox.table.field.event.id"])
            self.assertEqual("AggregateId", config["transforms.outbox.table.field.event.key"])
            self.assertEqual("Payload", config["transforms.outbox.table.field.event.payload"])
            self.assertEqual("AggregateType", config["transforms.outbox.route.by.field"])
            self.assertEqual(
                "EventType:header:eventType,Id:header:eventId,TraceParent:header:traceparent",
                config["transforms.outbox.table.fields.additional.placement"],
            )
            self.assertEqual("true", config["driver.encrypt"])
            self.assertEqual("source,kafka", config["signal.enabled.channels"])
            self.assertEqual(tenant["tenantId"], config["transforms.tenantHeader.value.literal"])
            self.assertNotIn("database.encrypt", config)
            self.assertNotIn("database.user", config)
            self.assertNotIn("database.password", config)
            self.assertNotIn("PrefixKey", serialized)
            self.assertNotIn("rekey", serialized.lower())


def render_one(tenant: dict[str, object]) -> dict[str, str]:
    with tempfile.TemporaryDirectory() as output_dir:
        manifest_path = Path(output_dir) / "manifest.json"
        manifest_path.write_text(json.dumps([tenant]))
        subprocess.run(
            [
                sys.executable,
                str(CONNECTORS_DIR / "generate.py"),
                "--manifest",
                str(manifest_path),
                "--sql-server-fqdn",
                "sql.lexfield.test",
                "--bootstrap-servers",
                "kafka:9092",
                "--output-dir",
                output_dir,
            ],
            check=True,
            capture_output=True,
            text=True,
        )
        config_path = Path(output_dir) / f"tenant-{tenant['tenantId']}-outbox.json"
        return json.loads(config_path.read_text())["config"]


def snapshot(output_dir: Path) -> str:
    sections = []
    for config_path in sorted(output_dir.glob("*.json")):
        sections.append(f"=== {config_path.name} ===\n{config_path.read_text()}")
    return "".join(sections)


if __name__ == "__main__":
    unittest.main()
