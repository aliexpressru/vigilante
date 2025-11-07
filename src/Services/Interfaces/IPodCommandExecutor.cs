namespace Vigilante.Services.Interfaces;

/// <summary>
/// Interface for executing commands in Kubernetes pods
/// </summary>
public interface IPodCommandExecutor
{
    /// <summary>
    /// Lists directories in a specified path on a pod
    /// </summary>
    Task<List<string>> ListDirectoriesAsync(
        string podName,
        string podNamespace,
        string path,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists files matching a pattern in a specified path on a pod
    /// </summary>
    Task<List<string>> ListFilesAsync(
        string podName,
        string podNamespace,
        string path,
        string pattern,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets the size of a file or directory on a pod
    /// </summary>
    Task<long?> GetSizeAsync(
        string podName,
        string podNamespace,
        string path,
        string name,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a file or directory and verifies deletion
    /// </summary>
    Task<bool> DeleteAndVerifyAsync(
        string podName,
        string podNamespace,
        string path,
        bool isDirectory,
        string itemDescription,
        CancellationToken cancellationToken);
}

