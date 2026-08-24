#!/usr/bin/env python3
"""Validate rendered Strimzi resources against schemas from pinned CRDs."""

import argparse
import sys
from pathlib import Path

import jsonschema
import yaml


def documents(path: Path) -> list[dict]:
    with path.open(encoding="utf-8") as stream:
        return [document for document in yaml.safe_load_all(stream) if document]


def crd_schemas(crds: list[dict]) -> dict[tuple[str, str, str], dict]:
    schemas = {}
    for crd in crds:
        if crd.get("kind") != "CustomResourceDefinition":
            continue
        spec = crd["spec"]
        for version in spec["versions"]:
            if "schema" not in version:
                continue
            key = (spec["group"], version["name"], spec["names"]["kind"])
            schemas[key] = version["schema"]["openAPIV3Schema"]
    return schemas


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--crds", required=True, type=Path)
    parser.add_argument("manifest", type=Path)
    args = parser.parse_args()

    schemas = crd_schemas(documents(args.crds))
    errors = []
    checked = 0
    for resource in documents(args.manifest):
        api_version = resource.get("apiVersion", "")
        if "/" not in api_version:
            continue
        group, version = api_version.split("/", 1)
        key = (group, version, resource.get("kind", ""))
        schema = schemas.get(key)
        name = resource.get("metadata", {}).get("name", "<unnamed>")
        if schema is None:
            errors.append(f"{key[2]} {name}: no schema in the pinned CRDs")
            continue
        checked += 1
        validator = jsonschema.Draft7Validator(schema)
        for error in sorted(validator.iter_errors(resource), key=lambda item: list(item.path)):
            location = ".".join(str(part) for part in error.path) or "<root>"
            errors.append(f"{key[2]} {name} at {location}: {error.message}")

    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    if checked == 0:
        print("No Strimzi custom resources were found.", file=sys.stderr)
        return 1
    print(f"Validated {checked} Strimzi custom resources against pinned CRDs.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
