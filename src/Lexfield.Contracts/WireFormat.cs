namespace Lexfield.Contracts;

/// <summary>
/// Kafka topic names. docs/specs/01-wire-format.md is the authority on the full
/// set; only the topics a .NET service names in code are here, because a name no
/// service uses is not a contract between services.
/// </summary>
/// <remarks>
/// Hyphens only. A topic name mixing dots and underscores collides in Kafka's
/// metric names, so the set avoids both.
/// </remarks>
public static class Topics
{
    /// <summary>The shared keyed topic every tenant publishes to by default (ADR-003).</summary>
    public const string WorkflowTransitions = "workflow-transitions";

    /// <summary>
    /// Prefix for the stream isolation tier. A tenant isolated from birth gets
    /// <c>workflow-transitions-{tenantId}</c>. A prefix rather than a formatting
    /// helper, because this project holds names and no behaviour.
    /// </summary>
    public const string WorkflowTransitionsTenantPrefix = "workflow-transitions-";

    /// <summary>
    /// Where a consumer copies a message it cannot process. Copies, not moves:
    /// Kafka has no operation that removes one message, so parking is a copy here
    /// plus an offset commit past the original, which stays on its partition.
    /// </summary>
    public const string WorkflowTransitionsParked = "workflow-transitions-parked";

    /// <summary>Operator instructions to a paused notifier partition: retry, or skip this offset.</summary>
    public const string NotifierControl = "notifier-control";
}

/// <summary>
/// Kafka header names. All header values are UTF-8 strings.
/// </summary>
/// <remarks>
/// The SMT chain sets all four; no .NET service produces them. Two are read very
/// differently. A missing or unparseable <see cref="TenantId"/> is a poison
/// event: the consumer parks the message and never falls back to the key,
/// because a key malformed the same way is the same fault. A missing
/// <see cref="TraceParent"/> is not a fault at all: the consumer starts a new
/// trace with no parent and carries on. <see cref="TenantId"/> decides where
/// data belongs and a traceparent decides nothing, so losing one is a
/// correctness fault and losing the other costs a link in a timeline.
/// </remarks>
public static class Headers
{
    /// <summary>The isolation trust root, stamped from connector configuration.</summary>
    public const string TenantId = "tenantId";

    /// <summary>The event type from the outbox row. <c>TaskTransitioned</c> in v1.</summary>
    public const string EventType = "eventType";

    /// <summary>The outbox row id. Traceability only; consumers never dedup on it.</summary>
    public const string EventId = "eventId";

    /// <summary>
    /// W3C trace context, copied from the outbox <c>TraceParent</c> column.
    /// Consumers continue the trace from it (observability.md section 3).
    /// </summary>
    public const string TraceParent = "traceparent";
}
