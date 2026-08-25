# Load generator

Produces the stream of events a load run will issue: how many per second, and
which synthetic tenant each one belongs to. Every event carries a client-side
issue time, which is stage zero of the three-stage latency breakdown blueprint
section 7 requires. Blueprint section 7 also makes a committed generator a
precondition for publishing any latency number, so this is a deliverable rather
than something written during a measurement session.

At this stage the generator prints the stream rather than posting it to
task-api. Posting arrives in the next change on this ticket. What is settled
here is the shape of a run, which is the part a measurement has to be able to
state and repeat.

Everything it names is synthetic and labelled as such: tenants are
`synthetic-tenant-0001` upwards.

## Running it

    dotnet run --project tools/loadgen -- --tenants 400 --distribution hot:8:0.8 --rate 50 --events 5000

| Option | Default | Meaning |
| --- | --- | --- |
| `--tenants` | 3 | how many synthetic tenants the run draws from |
| `--distribution` | `uniform` | `uniform`, or `hot:COUNT:SHARE` |
| `--rate` | 10 | events per second |
| `--events` | 100 | events to issue, then stop |
| `--seed` | 1 | random seed, so a run repeats exactly |

Stage-zero records go to stdout, one JSON object per line, so a measurement
pipes them to a file. The summary goes to stderr. Exit code is 2 when the
options do not parse.

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
