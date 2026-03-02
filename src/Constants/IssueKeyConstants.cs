namespace Vigilante.Constants;

public static class IssueKeyConstants
{
    private const string SnapshotPrefix = "snapshot";

    public static string Snapshot(string collectionName) => $"{SnapshotPrefix}:{collectionName}";
}
