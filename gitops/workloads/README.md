# Workload Applications

Terraform installs Argo CD and one Application named `root`. The `root`
Application creates `workloads`, and `workloads` creates `strimzi` and
`connect`. The `strimzi` Application installs the operator and the Kafka
resources. The `connect` Application then asks that operator to run the Kafka
Connect workers. Each Application therefore hands one smaller part of the
cluster to the next.

## Strimzi

The `strimzi` Application runs in wave 3, after the platform components. Its
first source installs the pinned operator chart from Strimzi's public OCI
registry. Its second source renders the existing Kafka resource chart under
`infra/disposable/kafka/`. Argo CD combines both sources and applies their
resources as one Application.

The Kubernetes API server removes an empty `properties` map from one status
schema node in the Strimzi Kafka custom resource definition (CRD). Argo CD
ignores only that removed map when it compares the live CRD with the chart.
The exception does not hide any other CRD change.

Keeping the Kafka chart under `infra/disposable/` does not make Terraform its
owner. Terraform installs Argo CD and the root Application only. The directory
stays where #34 first shipped it so this migration does not mix a delivery
change with a file move.

## Kafka Connect

The `connect` Application runs in wave 4 after the wave 3 `strimzi`
Application. It renders `gitops/workloads/connect`, which creates two Kafka
Connect workers in the `kafka` namespace. The parent passes the immutable
custom image reference and the existing managed identity client ID as
non-secret Helm values.

Connect is disabled when those environment-specific values are absent. The
Terraform bootstrap enables it for the disposable Azure environment, while
the kind workflow supplies a local image and synthetic client ID. The kind
values prove Kubernetes reconciliation only; they do not prove Azure identity
or an Azure Container Registry pull.
