namespace Lexfield.Connect.Tests.Snapshots;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IncrementalSnapshotCollection : ICollectionFixture<IncrementalSnapshotFixture>
{
    public const string Name = "incremental-snapshot";
}
