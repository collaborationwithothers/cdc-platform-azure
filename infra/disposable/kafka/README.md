# Build-scale Kafka

Terraform installs Argo CD and the `root` Application. `root` creates
`workloads`, which creates the `strimzi` Application. `strimzi` then installs
the operator and applies this chart. The chain gives Argo CD one delivery path
from the Git repository to Kafka.

The `strimzi` Application pins the operator at 1.2.0. This chart creates the
Kafka resources for one disposable build session and pins the broker at Kafka
4.3.1. Those pins stop an upgrade from silently changing the resource schema
or broker behavior.

Terraform does not apply this chart. It stays under `infra/disposable/` to
avoid mixing the delivery migration with a file move; Argo CD is its only
delivery owner.

The cluster has one node acting as both the KRaft metadata controller and the
broker, with replication factor 1. This is a build-scale economy with no high
availability. The production design remains three brokers, replication factor
3, and `min.insync.replicas` 2, as recorded in
[the blueprint](../../../docs/blueprint.md#3-target-architecture).

The broker uses a 32 GiB persistent claim. The claim preserves broker data
across a pod restart during one disposable session and is deleted with the
Kafka resource. Destroying the disposable layer still removes all Kafka
history.

## Topics

| Topic | Partitions | Compacted |
|---|---:|---|
| `workflow-transitions` | 12 | no |
| `workflow-transitions-lexfield-003` | 12 | no |
| `workflow-transitions-parked` | 1 | no |
| `notifier-control` | 1 | no |
| `connect-signals` | 1 | no |
| `schema-history-lexfield-001` | 1 | yes |
| `schema-history-lexfield-002` | 1 | yes |
| `schema-history-lexfield-003` | 1 | yes |
| `connect-configs` | 1 | yes |
| `connect-offsets` | 25 | yes |
| `connect-status` | 5 | yes |

The Connect internal topic counts are Kafka Connect's documented defaults:
one configuration partition, 25 offset partitions, and 5 status partitions.

## Identities

- `connect` writes the transition and schema-history topics, reads and writes
  the three internal topics, and reads `connect-signals`.
- `queue-builder` reads transition topics and writes parked events.
- `notifier` reads transition and control topics and writes parked events.
- `operations` writes control messages and snapshot signals.

All four identities use mutual TLS, so both sides authenticate with
certificates. Kafka access control lists (ACLs) enforce the per-identity
operations above. Topic auto-creation is disabled because every v1 topic is
committed.

## Container verification

The `gitops-kind` workflow lets Argo CD install both sources on a kind cluster.
It waits for the operator, broker, all 11 topics, and all four users. It also
checks that every committed user still has access control list entries.
