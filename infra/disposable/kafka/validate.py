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


def validation_context() -> list[str]:
    return [
        "Context: this validator checks the rendered Kubernetes manifest for the Kafka platform.",
        "Kafka is the message-streaming system that carries events between services.",
        "Strimzi is the Kubernetes operator that runs Kafka.",
        "A custom resource is a Kubernetes object that asks Strimzi to manage a Kafka component.",
        "A custom resource definition (CRD) is the schema that lists the fields that resource accepts.",
    ]


def main() -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Validate a rendered Strimzi manifest, the YAML artifact emitted by "
            "Helm, the Kubernetes package renderer, against pinned "
            "CustomResourceDefinitions (CRDs), the YAML schemas that define "
            "which fields Strimzi, the Kubernetes operator that runs Kafka, "
            "accepts. This is an offline check and does not contact a Kubernetes "
            "cluster."
        ),
        epilog=(
            "Existing rule: every custom resource with an API group and version in the "
            "rendered manifest must match a schema from --crds. Exit status 0 means at least one "
            "resource matched and every checked resource passed. Exit status 1 "
            "means a schema is missing, a schema rule failed, or no resource "
            "was checked. Example: python3 infra/disposable/kafka/validate.py "
            "--crds pinned-crds.yaml rendered.yaml"
        ),
    )
    parser.add_argument(
        "--crds",
        required=True,
        type=Path,
        help="YAML artifact containing the pinned Strimzi CustomResourceDefinitions.",
    )
    parser.add_argument(
        "manifest",
        type=Path,
        help="YAML artifact rendered from the Strimzi resource chart.",
    )
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
            errors.append(
                "\n".join(
                    [
                        "Validation failed for the rendered Strimzi manifest.",
                        *validation_context(),
                        f"Input artifacts: rendered manifest '{args.manifest}'; pinned CRDs '{args.crds}'.",
                        "Existing rule: every Strimzi custom resource, a Kubernetes object managed by the Kafka operator, must match a schema for its API group, version, and kind in the pinned CRDs.",
                        f"Resource: {key[2]} {name} uses apiVersion {api_version}.",
                        "Consequence: this resource could not be checked, so the validator exits with status 1 and does not claim that the rendered manifest is acceptable.",
                        "Safe correction: pass a pinned CRD artifact that intentionally defines this API group, version, and kind, or correct the source manifest to an API version already defined by the pinned CRDs, then rerun the validator.",
                        "Proven boundary: no matching schema was found for this resource.",
                        "Unverified boundary: the resource fields, Kubernetes admission, and Strimzi reconciliation were not proved.",
                    ]
                )
            )
            continue
        checked += 1
        validator = jsonschema.Draft7Validator(schema)
        for error in sorted(validator.iter_errors(resource), key=lambda item: list(item.path)):
            location = ".".join(str(part) for part in error.path) or "<root>"
            errors.append(
                "\n".join(
                    [
                        "Validation failed for the rendered Strimzi manifest.",
                        *validation_context(),
                        f"Input artifacts: rendered manifest '{args.manifest}'; pinned CRDs '{args.crds}'.",
                        f"Existing rule: the pinned CRD schema for {key[2]} {name} rejects field '{location}': {error.message}",
                        "Consequence: the rendered resource may be rejected by Kubernetes or Strimzi, so the validator exits with status 1.",
                        f"Safe correction: change the Helm values or source YAML that produced '{location}' so it satisfies the pinned CRD rule, then rerun the validator.",
                        f"Proven boundary: this run found a schema error at '{location}' for {key[2]} {name}.",
                        "Unverified boundary: no Kubernetes API or Strimzi operator was contacted, so admission and reconciliation were not proved.",
                    ]
                )
            )

    if errors:
        print("\n\n".join(errors), file=sys.stderr)
        return 1
    if checked == 0:
        print(
            "\n".join(
                [
                    "No Strimzi custom resources were found in the rendered manifest.",
                    *validation_context(),
                    f"Input artifacts: rendered manifest '{args.manifest}'; pinned CRDs '{args.crds}'.",
                    "Existing rule: the validator must check at least one custom resource with an API group and version against a schema loaded from the pinned CRDs before it can report success.",
                    "Consequence: the validator exits with status 1; this run provides no schema proof for the rendered resources.",
                    "Safe correction: render the workload chart into the manifest input and confirm that --crds points to the pinned CRD artifact, then rerun the validator.",
                    "Proven boundary: zero custom resources matched a loaded schema in this run.",
                    "Unverified boundary: resource schema validity, Kubernetes admission, and Strimzi reconciliation.",
                ]
            ),
            file=sys.stderr,
        )
        return 1
    print(
        "\n".join(
            [
                f"Validated {checked} Strimzi custom resources against pinned CRDs.",
                *validation_context(),
                f"Input artifacts: rendered manifest '{args.manifest}'; pinned CRDs '{args.crds}'.",
                "Existing rule: every checked custom resource matched a schema loaded from the pinned CRDs and produced no schema errors.",
                "Consequence: the checked resource shapes are compatible with the pinned schemas, so this offline check found no schema rejection.",
                "Safe correction: none for this schema check.",
                f"Proven boundary: {checked} custom resource(s) passed validation against the pinned CRDs.",
                "Unverified boundary: no Kubernetes API or Strimzi operator was contacted, so admission, reconciliation, broker health, and event delivery were not proved.",
            ]
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
