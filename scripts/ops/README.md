# Operator scripts

These scripts give an operator bounded controls and inspections for the
change-capture platform. The connector scripts pause or resume one tenant's
Debezium connector through Kafka Connect. The notifier script answers a paused
notifier consumer for one Kafka message. The task-api service owns workflow-task
writes. Its token inspector decodes a JSON Web Token (JWT), a signed identity
claim payload, and prints only the claim shape approved for live evidence.

The scripts do not change tenant data. They do not read credentials from this
repository. Each script names the request it sent, the state or message it
observed, and the next place to look when the operation fails.

## Terms used by the output

- A Debezium connector reads committed changes from one tenant database and
  publishes them through Kafka Connect.
- Kafka Connect is the service that runs and reports the connector. `RUNNING`
  means Connect reports the connector and its tasks are running. `PAUSED` means
  Connect reports that the connector and its tasks are paused.
- A Kafka topic is a named stream. A partition is one ordered log inside that
  topic. An offset is the position of one message within its partition.
- The notifier consumer reads workflow events and sends notifications. It pauses
  only the affected partition when it cannot send one notification, so an
  operator can retry or skip that message.

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

## Before running connector or notifier controls

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
| `inspect-taskapi-token.sh` | no arguments; token on standard input | Decodes one JWT payload and prints only allowlisted claim-shape evidence. |
| `capture-taskapi-delegated-tokens.sh` | no arguments; exported coordinates | Runs two user sign-ins and prints only the inspector summaries. |

### Capture the two task-api user tokens

After the [live preconditions](../../docs/runbooks/verify-taskapi-token-claims.md#1-require-every-live-precondition)
pass, run Section 2's complete block. It invokes Bash explicitly and exports
`tenant_id`, `user_client_id`, and `taskapi_resource` to the capture process.
The script requests `Tasks.Write openid profile` and `Tasks.Write openid`,
keeping `openid` constant. Follow both sign-in prompts with the same account.
Standard error contains temporary sign-in codes; never include it in reports.
Standard output contains only the two labelled summaries, after both succeed.
Tokens stay in memory and inspector pipes. Failure prints a bounded diagnostic
and stops without creating Azure resources or publishing partial summaries.

### Inspect one task-api token

The caller must disable shell tracing before `$TOKEN` expands. The inspector also
disables its own tracing, accepts a token only through standard input, never
prints the raw token, and rejects malformed input before printing payload
fragments. Its output contains the token version, literal `idtyp`,
presence booleans for `tid`, `oid`, `azp`, and `appid`, permission names from
`scp` and `roles`, and the `sub == oid` boolean. It never prints identifier
values. Payload decoding is inspection, not token-signature validation.

```text
set +x
printf '%s\n' "$TOKEN" | scripts/ops/inspect-taskapi-token.sh
```

Use the full bounded capture procedure in
[verify-taskapi-token-claims.md](../../docs/runbooks/verify-taskapi-token-claims.md).

### Pause or resume one connector

Use `pause-connector.sh` when a runbook tells you to stop capture before
diagnosing a tenant stream. Use `resume-connector.sh` after the cause is fixed.
Both commands send the REST request, then poll until the connector and every
task report the requested state. An HTTP acknowledgement alone does not prove
that the state transition has finished.

```text
CONNECT_URL=http://localhost:8083 \
  scripts/ops/pause-connector.sh alpha

CONNECT_URL=http://localhost:8083 \
  scripts/ops/resume-connector.sh alpha
```

Expected success output names the request and the observed states:

```text
request: pause Kafka Connect Debezium connector tenant-alpha-outbox at http://localhost:8083. Requested state: PAUSED. Waiting for the observed connector and task states.
success: pause completed for Kafka Connect Debezium connector tenant-alpha-outbox. Requested state: PAUSED. Observed states: connector PAUSED, task 0 PAUSED.
```

The same shape appears for resume, with `resume`, `RUNNING`, and the observed
`RUNNING` states.

### Answer one notifier partition

Use `retry` when the message is valid and the surrounding failure is fixed, such
as a downstream sender that has been redeployed. Use `skip` only when the
message cannot succeed, such as a permanently malformed event. The reason is
recorded in the control message so an operator can explain why a notification
was skipped.

The command below sends a skip answer for partition 7, offset 4102:

```text
KAFKA_BOOTSTRAP_SERVERS=localhost:9093 \
KAFKA_CLIENT_CONFIG=/tmp/notifier-client.properties \
  scripts/ops/notifier-control.sh skip 7 4102 \
  "tenantId header was missing after the 09:40 deploy"
```

The command writes this JSON shape to the `notifier-control` topic. The field
names and values are part of the notifier contract and are not changed by this
script:

```json
{"action": "skip", "partition": 7, "offset": 4102, "reason": "tenantId header was missing after the 09:40 deploy"}
```

Expected output identifies the notifier action and the affected message:

```text
request: notifier consumer action 'skip' for Kafka partition 7 at offset 4102; writing to topic notifier-control via Kafka endpoint localhost:9093
success: notifier control action 'skip' written to topic notifier-control for Kafka partition 7 at offset 4102.
```

Called with no arguments, each connector or notifier control prints its required arguments and exits
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
bounded rather than indefinite, and a timeout prints the last states observed.
Read those states: a `FAILED` connector needs the `recover-connector` runbook,
not another pause.

### Which notifier verb

`retry` fixes the processing, not the message: use it when the message is fine
and something around it was broken, such as a failing downstream sender that has
now been redeployed. `skip` accepts the loss for a message that will never
succeed, such as one whose `tenantId` header is missing, which is permanently
malformed on the topic. Retrying that one fails every time.
[docs/specs/23-src-notifier.md](../../docs/specs/23-src-notifier.md) has the full
argument. The reason is recorded with the message, so a skipped notification has
a named owner rather than vanishing, and the script refuses an empty one.

## Failure output

Every operational failure starts with `FAIL:` on standard error and exits
non-zero. The exit status remains the signal a runbook uses to stop. Read the
rest of the line before retrying:

- A local Connect failure means the REST endpoint is unreachable. Check
  `CONNECT_URL` and the port-forward.
- A missing connector means the tenant ID did not map to an existing
  `tenant-<tenantId>-outbox` connector. Check the tenant ID before retrying.
- A connector timeout prints the last states observed. A `FAILED` state needs
  the connector recovery procedure, not another pause request.
- A local Kafka failure means the broker endpoint, Kafka producer path, or TLS
  client properties need attention. Check `KAFKA_BOOTSTRAP_SERVERS`,
  `KAFKA_BIN_DIR`, and `KAFKA_CLIENT_CONFIG`.
- A validation failure names the bad value and shows a valid form. Partition
  and offset must be non-negative integers. The notifier reason must contain
  non-whitespace text.

## Verifying a change

The test project contains argument checks and a stub Kafka-producer check that
do not need Docker. It also contains Docker-dependent checks that run Kafka and
Kafka Connect containers. Docker must be running for the full command. From the
repository root, run:

```text
dotnet test tests/Lexfield.Ops.Tests/Lexfield.Ops.Tests.csproj
```
