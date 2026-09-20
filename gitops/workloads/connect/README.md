# Kafka Connect workers

Kafka Connect is the distributed runtime that hosts Debezium connectors. A
Debezium connector reads committed changes from one tenant Azure SQL database
and publishes them to Kafka topics. A topic is a named stream of messages. This
chart deploys two workers because the design intends the second worker to be
available for task reassignment if the first worker stops. The current kind
check proves that both workers start; it does not stop a worker or measure task
recovery.

The chart creates one Strimzi `KafkaConnect` resource named `connect` in the
`kafka` namespace. Strimzi is the Kubernetes operator that turns this resource
into the worker pods and their service account.

## Runtime contract

The workers use Kafka 4.3.1, matching the broker and the published custom image.
They connect to the internal TLS listener and authenticate with the existing
`connect` Kafka user certificate. The three internal topics store connector
configuration, source offsets, and task status:

- `connect-configs`
- `connect-offsets`
- `connect-status`

The worker group is `connect-cluster`. Connector registrations remain outside
this chart because onboarding owns those REST API calls.

The two worker settings make separate behavior explicit:

- `connect.protocol: sessioned` pins a value accepted by the
  [Apache worker reference](https://kafka.apache.org/43/configuration/kafka-connect-configs/#connectconfigs_connect.protocol).
  The reference does not describe connector-assignment behavior, so V6 leaves
  incremental cooperative assignment unverified.
- `scheduled.rebalance.max.delay.ms: 300000` lets the group leader wait for up
  to 5 minutes after a worker departs before reassigning that worker's
  connectors and tasks. Those connectors and tasks remain unassigned during
  the wait, as defined by the [Apache worker reference](https://kafka.apache.org/43/configuration/kafka-connect-configs/#connectconfigs_scheduled.rebalance.max.delay.ms).

[V6 in the verification register](../../../docs/specs/02-verification-register.md#v6-connect-rebalance-protocol-default)
did not establish relying on the protocol default as a documentation-backed
contract. An upgrade therefore cannot change this deployment silently by
changing a default.

## Required inputs

The parent `workloads` Application supplies two non-secret values:

- `image` is the immutable Azure Container Registry reference for the custom
  worker image. Production uses a digest-pinned reference. The kind check uses
  a locally built tag and does not prove Azure registry access.
- `identityClientId` is the client ID of the existing Connect user-assigned
  managed identity. It is read from the persistent Terraform layer.

Rendering fails when either value is absent. The chart never carries a registry
password, database credential, connector configuration, or Kubernetes Secret.

## Workload identity boundary

The generated service account is `connect-connect`. Its client ID annotation
selects the existing managed identity. The worker pod label opts the pod into
the Azure workload identity webhook. The disposable Terraform layer creates a
federated credential for the exact
`system:serviceaccount:kafka:connect-connect` subject.

These declarations do not prove a token exchange. The kind check confirms that
Strimzi propagates the annotation and label to generated Kubernetes objects.
The live identity spike owns Azure token exchange and database authentication.

## Verification

The `gitops-kind` workflow performs four checks without Azure:

1. Helm renders exactly one `KafkaConnect` and no connector or Secret.
2. The pinned Strimzi custom resource definition accepts the resource shape.
3. Argo CD reconciles the Connect Application after Strimzi and Kafka.
4. Strimzi starts exactly two ready worker pods from the locally built image.

The workflow also checks the generated service account and pods for the exact
workload identity metadata. It does not prove an Azure Container Registry pull,
Azure workload identity, or end-to-end SQL Server-to-Kafka delivery.
