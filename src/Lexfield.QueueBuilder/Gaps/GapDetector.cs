namespace Lexfield.QueueBuilder.Gaps;

internal interface IGapDetector
{
    GapKind Detect(int? storedVersion, int incomingVersion);
}

internal sealed class GapDetector : IGapDetector
{
    public GapKind Detect(int? storedVersion, int incomingVersion)
    {
        if (storedVersion is null)
            return incomingVersion > 1 ? GapKind.HeadLoss : GapKind.None;

        return (long)incomingVersion > (long)storedVersion.Value + 1
            ? GapKind.Jump
            : GapKind.None;
    }
}

internal enum GapKind
{
    None,
    Jump,
    HeadLoss
}
