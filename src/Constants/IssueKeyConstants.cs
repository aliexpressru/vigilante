namespace Vigilante.Constants;

public static class IssueKeyConstants
{
    private const string SnapshotPrefix = "snapshot";
    private const string JobPrefix = "job";

    public static string Snapshot(string collectionName) => $"{SnapshotPrefix}:{collectionName}";

    public static string JobFailure(string key) => $"{JobPrefix}:{key}";
}
