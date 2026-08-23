using System.Text.Json.Serialization;

namespace Lexfield.Contracts;

/// <summary>
/// The seven workflow states, in the order a task moves through them.
/// Blueprint section 2 defines the set; docs/specs/20-src-task-api.md owns the
/// legal edges between them, which deliberately do not live here: this type is
/// the vocabulary, not the rules.
/// </summary>
/// <remarks>
/// Serialized as its name, never its ordinal. The tenant database stores
/// <c>State</c> as <c>nvarchar(16)</c> and the topic message carries the same
/// text, so an ordinal would make the two disagree and would break every
/// existing message the moment a state is inserted into the middle.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<TaskState>))]
public enum TaskState
{
    Created,
    Assigned,
    InProgress,
    Submitted,
    QA,
    Completed,
    Delivered,
}

/// <summary>
/// The business event announcing one task transition. This is the JSON in the
/// outbox row's <c>Payload</c> column and, unchanged, the message value on
/// <c>workflow-transitions</c>. docs/specs/01-wire-format.md traces both.
/// </summary>
/// <remarks>
/// Two fields a reader will look for are deliberately absent.
/// <para>
/// <c>tenantId</c> is absent because it travels on the message key and in the
/// <c>tenantId</c> header, both stamped from connector configuration. One
/// attribution source is the point: the reconciler's failure-mode-9 check
/// compares the observed header against the tenant's own database claim, and a
/// second copy inside the value would let the platform check itself.
/// </para>
/// <para>
/// <c>traceparent</c> is absent because the envelope records what happened and
/// a trace identifier records how the platform followed it. It rides in its own
/// outbox column and becomes a Kafka header.
/// </para>
/// Property names are pinned with <see cref="JsonPropertyNameAttribute"/> rather
/// than left to a serializer option: four services read this type, and a naming
/// policy configured in three of them is a wire break nothing catches at compile
/// time.
/// </remarks>
public sealed record TransitionEvent
{
    /// <summary>The per-tenant task identifier. Not unique across tenants.</summary>
    [JsonPropertyName("taskId")]
    public required int TaskId { get; init; }

    /// <summary>
    /// The state before the transition. Null on the Created event, which is
    /// always version 1 (ADR-004).
    /// </summary>
    [JsonPropertyName("from")]
    public TaskState? From { get; init; }

    /// <summary>The state after the transition.</summary>
    [JsonPropertyName("to")]
    public required TaskState To { get; init; }

    /// <summary>Who performed the transition, for example <c>user:1234</c>.</summary>
    [JsonPropertyName("actor")]
    public required string Actor { get; init; }

    /// <summary>When the transition was committed, UTC.</summary>
    [JsonPropertyName("at")]
    public required DateTimeOffset At { get; init; }

    /// <summary>
    /// The task version after the transition. Monotonic per task, incremented in
    /// the same transaction as the state change, which is what lets a consumer
    /// detect a gap without asking the source.
    /// </summary>
    [JsonPropertyName("version")]
    public required int Version { get; init; }

    /// <summary>
    /// The owning team after the transition. Carried so queue-builder can
    /// maintain it in QueueState without reading the tenant database.
    /// </summary>
    [JsonPropertyName("teamId")]
    public string? TeamId { get; init; }

    /// <summary>The assignee after the transition, carried for the same reason.</summary>
    [JsonPropertyName("assigneeId")]
    public string? AssigneeId { get; init; }
}
