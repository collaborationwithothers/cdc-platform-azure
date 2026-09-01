using Confluent.Kafka;
using Lexfield.Contracts;
using Lexfield.TestSupport;

namespace Lexfield.Notifier.Tests.CrashWindow;

[Collection(NotifierContainers.Name)]
public sealed class CrashWindowTests(SqlServerFixture sql, KafkaFixture kafka)
{
    [Fact]
    public async Task Crash_after_send_before_record_redelivers_as_duplicate_and_never_drops()
    {
        var topic = $"workflow-transitions-issue-59-{Guid.NewGuid():N}";
        var firstSender = new RecordingSender(crashAfterSend: true);
        await using var first = await CrashWindowHost.StartAsync(
            sql, kafka, topic, firstSender);
        firstSender.RowsAtSend = () => first.CountRowsAsync("SentNotifications");
        firstSender.LockConnectionString = first.ConnectionString;
        firstSender.WaitBeforeStop = () =>
            first.WaitForSignalAsync("Notifier.NotificationSent");

        await first.ProduceAsync(TransitionEventFixture.Event(1));
        await firstSender.WaitForSendAsync();
        await first.WaitForStoppingAsync();
        await first.StopAsync();
        await firstSender.ReleaseLockAsync();

        Assert.Equal(1, firstSender.Count);
        Assert.Equal(0, firstSender.Calls.Single().RowsAtSend);
        Assert.Equal(0, await first.CountRowsAsync("SentNotifications"));
        Assert.Equal(Offset.Unset, first.GetCommittedOffset());
        Assert.Contains("\"eventName\":\"Notifier.NotificationSent\"", first.LogOutput);
        Assert.DoesNotContain("\"eventName\":\"Notifier.SendRecorded\"", first.LogOutput);
        Assert.Contains("\"tenantId\":\"lexfield-001\"", first.LogOutput);
        Assert.Contains("\"taskId\":4711", first.LogOutput);
        Assert.Contains("\"version\":1", first.LogOutput);

        var secondSender = new RecordingSender();
        await using var restarted = await CrashWindowHost.StartAsync(
            sql, kafka, topic, secondSender, first.ConnectionString,
            first.Output, captureOutput: false);

        await secondSender.WaitForCountAsync(1);
        await restarted.WaitForSentRowsAsync(1);
        await restarted.WaitForSignalAsync("Notifier.SendRecorded");
        await restarted.WaitForCommittedOffsetAsync(1);

        Assert.Equal(2, firstSender.Count + secondSender.Count);
        Assert.Equal(1, secondSender.Count);
        Assert.Equal(1, await restarted.CountRowsAsync("SentNotifications"));
        Assert.Equal(2, CountOccurrences(
            restarted.LogOutput, "\"eventName\":\"Notifier.NotificationSent\""));
        Assert.Equal(1, CountOccurrences(
            restarted.LogOutput, "\"eventName\":\"Notifier.SendRecorded\""));
        Assert.Equal(0, CountOccurrences(
            restarted.LogOutput, "\"eventName\":\"Notifier.DuplicateSkipped\""));
        AssertEventsInOrder(
            restarted.LogOutput,
            "Notifier.EventReceived",
            "Notifier.NotificationSent",
            "Notifier.EventReceived",
            "Notifier.NotificationSent",
            "Notifier.SendRecorded");
    }

    [Fact]
    public async Task Rebalance_mid_stream_does_not_resend_an_already_recorded_version()
    {
        var topic = $"workflow-transitions-issue-59-{Guid.NewGuid():N}";
        var firstSender = new RecordingSender();
        await using var first = await CrashWindowHost.StartAsync(
            sql, kafka, topic, firstSender);
        firstSender.RowsAtSend = () => first.CountRowsAsync("SentNotifications");
        firstSender.TrackRecord = true;

        var transition = TransitionEventFixture.Event(1);
        await first.ProduceAsync(transition);
        await firstSender.WaitForCountAsync(1);
        await first.WaitForSentRowsAsync(1);
        await firstSender.WaitForRecordAsync();

        Assert.Equal(["send", "record"], firstSender.CallOrder);

        var secondSender = new RecordingSender();
        await using var second = await CrashWindowHost.StartAsync(
            sql, kafka, topic, secondSender, first.ConnectionString);
        await first.WaitForStableTwoMemberGroupAsync();

        // Re-publish the recorded transition after the stable rebalance to
        // exercise the redelivery path for an already-recorded version.
        await first.ProduceAsync(transition);
        await second.WaitForSignalAsync("Notifier.DuplicateSkipped");

        Assert.Equal(1, firstSender.Count + secondSender.Count);
        Assert.Equal(0, secondSender.Count);
        Assert.Equal(1, await first.CountRowsAsync("SentNotifications"));
        Assert.Equal(1, CountOccurrences(
            second.LogOutput, "\"eventName\":\"Notifier.DuplicateSkipped\""));
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static void AssertEventsInOrder(string output, params string[] eventNames)
    {
        var previous = -1;
        foreach (var eventName in eventNames)
        {
            var current = output.IndexOf(
                $"\"eventName\":\"{eventName}\"", previous + 1,
                StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected {eventName} after the previous event.");
            previous = current;
        }
    }
}
