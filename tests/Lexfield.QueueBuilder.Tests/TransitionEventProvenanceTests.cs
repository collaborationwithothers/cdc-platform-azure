using System.Text.Json;
using Lexfield.Contracts;

namespace Lexfield.QueueBuilder.Tests;

/// <summary>
/// Pure-unit checks that the shared <see cref="TransitionEvent"/> contract binds
/// the two provenance fields on a new-shape event and still deserializes a legacy
/// event that predates them (issue #265, ADR-004).
///
/// These tests deliberately join no <c>[Collection(...)]</c>: they need no
/// SQL Server or Kafka container. They deserialize with
/// <see cref="JsonSerializer.Deserialize{TValue}(string, JsonSerializerOptions)"/>
/// using default options, which is exactly what queue-builder's
/// <c>TransitionMessageDecoder.Decode</c> does with the Kafka message value
/// (it calls <c>JsonSerializer.Deserialize&lt;TransitionEvent&gt;(message.Value)</c>
/// with no options). The decoder itself is internal to Lexfield.QueueBuilder and
/// the message it takes requires a container-backed host to be meaningful, so the
/// contract-level deserialization here proves the queue-builder boundary tolerates
/// both shapes without constructing the decoder.
/// </summary>
public sealed class TransitionEventProvenanceTests
{
    private const string CanonicalActor =
        "user:00000000-0000-0000-0000-000000000001:00000000-0000-0000-0000-000000000002";

    // New-shape event from docs/specs/01-wire-format.md: carries the two
    // provenance fields (clientApplicationId, permissionMode).
    private const string NewShapeJson =
        """{"taskId":4711,"from":"Assigned","to":"InProgress","actor":"user:00000000-0000-0000-0000-000000000001:00000000-0000-0000-0000-000000000002","clientApplicationId":"00000000-0000-0000-0000-00000000000c","permissionMode":"delegated","at":"2026-08-22T10:15:03.221Z","version":7,"teamId":"team-conveyancing","assigneeId":"user:1234"}""";

    // Legacy event: an old ad-hoc actor and none of the new provenance fields.
    private const string LegacyJson =
        """{"taskId":4711,"from":"Assigned","to":"InProgress","actor":"user:1234","at":"2026-08-22T10:15:03.221Z","version":7,"teamId":"team-conveyancing","assigneeId":"user:1234"}""";

    [Fact]
    public void New_shape_event_binds_all_three_provenance_fields()
    {
        var taskEvent = JsonSerializer.Deserialize<TransitionEvent>(NewShapeJson);

        Assert.NotNull(taskEvent);
        Assert.Equal("00000000-0000-0000-0000-00000000000c", taskEvent.ClientApplicationId);
        Assert.Equal("delegated", taskEvent.PermissionMode);
        Assert.Equal(CanonicalActor, taskEvent.Actor);
        Assert.True(taskEvent.HasVerifiedProvenance);

        // Projection-relevant fields still bind: no regression for queue-builder.
        Assert.Equal(4711, taskEvent.TaskId);
        Assert.Equal(TaskState.InProgress, taskEvent.To);
        Assert.Equal(7, taskEvent.Version);
        Assert.Equal("team-conveyancing", taskEvent.TeamId);
        Assert.Equal("user:1234", taskEvent.AssigneeId);
    }

    [Fact]
    public void Legacy_event_deserializes_and_reports_unverified_provenance()
    {
        var taskEvent = JsonSerializer.Deserialize<TransitionEvent>(LegacyJson);

        // Deserializes despite the required fields, because every existing
        // required field (taskId, to, actor, at, version) is present, and the
        // two new fields are nullable and not required.
        Assert.NotNull(taskEvent);
        Assert.Null(taskEvent.ClientApplicationId);
        Assert.Null(taskEvent.PermissionMode);
        Assert.False(taskEvent.HasVerifiedProvenance);
        Assert.Equal("legacy-unverified", TransitionEvent.LegacyProvenanceLabel);

        // Projection-relevant fields bind identically to the new-shape event,
        // proving the legacy shape causes no projection regression.
        Assert.Equal(4711, taskEvent.TaskId);
        Assert.Equal(TaskState.InProgress, taskEvent.To);
        Assert.Equal(7, taskEvent.Version);
        Assert.Equal("team-conveyancing", taskEvent.TeamId);
        Assert.Equal("user:1234", taskEvent.AssigneeId);
    }
}
