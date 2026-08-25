# Workload Applications

Terraform installs Argo CD and one Application named `root`. The `root`
Application creates `workloads`, and `workloads` creates `strimzi`. The
`strimzi` Application installs the operator and the Kafka resources. Each
Application therefore hands one smaller part of the cluster to the next.

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

Wave 4 remains reserved for Kafka Connect and platform services. Those
Applications are future work.
