#!/usr/bin/env python3

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any


TEMPLATE_PATH = Path(__file__).with_name("connector-template.json")
REQUIRED_FIELDS = {"tenantId": str, "database": str, "streamIsolated": bool}
PLACEHOLDER_PATTERN = re.compile(
    r"\{(tenantId|databaseName|sqlServerFqdn|bootstrapServers|routingTopic)\}"
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Generate one Debezium connector configuration per tenant."
    )
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--sql-server-fqdn", required=True)
    parser.add_argument("--bootstrap-servers", required=True)
    parser.add_argument("--output-dir", required=True, type=Path)
    return parser.parse_args()


def load_manifest(path: Path) -> list[dict[str, Any]]:
    manifest = json.loads(path.read_text())
    if not isinstance(manifest, list):
        raise ValueError("The tenant manifest must contain a JSON array.")

    seen_tenants: set[str] = set()
    for tenant in manifest:
        if not isinstance(tenant, dict):
            raise ValueError("Each tenant manifest entry must be a JSON object.")
        for field, expected_type in REQUIRED_FIELDS.items():
            if field not in tenant or type(tenant[field]) is not expected_type:
                raise ValueError(f"Tenant manifest field '{field}' has the wrong type.")
        if not tenant["tenantId"].strip() or not tenant["database"].strip():
            raise ValueError("Tenant id and database must not be blank.")
        if Path(tenant["tenantId"]).name != tenant["tenantId"]:
            raise ValueError("Tenant id must not contain a path separator.")
        if tenant["tenantId"] in seen_tenants:
            raise ValueError(f"Tenant id '{tenant['tenantId']}' appears more than once.")
        seen_tenants.add(tenant["tenantId"])
    return manifest


def replace_placeholders(value: Any, replacements: dict[str, str]) -> Any:
    if isinstance(value, str):
        return PLACEHOLDER_PATTERN.sub(lambda match: replacements[match.group(1)], value)
    if isinstance(value, list):
        return [replace_placeholders(item, replacements) for item in value]
    if isinstance(value, dict):
        return {key: replace_placeholders(item, replacements) for key, item in value.items()}
    return value


def render_connector(
    template: dict[str, Any],
    tenant: dict[str, Any],
    sql_server_fqdn: str,
    bootstrap_servers: str,
) -> dict[str, Any]:
    tenant_id = tenant["tenantId"]
    routing_topic = (
        f"workflow-transitions-{tenant_id}"
        if tenant["streamIsolated"]
        else "workflow-transitions"
    )
    return replace_placeholders(
        template,
        {
            "tenantId": tenant_id,
            "databaseName": tenant["database"],
            "sqlServerFqdn": sql_server_fqdn,
            "bootstrapServers": bootstrap_servers,
            "routingTopic": routing_topic,
        },
    )


def generate(
    manifest_path: Path,
    sql_server_fqdn: str,
    bootstrap_servers: str,
    output_dir: Path,
) -> None:
    template = json.loads(TEMPLATE_PATH.read_text())
    tenants = load_manifest(manifest_path)
    output_dir.mkdir(parents=True, exist_ok=True)
    existing = sorted(output_dir.glob("tenant-*-outbox.json"))
    if existing:
        names = ", ".join(path.name for path in existing)
        raise ValueError(f"Output directory already contains generated connector files: {names}")

    for tenant in tenants:
        connector = render_connector(template, tenant, sql_server_fqdn, bootstrap_servers)
        destination = output_dir / f"tenant-{tenant['tenantId']}-outbox.json"
        destination.write_text(json.dumps(connector, indent=2) + "\n")


def main() -> int:
    args = parse_args()
    try:
        generate(
            args.manifest,
            args.sql_server_fqdn,
            args.bootstrap_servers,
            args.output_dir,
        )
    except (OSError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
