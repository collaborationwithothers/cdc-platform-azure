# Operator scripts

Three scripts a runbook's first step runs. They change connector and consumer
state and nothing else: none of them touches tenant data, and none reads a
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
| `notifier-control.sh` | `<retry\|skip> <partition> <offset> <reason>` | Writes one control message to the `notifier-control` topic. |

Called with no arguments, each script prints the arguments it needs and exits
non-zero. Every failure prints a line beginning `FAIL:` on standard error and
exits non-zero, so a runbook step can be checked by its exit code.

### Environment

| Variable | Default | Used by |
| --- | --- | --- |
| `CONNECT_URL` | `http://localhost:8083` | the two connector scripts |
| `CONNECT_TIMEOUT_SECONDS` | `60` | the two connector scripts |
| `CONNECT_POLL_INTERVAL_SECONDS` | `1` | the two connector scripts |
| `KAFKA_BOOTSTRAP_SERVERS` | `localhost:9093` | `notifier-control.sh` |
| `KAFKA_CLIENT_CONFIG` | unset | `notifier-control.sh` |
| `KAFKA_BIN_DIR` | unset, so `PATH` is searched | `notifier-control.sh` |

`KAFKA_CLIENT_CONFIG` names a Kafka client properties file. The cluster's only
listener is TLS with mutual authentication, so the console producer needs the
keystore and truststore that Strimzi puts in the cluster's own Kubernetes
secrets. Extract them to a directory outside this repository, write the
properties file there, and point the variable at it. Nothing under `scripts/ops/`
reads a credential, and no credential belongs in this repository at all.

### What a timeout means

A connector or task that had already failed does not transition on a pause
request; Connect rejects the change and leaves it `FAILED`. The wait is therefore
bounded rather than indefinite, and a timeout prints the last states seen. Read
those states: a `FAILED` connector needs the `recover-connector` runbook, not
another pause.

### Which notifier verb

`retry` fixes the processing, not the message: use it when the message is fine
and something around it was broken, such as a failing downstream sender that has
now been redeployed. `skip` accepts the loss for a message that will never
succeed, such as one whose `tenantId` header is missing, which is permanently
malformed on the topic. Retrying that one fails every time.
[docs/specs/23-src-notifier.md](../../docs/specs/23-src-notifier.md) has the full
argument. The reason is recorded with the message, so a skipped notification has
a named owner rather than vanishing, and the script refuses an empty one.

## Verifying a change

`tests/Lexfield.Ops.Tests` runs the connector scripts against a Kafka Connect
container and a Kafka broker, and runs the notifier script against a stub
producer. `dotnet test src/Lexfield.slnx` runs them.
