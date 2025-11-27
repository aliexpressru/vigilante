using k8s;
using System.Net.WebSockets;
using System.Text;
using Vigilante.Extensions;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

/// <summary>
/// Executes shell commands in Kubernetes pods via WebSocket
/// </summary>
public class PodCommandExecutor(IKubernetes? kubernetes, ILogger<PodCommandExecutor> logger) : IPodCommandExecutor
{

    // Shell command templates with detailed explanations
    
    // Command: cd {directory} && ls -1d */
    // - "cd {directory}": Change to target directory
    // - "&&": Execute next command only if cd succeeds
    // - "ls": List directory contents
    // - "-1": One entry per line (easier parsing)
    // - "-d": List directories themselves, not their contents
    // - "*/": Match only directories (trailing slash)
    private const string ListDirectoriesCommand = "cd {0} && ls -1d */";

    // Command: cd {directory} && ls -1 {pattern} 2>/dev/null || echo ''
    // - "cd {directory}": Change to target directory
    // - "&&": Execute next command only if cd succeeds
    // - "ls -1 {pattern}": List files matching pattern, one per line
    // - "2>/dev/null": Redirect errors to null (suppress error messages)
    // - "||": Execute next command if previous fails
    // - "echo ''": Return empty string if no files found (prevents error)
    private const string ListFilesCommand = "cd {0} && ls -1 {1} 2>/dev/null || echo ''";

    // Command: cd {directory} && du -sb "{item}" | cut -f1
    // - "cd {directory}": Change to target directory
    // - "&&": Execute next command only if cd succeeds
    // - "du": Disk usage command
    // - "-s": Summary only (don't show subdirectories separately)
    // - "-b": Size in bytes (instead of blocks)
    // - "\"{item}\"": Item name in quotes (handles special characters)
    // - "|": Pipe output to next command
    // - "cut -f1": Extract first field (size), excluding the path
    private const string GetSizeCommand = "cd {0} && du -sb \"{1}\" | cut -f1";

    // Command: rm -rf {path}
    // - "rm": Remove files/directories
    // - "-r": Recursive (remove directories and contents)
    // - "-f": Force (don't prompt, ignore nonexistent files)
    // - "{path}": Full path to remove
    private const string RemoveDirectoryCommand = "rm -rf {0}";

    // Command: rm -f {path}
    // - "rm": Remove files
    // - "-f": Force (don't prompt, ignore nonexistent files)
    // - "{path}": Full path to file to remove
    private const string RemoveFileCommand = "rm -f {0}";

    // Command: test -d {path} && echo 'exists' || echo 'deleted'
    // - "test -d {path}": Check if directory exists
    // - "&&": Execute next if test succeeds
    // - "echo 'exists'": Print 'exists' if directory found
    // - "||": Execute next if previous fails
    // - "echo 'deleted'": Print 'deleted' if directory not found
    private const string CheckDirectoryExistsCommand = "test -d {0} && echo 'exists' || echo 'deleted'";

    // Command: test -f {path} && echo 'exists' || echo 'deleted'
    // - "test -f {path}": Check if file exists
    // - "&&": Execute next if test succeeds
    // - "echo 'exists'": Print 'exists' if file found
    // - "||": Execute next if previous fails
    // - "echo 'deleted'": Print 'deleted' if file not found
    private const string CheckFileExistsCommand = "test -f {0} && echo 'exists' || echo 'deleted'";

    // Command: cat {path} 2>/dev/null || echo ''
    // - "cat {path}": Read file contents
    // - "2>/dev/null": Redirect errors to null (suppress error messages)
    // - "||": Execute next command if previous fails
    // - "echo ''": Return empty string if file not found (prevents error)
    private const string GetFileContentCommand = "cat {0} 2>/dev/null || echo ''";

    // Command: cat {path}
    // - "cat {path}": Stream file contents to stdout
    // Note: cat outputs EXACTLY the file size (verified with wc -c)
    // IMPORTANT: Use base64 encoding for file transfer via Kubernetes WebSocket
    // Without base64, Kubernetes WebSocket converts LF (0x0a) to CRLF (0x0d 0x0a) causing data corruption
    // This was verified with test files: original 4,538,894 bytes became 4,588,894 bytes (+50,000 bytes)
    // With base64: checksum matches perfectly (9682cf3aedb95823f365b8eb31931dd646d04d71f2b103c593fa877a06f8358d)
    private const string StreamFileCommand = "base64 {0}";

    // Command: stat -c %s {path}
    // - "stat": Display file status

    /// <summary>
    /// Lists directories in the specified path
    /// </summary>
    public async Task<List<string>> ListDirectoriesAsync(
        string podName,
        string podNamespace,
        string directory,
        CancellationToken cancellationToken)
    {
        var command = string.Format(ListDirectoriesCommand, directory);
        return await ExecuteCommandAndGetLinesAsync(podName, podNamespace, command, cancellationToken);
    }

    /// <summary>
    /// Lists files matching pattern in the specified path
    /// </summary>
    public async Task<List<string>> ListFilesAsync(
        string podName,
        string podNamespace,
        string directory,
        string pattern,
        CancellationToken cancellationToken)
    {
        var command = string.Format(ListFilesCommand, directory, pattern);
        return await ExecuteCommandAndGetLinesAsync(podName, podNamespace, command, cancellationToken);
    }

    /// <summary>
    /// Gets the size of a file or directory in bytes
    /// </summary>
    public async Task<long?> GetSizeAsync(
        string podName,
        string podNamespace,
        string baseDirectory,
        string itemName,
        CancellationToken cancellationToken)
    {
        try
        {
            var command = string.Format(GetSizeCommand, baseDirectory, itemName);
            var rawOutput = await ExecuteCommandAsync(podName, podNamespace, command, cancellationToken);
            
            var output = rawOutput
                .Trim()
                .Replace("\n", "")
                .Replace("\r", "")
                .Replace("\0", "");
            
            logger.LogDebug("Received size data for {Item}: '{Output}'", itemName, output);

            var cleanedOutput = new string(output.Where(char.IsDigit).ToArray());
            if (!string.IsNullOrEmpty(cleanedOutput) && long.TryParse(cleanedOutput, out var sizeBytes))
            {
                return sizeBytes;
            }
            
            logger.LogWarning("Failed to parse size for {Item}: '{Output}'", itemName, output);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get size for {Item} in pod {PodName}", itemName, podName);
            return null;
        }
    }

    /// <summary>
    /// Deletes a file or directory and verifies deletion
    /// </summary>
    public async Task<bool> DeleteAndVerifyAsync(
        string podName,
        string podNamespace,
        string fullPath,
        bool isDirectory,
        string itemDescription,
        CancellationToken cancellationToken)
    {
        try
        {
            // Execute delete command
            var deleteCommand = isDirectory 
                ? string.Format(RemoveDirectoryCommand, fullPath)
                : string.Format(RemoveFileCommand, fullPath);
            
            var deleteOutput = await ExecuteCommandAsync(podName, podNamespace, deleteCommand, cancellationToken);

            // Check for errors in output
            if (!string.IsNullOrEmpty(deleteOutput) && 
                (deleteOutput.Contains("error", StringComparison.OrdinalIgnoreCase) || 
                 deleteOutput.Contains("permission denied", StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogError("Failed to delete {Description}: {Output}", itemDescription, deleteOutput);
                return false;
            }

            // Verify deletion
            var verifyCommand = isDirectory 
                ? string.Format(CheckDirectoryExistsCommand, fullPath)
                : string.Format(CheckFileExistsCommand, fullPath);
            
            var verifyOutput = await ExecuteCommandAsync(podName, podNamespace, verifyCommand, cancellationToken);
            var verifyResult = verifyOutput.Trim();

            if (verifyResult.Contains("deleted"))
            {
                logger.LogInformation("✅ {Description} deleted successfully from disk on pod {PodName}", 
                    itemDescription, podName);
                return true;
            }
            else
            {
                logger.LogError("{Description} still exists after deletion attempt on pod {PodName}", 
                    itemDescription, podName);
                return false;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete {Description} on pod {PodName}", itemDescription, podName);
            return false;
        }
    }

    private async Task<string> ExecuteCommandAsync(
        string podName,
        string podNamespace,
        string command,
        CancellationToken cancellationToken)
    {
        if (kubernetes == null)
        {
            logger.LogWarning("Kubernetes client is not available. Running outside Kubernetes cluster?");
            throw new InvalidOperationException("Kubernetes client is not available");
        }
        
        using var webSocket = await kubernetes.WebSocketNamespacedPodExecAsync(
            podName,
            podNamespace,
            new[] { "sh", "-c", command },
            "qdrant",
            cancellationToken: cancellationToken);

        var buffer = new byte[4096];
        var segment = new ArraySegment<byte>(buffer);
        var output = new StringBuilder(512);

        WebSocketReceiveResult result;
        do
        {
            result = await webSocket.ReceiveAsync(segment, cancellationToken);
            if (result.Count > 0)
            {
                // Kubernetes WebSocket uses channel prefix (first byte):
                // 0 = stdin, 1 = stdout, 2 = stderr, 3 = error/resize
                // Check if first byte is a channel ID (0-3), otherwise it's regular data
                var dataStart = 0;
                var dataLength = result.Count;
                
                if (result.Count > 0 && buffer[0] <= 3)
                {
                    // First byte is a channel prefix, skip it
                    dataStart = 1;
                    dataLength = result.Count - 1;
                }
                
                if (dataLength > 0)
                {
                    output.Append(Encoding.UTF8.GetString(buffer, dataStart, dataLength));
                }
            }
        } while (!result.CloseStatus.HasValue && !cancellationToken.IsCancellationRequested);

        return output.ToString();
    }

    private async Task<List<string>> ExecuteCommandAndGetLinesAsync(
        string podName,
        string podNamespace,
        string command,
        CancellationToken cancellationToken)
    {
        var output = await ExecuteCommandAsync(podName, podNamespace, command, cancellationToken);

        // First remove all control characters from the entire output
        var cleanOutput = new string(output.Where(c => !char.IsControl(c) || c == '\n' || c == '\r').ToArray());

        return cleanOutput
            .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(name => name
                .TrimEnd('/', ':')  // Remove both / and : that ls -1d */ can add
                .Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name) && !name.StartsWith("."))
            .ToList();
    }


    /// <summary>
    /// Gets exact file size in bytes using stat command
    /// </summary>
    public async Task<long?> GetFileSizeInBytesAsync(
        string podName,
        string podNamespace,
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Getting file size for {FilePath} on pod {PodName} in namespace {Namespace}", 
                filePath, podName, podNamespace);
            
            // Use stat -c %s to get exact file size in bytes
            var command = $"stat -c %s {filePath}";
            logger.LogInformation("Executing command: {Command}", command);
            
            var output = await ExecuteCommandAsync(podName, podNamespace, command, cancellationToken);
            
            logger.LogInformation("stat command raw output: '{Output}' (length: {Length})", 
                output, output?.Length ?? 0);
            
            if (string.IsNullOrWhiteSpace(output))
            {
                logger.LogWarning("stat command returned empty output for {FilePath}", filePath);
                return null;
            }
            
            // Trim all whitespace
            var trimmedOutput = output.Trim();
            
            logger.LogInformation("After trim: '{TrimmedOutput}' (length: {Length})", 
                trimmedOutput, trimmedOutput.Length);
            
            if (long.TryParse(trimmedOutput, out var size))
            {
                logger.LogInformation("✅ Got file size for {FilePath}: {Size} bytes", filePath, size);
                return size;
            }
            
            logger.LogWarning("❌ Could not parse file size. Output: '{Output}'", trimmedOutput);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "❌ Failed to get file size for {FilePath} on pod {PodName}", filePath, podName);
            return null;
        }
    }

    /// <summary>
    /// Gets content of a file from pod
    /// </summary>
    public async Task<string?> GetFileContentAsync(
        string podName,
        string podNamespace,
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogDebug("Reading file content from {FilePath} on pod {PodName}", filePath, podName);
            
            var command = string.Format(GetFileContentCommand, filePath);
            var content = await ExecuteCommandAsync(podName, podNamespace, command, cancellationToken);
            
            if (string.IsNullOrWhiteSpace(content))
            {
                logger.LogDebug("File not found or empty at {FilePath}", filePath);
                return null;
            }
            
            return content.Trim();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read file content from {FilePath}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Downloads a file from a pod using kubectl cp (more reliable for large files)
    /// </summary>
    public async Task<Stream?> DownloadFileViaKubectlCpAsync(
        string podName,
        string podNamespace,
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Downloading file {FilePath} from pod {PodName} in namespace {Namespace} using kubectl cp",
                filePath, podName, podNamespace);

            // Create temporary file for download
            var tempFile = Path.GetTempFileName();

            try
            {
                // Use kubectl cp: kubectl cp <namespace>/<pod>:/path/to/file /local/path
                var source = $"{podNamespace}/{podName}:{filePath}";
                var kubeConfigPath = Environment.GetEnvironmentVariable("KUBECONFIG") ??
                                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kube", "config");

                // Build kubectl cp command
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "kubectl",
                    Arguments = $"cp {source} {tempFile} --kubeconfig {kubeConfigPath}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                logger.LogInformation("Executing: kubectl cp {Source} {TempFile}", source, tempFile);

                using var process = System.Diagnostics.Process.Start(startInfo);
                if (process == null)
                {
                    logger.LogError("Failed to start kubectl process");
                    File.Delete(tempFile);
                    return null;
                }

                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                var error = await process.StandardError.ReadToEndAsync(cancellationToken);

                await process.WaitForExitAsync(cancellationToken);

                if (process.ExitCode != 0)
                {
                    logger.LogError("kubectl cp failed with exit code {ExitCode}. Error: {Error}",
                        process.ExitCode, error);
                    File.Delete(tempFile);
                    return null;
                }

                if (!string.IsNullOrEmpty(error))
                {
                    logger.LogWarning("kubectl cp stderr: {Error}", error);
                }

                // Verify file was downloaded
                if (!File.Exists(tempFile))
                {
                    logger.LogError("Temp file {TempFile} does not exist after kubectl cp", tempFile);
                    return null;
                }

                var fileInfo = new FileInfo(tempFile);
                logger.LogInformation("✅ Downloaded {Size} bytes to {TempFile} using kubectl cp",
                    fileInfo.Length, tempFile);

                // Open file stream with DeleteOnClose option
                var fileStream = new FileStream(
                    tempFile,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 81920, // 80KB buffer
                    FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);

                return fileStream;
            }
            catch
            {
                // Cleanup temp file on error
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
                throw;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to download file {FilePath} from pod {PodName} using kubectl cp",
                filePath, podName);
            return null;
        }
    }

    /// <summary>
    /// Downloads a file from a pod as a stream using cat command
    /// </summary>
    public Task<Stream?> DownloadFileAsync(
        string podName,
        string podNamespace,
        string filePath,
        CancellationToken cancellationToken)
    {
        return DownloadFileAsync(podName, podNamespace, filePath, null, cancellationToken);
    }

    /// <summary>
    /// Downloads a file from a pod as a stream using cat command with optional expected size for logging
    /// </summary>
    public async Task<Stream?> DownloadFileAsync(
        string podName,
        string podNamespace,
        string filePath,
        long? expectedSize,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Downloading file {FilePath} from pod {PodName} in namespace {Namespace}",
                filePath, podName, podNamespace);

            // Use cat - it outputs exactly the file size (verified: cat | wc -c == stat)
            var command = string.Format(StreamFileCommand, filePath);
            
            if (expectedSize.HasValue)
            {
                logger.LogInformation("Expected file size: {Size} bytes ({FormattedSize})", 
                    expectedSize.Value, expectedSize.Value.ToPrettySize());
            }
            
            if (kubernetes == null)
            {
                logger.LogWarning("Kubernetes client is not available. Running outside Kubernetes cluster?");
                throw new InvalidOperationException("Kubernetes client is not available");
            }
            
            var webSocket = await kubernetes.WebSocketNamespacedPodExecAsync(
                podName,
                podNamespace,
                new[] { "sh", "-c", command },
                "qdrant",
                cancellationToken: cancellationToken);

            // Create a stream that will read from WebSocket (base64 encoded)
            var webSocketStream = new WebSocketStream(webSocket, logger, filePath, podName, null);
            
            // Wrap in CryptoStream to decode base64 on the fly
            var base64Transform = new System.Security.Cryptography.FromBase64Transform();
            var decodingStream = new System.Security.Cryptography.CryptoStream(
                webSocketStream, 
                base64Transform, 
                System.Security.Cryptography.CryptoStreamMode.Read);
            
            logger.LogInformation("✅ Started streaming file {FilePath} from pod {PodName} (base64 encoded)",
                filePath, podName);
            
            return decodingStream;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to download file {FilePath} from pod {PodName}",
                filePath, podName);
            return null;
        }
    }
}
