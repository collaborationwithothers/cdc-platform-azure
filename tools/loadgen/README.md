# Load generator

Drives task transitions through task-api's HTTP surface at a rate you set,
spread across as many synthetic tenants as you ask for. Every event carries a
client-side issue time, which is stage zero of the three-stage latency breakdown
blueprint section 7 requires. Blueprint section 7 also makes a committed
generator a precondition for publishing any latency number, so this is a
deliverable rather than something written during a measurement session.

Everything it writes is synthetic and labelled as such: tenants are
`synthetic-tenant-0001` upwards, and the actor on every event is
`synthetic:loadgen`.

## Running it

    export LEXFIELD_LOADGEN_TOKEN=<a bearer token task-api accepts>
    dotnet run --project tools/loadgen -- \
      --base-address https://task-api.example \
      --tenants 400 --distribution hot:8:0.8 --rate 50 --events 5000

| Option | Default | Meaning |
| --- | --- | --- |
| `--base-address` | `http://localhost:5000` | task-api base address |
| `--tenants` | 3 | how many synthetic tenants the run draws from |
| `--distribution` | `uniform` | `uniform`, or `hot:COUNT:SHARE` |
| `--rate` | 10 | events per second |
| `--events` | 100 | events to issue, then stop |
| `--seed` | 1 | random seed, so a run repeats exactly |

The bearer token is read from `LEXFIELD_LOADGEN_TOKEN`. No credential is read
from a file in this repository. How that token is obtained is not settled here:
task-api requires an Entra JWT whose tenant claim matches the route, and the
identity spike owns the audience and the command that mints one.

Stage-zero records go to stdout, one JSON object per line, so a measurement
pipes them to a file. The summary goes to stderr. Exit code is 0 when every
event succeeded, 1 when any failed, and 2 when the options do not parse.

## What a run actually does

Each event draws a tenant from the distribution. The first event for a tenant
creates a task; every later event moves that tenant's task one step along the
legal edges. Once a task reaches QA it takes the rework edge back to InProgress,
so one task per tenant produces an unbounded stream of transitions. The
generator never drives a task to Completed or Delivered.

A rejected transition is counted and reported, and the local version is not
advanced, so the next attempt sends the version the server still holds rather
than compounding one rejection into a permanently wedged task.

The run starts no trace and attaches no listener. task-api writes
`Activity.Current?.Id` into the outbox row inside the transaction, so an untraced
client is what makes it write a null `TraceParent`. Every load run therefore
exercises the untraced write path.

## Why the rate is a schedule, not a sleep

Event n is due at `start + n / rate`. Sleeping a fixed interval between events
instead would make the offered rate depend on how long each event took, so a
slow run quietly becomes a 30/s run when 50/s was configured, and every figure
measured from it describes a load nobody asked for. With an absolute schedule a
caller that falls behind waits zero until it has caught up, so the run holds the
configured rate across its whole length.

The catch-up is visible on short runs. Process start and first-call warm-up cost
more than one event's worth of schedule, so a run of a few events spends most of
its life behind and reports an observed rate well under the target. That is the
schedule working, not the rate failing, and it is a reason not to read an
observed rate off a run of tens of events.

## Why the tenant spread is configured

A `uniform` run spreads load across every tenant, which is the shape that
exercises every connector. A `hot:COUNT:SHARE` run concentrates it, which is the
shape the poison-event blast-radius measurement needs: `hot:8:0.8` sends 80
percent of events to 8 tenants. Assuming one shape would make the other
unmeasurable.

## The limitation any published number must carry

The build scale is 3 tenant databases (AGENTS.md, Scope). A run with
`--tenants 400` names 400 distinct tenant key values, but the tenant catalog
maps them onto the 3 databases that exist. Contention that 400 separate
databases would not show is therefore present in the numbers. Any figure
published from a run with more tenants than databases states that, and states
the two counts.
