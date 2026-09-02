# Lexfield.QueueReconciler

Lexfield.QueueReconciler provides the pass-one comparison used by the work-queue backstop. It reads the Task API Change Tracking feed, compares source task versions with QueueState, and records first-pass mismatches in QueueStore. It does not connect to tenant databases or Kafka.

## Sweep contract

`PassOne` is explicitly invoked with a lease supplied by its scheduled host. It reads one tenant's watermark, calls the authenticated Task API changes endpoint, compares returned versions with QueueState, and commits observations plus the next watermark through `ReconcilerStateStore`. A stale lease or failed commit leaves the watermark unchanged. A successful empty feed is committed with the same watermark and returns a zero change count.

Missing watermarks and HTTP 410 responses are left unchanged for the bootstrap path owned by issue #56. Pass two, grace-window confirmation, repair, and attribution checks are later work.

## Configuration

```text
QueueReconciler:TaskApiBaseAddress=http://task-api
QueueReconciler:TaskApiBearerToken=<token supplied by the deployment>
ConnectionStrings:QueueStore=<QueueStore SQL connection string>
```

The bearer token is an input boundary only. Production token acquisition through Azure identity is not implemented by this ticket. Container tests use locally signed tokens accepted by the Task API production authentication pipeline.

These tests prove local HTTP, authentication, comparison, and SQL transaction behavior. They do not prove scheduling, lease failover, Azure identity, deployment, production timing, or 400-tenant performance. Scheduling and lease ownership are implemented by issue #327.
