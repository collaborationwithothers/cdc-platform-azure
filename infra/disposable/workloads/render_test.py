#!/usr/bin/env python3
"""Assert the public telemetry contract of the rendered workload chart."""

from pathlib import Path
import re
import subprocess
import sys


CHART = Path(__file__).resolve().parent


def main() -> int:
    result = subprocess.run(
        [
            "helm",
            "template",
            "cdc-platform-workloads",
            str(CHART),
            "--set-string",
            "applicationInsightsConnectionString=test-only-placeholder",
        ],
        check=True,
        capture_output=True,
        text=True,
    )
    rendered = result.stdout
    deployments = re.findall(
        r"(?ms)^kind: Deployment\n.*?(?=^---$|\Z)", rendered
    )
    names = {
        re.search(r"(?m)^  name: ([^\n]+)$", deployment).group(1)
        for deployment in deployments
    }

    assert len(deployments) == 4, "the chart must render four Deployments"
    assert names == {"task-api", "queue-builder", "queue-reconciler", "notifier"}
    assert "kind: Secret" in rendered
    assert all(
        "name: APPLICATIONINSIGHTS_CONNECTION_STRING\n              valueFrom:\n                secretKeyRef:\n                  name: platform-application-insights"
        in deployment
        for deployment in deployments
    )
    assert all(
        "name: OTEL_TRACES_SAMPLER\n              value: \"microsoft.fixed_percentage\""
        in deployment
        for deployment in deployments
    )
    assert all(
        "name: OTEL_TRACES_SAMPLER_ARG\n              value: \"1.0\"" in deployment
        for deployment in deployments
    )
    assert not re.search(r"(?:InstrumentationKey|IngestionEndpoint)=", rendered)
    print("Rendered workload telemetry contract passed.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, subprocess.CalledProcessError) as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
