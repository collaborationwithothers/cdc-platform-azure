# Spec: fleet density lab

Blueprint sections 2, 7, and 11 make Connect fleet density at 400 connectors
something the build demonstrates by measurement rather than asserts. This lab is
where that measurement happens.

It runs on a local kind cluster, not Azure. kind is a tool that runs a
Kubernetes cluster inside Docker containers on one machine, so Strimzi and the
Connect workers behave as they would on AKS without anything being provisioned
or billed. That matters for planning: the lab
is containers-class verification, so it does not consume the serialized live
slot and does not wait behind the identity spike.

Owning area: connect/, with infra/disposable contributing the Strimzi manifests.
Paths owned: `labs/fleet-density/`, `docs/labs/fleet-density.md`.

## What it measures

Blueprint section 7 names four things:

1. Memory per connector task.
2. Startup storm behaviour, meaning what happens when many connectors are
   registered at once.
3. Rebalance duration at 400 connectors on 2 to 3 workers.
4. Density ceiling per worker.

It also answers V5, the Strimzi build-pod service account question, which
blueprint section 3 requires validated against the targeted Strimzi version
before the claim ships publicly.

## Environment

- kind cluster, node count and resource limits recorded and held constant across
  runs.
- Strimzi at the version this repo pins. The pin is the point; a measurement
  against a different version answers a different question.
- Kafka, KRaft mode, single broker, matching the build-scale configuration.
- `KafkaConnect` with 2 workers, then 3, using the custom image from the connect/
  area. Worker memory limits recorded.
- SQL Server containers hosting the target databases, each with the tenant schema
  and CDC enabled on `dbo.Outbox`.

### The synthetic boundary, stated up front

400 connectors do not require 400 separate SQL Server instances. The lab targets
Connect worker behaviour, not database load, so the databases are spread across a
small number of SQL Server containers, SPEC-LEVEL four containers at 100
databases each.

This is a real limitation and it appears beside every published figure, not in a
footnote. The numbers describe Connect at 400 connectors against lightly loaded
databases. They do not describe 400 production tenant databases under load, and
blueprint section 2 already separates what v1 demonstrates from what it asserts.

Risk, recorded now so the ticket is not surprised: 100 CDC-enabled databases per
SQL Server container may be more than the container can carry, because each
carries its own capture machinery. If so, the lab reduces the database count per
container and adds containers; if the host cannot carry 400 at all, the lab
records the maximum actually reached, states it, and extrapolates with a labelled
model rather than quietly reporting a smaller number as if it were the target.

## Procedure

1. Bring up the cluster, Strimzi, Kafka, and 2 Connect workers. Record baseline
   worker resident memory with zero connectors.
2. Bring up the SQL Server containers and create the databases with the
   onboarding T-SQL.
3. Register connectors in batches of 50. After each batch, record: worker
   resident memory, JVM heap used, task count per worker, and the time from the
   last registration in the batch to all connectors in the batch reaching
   RUNNING.
4. At 400 connectors, let the cluster settle and record the steady state.
5. Rebalance measurement: delete one worker pod. Record the time until every
   connector returns to RUNNING, and record how many connectors changed worker.
   With incremental cooperative rebalancing, only the dead worker's connectors
   should move; the count is how that claim gets checked rather than repeated.
6. Repeat step 5 with 3 workers.
7. Density ceiling: continue registering connectors past 400 on a fixed worker
   count until either a worker exceeds its memory limit or the rebalance time
   crosses the gate below. Record the count at which it happened and which limit
   bound first.
8. V5: attempt a `KafkaConnect` build using the Strimzi operator's own build
   mechanism on the pinned version, and record whether the build pod's service
   account can carry the workload identity annotation.

Every step records the wall-clock time and the cluster state, so a run can be
reconstructed from the artifact rather than from memory.

## Gates

A measurement lab does not pass or fail by producing a number. These are the
gates that do exist.

**G1, completeness.** The run produces all four figures with the environment
stated beside each. A run that produces three figures is repeated. It is never
resolved by publishing three and describing the fourth qualitatively.

**G2, the claim gate for V5.** The Strimzi build-pod question is answered against
the pinned version. If the answer refutes the premise, blueprint section 3's
sentence explaining why the image is baked outside the operator is wrong as
written and must be corrected before any public document repeats it. The custom
image itself is unaffected; only the published reason changes.

**G3, rebalance bound.** SPEC-LEVEL: all connectors return to RUNNING within 120
seconds of a worker loss at 400 connectors on 2 workers. Exceeding it is a
recorded finding against blueprint failure mode 10, not a blocker for v1, because
v1 runs 3 connectors and this figure is fleet-scale design reasoning.

**G4, cooperative rebalancing actually cooperates.** Only the lost worker's
connectors change ownership. If connectors on surviving workers also move, the
incremental cooperative claim in blueprint section 3 is not holding in this
configuration, and that is a finding worth more than the memory numbers.

**G5, density stance.** The measured ceiling per worker is stated as connectors
per worker at a named memory limit. If the ceiling falls below 200, the two-worker
fleet-scale stance in blueprint section 3 does not carry 400 connectors and the
document must say what worker count does.

## Artifact

`docs/labs/fleet-density.md`. Dated. Contains:

- The environment, in enough detail to re-run: kind node configuration, Strimzi
  version, image digest, worker memory limits, database layout.
- The four figures, each beside its environment and the synthetic boundary.
- The V5 answer, and if it refutes the premise, the corrected rationale.
- The rebalance ownership-change counts, which is the evidence for G4.
- The extrapolation model, if the host could not reach 400, labelled as
  extrapolation with its basis.

Blueprint section 13 lists Connect distributed-mode internals as a learning item
and names this lab as where the density and rebalance numbers come from, so the
artifact is also the record of what was actually learned rather than made to
work.

## Dependencies

Blocked by: the connect/ area's custom image ticket (C3) and connector
configuration generator (C4), and infra/disposable's Strimzi manifests (D6, D7),
since the lab reuses them rather than writing a second set.

Blocks: nothing in code. It blocks the public claim in blueprint section 3 and
anything in the README that repeats a density or rebalance figure.

## Candidate tickets

| # | Behavior | Verification | Size forecast |
| --- | --- | --- | --- |
| L1 | Lab harness: kind configuration, Strimzi and Kafka manifests, SQL Server container set with 400 databases created by the onboarding runner. | containers | 8 files, 420 lines |
| L2 | Measurement scripts: batched registration with per-batch memory and startup timing, plus the rebalance measurement including ownership-change counts. | containers | 5 files, 380 lines |
| L3 | Density ceiling run and the V5 build-pod check. | containers | 3 files, 240 lines |
| L4 | `docs/labs/fleet-density.md` with all four figures, the V5 answer, and the synthetic boundary stated beside each figure. | unit | 2 files, 400 lines |

L1 is the ticket most likely to run into the density risk above. If it does, it
stops and records the maximum reached on the issue rather than silently reducing
the target, per AGENTS.md's rule about not modifying a ticket's scope.
