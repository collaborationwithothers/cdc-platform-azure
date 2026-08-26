# Operator scripts

The scripts a runbook's first step runs. They change connector and consumer
state and nothing else: neither touches tenant data, and neither reads a
credential from this repository.

`pause-connector.sh` is the one the runbooks lean on hardest.
[observability.md](../../docs/observability.md) section 8 sets the bar with the
`attribution-breach` anchor, whose first action stops capture before any
diagnosis, because every second of diagnosis while a connector is misrouting is
more contaminated rows. A step that stops there has not finished: Kafka Connect
answers a pause request with `202 Accepted` and moves the connector and its tasks
afterwards, so a script that returns on the acknowledgement tells an operator
capture has stopped when it may still be running. `pause-connector.sh` polls
`GET /connectors/{name}/status` until the connector and every task report
`PAUSED`, then prints what it saw.

## Before you run anything

Neither Kafka Connect's REST listener nor the Kafka broker has public ingress
(blueprint section 9), so forward the port you need first. The service names come
from the cluster rather than from here, because the resources that carry them are
owned by `infra/disposable/`:

```
kubectl -n <namespace> get svc
kubectl -n <namespace> port-forward svc/<connect-rest-service> 8083:8083
```

## The scripts

| Script | Arguments | What it does |
| --- | --- | --- |
| `pause-connector.sh` | `<tenantId>` | Pauses `tenant-<tenantId>-outbox` and waits until it and its tasks report `PAUSED`. |
| `resume-connector.sh` | `<tenantId>` | Resumes the same connector and waits until it reports `RUNNING`. |

Called with no arguments, each script prints the arguments it needs and exits
non-zero. Every failure prints a line beginning `FAIL:` on standard error and
exits non-zero, so a runbook step can be checked by its exit code.

### Environment

| Variable | Default | Used by |
| --- | --- | --- |
| `CONNECT_URL` | `http://localhost:8083` | both scripts |
| `CONNECT_TIMEOUT_SECONDS` | `60` | both scripts |
| `CONNECT_POLL_INTERVAL_SECONDS` | `1` | both scripts |

Nothing under `scripts/ops/` reads a credential, and no credential belongs in
this repository at all.

### What a timeout means

A connector or task that had already failed does not transition on a pause
request; Connect rejects the change and leaves it `FAILED`. The wait is therefore
bounded rather than indefinite, and a timeout prints the last states seen. Read
those states: a `FAILED` connector needs the `recover-connector` runbook, not
another pause.

## Verifying a change

`tests/Lexfield.Ops.Tests` runs both scripts against a Kafka Connect container
and a Kafka broker. `dotnet test src/Lexfield.slnx` runs them.
