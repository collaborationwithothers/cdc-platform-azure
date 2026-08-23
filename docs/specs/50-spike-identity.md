# Spec: identity spike (ADR-006)

ADR-006 decides workload identity end to end and then gates shipping it behind
this spike. The decision is settled; what is not settled is whether the stack
behaves. ADR-006 says plainly that no public end-to-end reference exists for
this combination, which means this spike is the only source of truth about it,
and a passing spike written up honestly becomes that reference.

Owning area: infra/disposable for the provisioning, connect/ for the connector.
Both stages are `needs-live-test`, run by Hari, serialized against every other
live ticket.

## Why it is two stages

Stage A answers "can it authenticate at all" and needs a database, a cluster,
and a pod. Stage B answers "does it stay authenticated" and needs Kafka, a
Connect worker, and a real connector holding real offsets, because the pass gate
mentions LSN resumption and only a connector has an LSN.

Splitting them matters for scheduling. Stage A can run in wave 1, immediately
after the minimal disposable slice, and its answer changes what the connect/
area builds. Stage B cannot run until the image and Strimzi are deployed. A
single combined spike would push the whole identity question to the far end of
the plan and leave four areas building against an unverified assumption.

## Stage A: authentication proof

### Preconditions

1. Persistent layer applied, including the `id-connect` user-assigned managed
   identity.
2. Minimal disposable slice applied: AKS with the OIDC issuer and workload
   identity enabled, one S3-class tenant database, Entra admin set on the logical
   server.
3. Federated credential created on `id-connect` against the AKS OIDC issuer,
   with the subject naming the Connect namespace and service account.
4. Onboarding step 4 run against the tenant database: `CREATE USER FROM EXTERNAL
   PROVIDER` for `id-connect`, plus `db_datareader` and EXECUTE on the `cdc`
   schema.
5. Onboarding steps 1, 2, 3, and 5 already run, so `dbo.Outbox` is CDC-enabled
   and has at least one row.

### Procedure

1. Deploy a probe pod running the custom Connect image, in the Connect
   namespace, with the workload identity service account and the pod label the
   webhook requires. The pod runs a small JDBC probe on the image's own
   classpath rather than a Connect worker, so stage A needs no Kafka.
2. The probe connects with `authentication=ActiveDirectoryDefault` and no
   credential of any kind in its configuration or environment.
3. The probe runs three statements and prints the results:
   - `SELECT SUSER_SNAME()`, to see which principal the server thinks connected.
   - A read against a `cdc.fn_cdc_get_all_changes_*` function for the outbox
     capture instance, to prove the grants are sufficient for the real workload.
   - An attempted write against `dbo.Outbox`, which must fail, to prove the
     grants are read-only as blueprint section 9 claims.
4. Record the image's resolved versions of mssql-jdbc, azure-identity, and
   MSAL4J. Blueprint section 13 names that coupling as a place where a wrong
   combination fails at runtime, so the working combination is part of the
   result.

### Pass gate

All four must hold:

- The probe connects with no secret present anywhere in the pod specification,
  the image, or the environment.
- `SUSER_SNAME()` returns the managed identity, not a shared or fallback
  principal.
- The CDC function read succeeds.
- The write attempt fails with a permissions error.

### Fail path

Flip to the Key Vault configuration provider with SQL authentication. Same
image, configuration change only, which is why ADR-006 requires the fallback
packaged from day one. Record precisely which of the four gates failed and what
the error was; a spike that fails informatively is still a useful artifact.

## Stage B: reconnect stress past token lifetime

### Preconditions

Stage A passed. Strimzi Kafka and a Connect cluster deployed with the custom
image and workload identity. One real Debezium connector registered against the
tenant database. The load generator available.

### Procedure

1. Record the observed access token lifetime for the identity, and run for at
   least three times that lifetime. The point is to cross the boundary several
   times, not once.
2. Run the load generator continuously at a modest steady rate for the whole
   window, so there is always a stream to lose events from.
3. Run a checker alongside, consuming `workflow-transitions` and applying the
   same version arithmetic queue-builder uses: per key, versions must form a
   gapless ascending sequence. Duplicates are recorded, not failures.
4. At intervals spread across the run, inject four disturbances, each at least
   once after the first token expiry:
   - Delete the Connect pod and let it be rescheduled.
   - Kill the connector's SQL sessions from an administrative connection, so the
     driver sees a connection death rather than a clean close.
   - Scale the Connect deployment down to one worker and back to two, forcing
     reassignment.
   - Toggle database auditing on and then off.
5. The auditing toggle is the hypothesis ADR-006 names: whether it invalidates
   the server security cache in a way that kills live token-authenticated
   connections. It is a hypothesis under test, not a documented trigger, and the
   write-up says so whichever way it goes.
6. Record throughout: connector state transitions, restart counts, the duration
   of every window where the connector was not RUNNING, and any operator action
   taken. Operator actions should be zero; the count is recorded so a nonzero
   value cannot be forgotten.

### Pass gate

ADR-006 states the gate as: every reconnect re-authenticates unattended and
resumes from the correct LSN. Concretely, all five:

- Zero operator interventions across the whole run.
- The connector never reaches a terminal FAILED state.
- The checker reports zero gaps in per-key version sequences.
- Every disturbance is followed by a return to RUNNING without a configuration
  change.
- No unavailability window exceeds a stated bound. SPEC-LEVEL bound: 120
  seconds. A longer window is not automatically a failure, but it is a recorded
  finding against blueprint failure mode 3 and it must be explained.

Duplicates are expected and are not a failure. At-least-once delivery is the
contract and the consumers absorb it by construction.

Watch specifically for the non-progressing retry loop blueprint failure mode 3
names: reconnects that re-run schema-history recovery on fresh connections
without making forward progress. A connector that is RESTARTING forever while
looking busy passes a naive health check and fails this gate.

### Fail path

Same as stage A: flip to Key Vault with SQL authentication by configuration.
Record which gate failed, at which disturbance, and after how long.

## Artifacts

1. `docs/labs/identity-spike.md`. Dated, environment stated beside every figure,
   including the resolved library versions from stage A. States the result for
   each gate, the auditing hypothesis outcome either way, and every unavailability
   window measured. Written honestly enough that a reader can tell whether their
   own stack matches this one.
2. An appended result section on ADR-006 in `docs/decisions/`, recording which
   path shipped and why.
3. The shipping connector authentication configuration, in `connect/connectors/`,
   which is either the primary path or the Key Vault fallback depending on the
   gates.

## Candidate tickets

| # | Behavior | Verification | Size forecast |
| --- | --- | --- | --- |
| S1 | Probe application and its pod manifest, plus the federated credential wiring. Stage A executed and its four gates recorded on the issue. | live | 5 files, 280 lines |
| S2 | Checker application applying version arithmetic to the live stream, container-tested before it is trusted with a live run. | containers | 4 files, 300 lines |
| S3 | Stage B executed: the disturbance schedule run, all five gates recorded. | live | 2 files, 200 lines |
| S4 | `docs/labs/identity-spike.md` and the ADR-006 result section. | unit | 3 files, 380 lines |

S2 is deliberately container-verified first. A checker that itself has a bug
would either hide a real gap or invent one, and discovering that during a
three-hour live run wastes the serialized live slot.
