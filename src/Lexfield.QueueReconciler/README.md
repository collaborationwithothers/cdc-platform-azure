# Lexfield.QueueReconciler

Lexfield.QueueReconciler is the scheduled backstop for the work-queue projection, the stored copy used for fast reads. It reads source task versions through task-api, compares them with QueueState, and records first-pass mismatches in QueueStore. It does not connect to tenant databases or Kafka.

## Sweep contract

The generic host runs one sweep, a check across every configured tenant, after each configured interval. Each tick first tries to acquire the single renewable `SweepLease` row in QueueStore. Only the winning replica emits `Reconciler.SweepStarted`, advances watermarks, and emits `Reconciler.SweepCompleted`. A completed event includes the total change count, including zero for a successful empty sweep.

The host renews the lease during a long sweep. Every pass-one commit is fenced by the lease owner and expiry in the same SQL transaction as its watermark update. Lease loss therefore suppresses completion and stops the host before a later tenant watermark can commit. Lease expiry permits another replica to take over on a later tick. Killed-process takeover remains unverified here; issue #328 owns that proof.

A process also rejects a tick while its prior sweep is still running. Each rejection increments `reconciler.sweep.skipped`, an in-process monotonic counter that resets when the process restarts. Monitoring must use the counter's increase over a time window, not its absolute value.

Missing watermarks and HTTP 410 responses are left unchanged for the bootstrap path owned by issue #56. Pass two, grace-window confirmation, repair, and attribution checks are later work.

## Configuration

```text
QueueReconciler:TaskApiBaseAddress=http://task-api
QueueReconciler:TaskApiBearerTokens:tenant-a=<tenant-a token supplied by the deployment>
QueueReconciler:Interval=00:10:00
QueueReconciler:LeaseDuration=00:02:00
QueueReconciler:RenewalPeriod=00:00:30
TenantManifest:Path=<shared tenant manifest JSON>
ConnectionStrings:QueueStore=<QueueStore SQL connection string>
```

The per-tenant bearer tokens are an input boundary only. Production token acquisition and refresh through Azure identity are not implemented by this ticket. Container tests use locally signed tokens accepted by the Task API production authentication pipeline.

The focused container tests start two real reconciler hosts against the real task-api authentication and HTTP pipeline plus Testcontainers SQL Server:

```text
dotnet test tests/Lexfield.QueueReconciler.Tests/Lexfield.QueueReconciler.Tests.csproj --configuration Release --filter FullyQualifiedName~SweepHost
```

The tests prove local host, HTTP, lease renewal and fencing, telemetry, and SQL behavior. They do not prove live Azure identity, deployment, production timing, killed-process recovery, or 400-tenant performance.
