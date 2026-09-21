# Lexfield.QueueReconciler

Lexfield.QueueReconciler checks whether the work queue still matches task-api, the service that reads current task data. The work queue is a stored copy of task data used for fast reads.

The service reads changed task versions from task-api. It compares them with the work queue and records differences in QueueStore, the SQL database that holds queue and checking data.

This detects a task update that did not reach the work queue.

The service does not read tenant databases or Kafka message streams directly.

## Scheduled check

A sweep is one check across every configured tenant. The service starts a sweep after each configured interval.

Before checking tenants, each running process tries to claim the single `SweepLease` row in QueueStore. This row records which process may save check progress and when that permission expires.

Only the process that claims the row logs `Reconciler.SweepStarted`, updates watermarks, and logs `Reconciler.SweepCompleted`. A watermark is the last task-api change version that the service finished checking.

The process extends the expiry time while a sweep is running. Every watermark update checks that the process still owns the row and that its permission has not expired.

If either check fails, SQL rejects the watermark update. The process logs `Reconciler.SweepLeaseLost` and stops before checking another tenant.

If a process stops unexpectedly, its permission eventually expires. Another process can then claim the row during a later scheduled run. Issue #328 owns the unverified test that stops a real process.

A process does not start a second sweep while its previous sweep is running. It increments `reconciler.sweep.skipped` instead.

This counter only increases while the process is running and resets after restart. Monitoring must check whether the counter increased during a time window, not whether its total is above a fixed value.

A tenant may have no watermark, or task-api may return HTTP 410 because the saved version is too old. The service leaves that tenant unchanged and logs `Reconciler.SweepIncomplete` with the tenant and reason.

The same sweep continues with later tenants but does not log `Reconciler.SweepCompleted`. Issue #56 owns the work that creates or replaces an unusable watermark.

If a connection failure or HTTP timeout prevents task-api from answering for one tenant, the service logs `Reconciler.SweepFailed` with that tenant. The same sweep continues with later tenants but does not log `Reconciler.SweepCompleted`.

Any other unexpected failure logs `Reconciler.SweepFailed`. The process remains running and attempts the next scheduled sweep.

This change only records differences. Later work will wait to see whether they persist, correct confirmed differences, and check whether every configured tenant is sending events.

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

The deployment supplies one task-api token for each tenant. This change does not obtain or refresh production tokens through Azure identity.

The container tests create local signed tokens. Task-api validates those tokens through the same authentication code used by the running application.

## Verification

The focused container tests start two Queue Reconciler processes. Both use the task-api HTTP and authentication code plus a SQL Server container.

```text
dotnet test tests/Lexfield.QueueReconciler.Tests/Lexfield.QueueReconciler.Tests.csproj --configuration Release --filter FullyQualifiedName~SweepHost
```

The tests cover local scheduling, HTTP calls, SQL updates, permission renewal, loss of update permission, logs, and the skipped-sweep counter.

The tests do not cover Azure identity, Azure deployment, timing under a production workload, recovery after the operating system stops a process, or performance with 400 tenants.
