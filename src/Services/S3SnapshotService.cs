using Amazon.S3;
using Amazon.S3.Model;
using Amazon.Runtime;
using Vigilante.Configuration;
using Vigilante.Constants;
using Vigilante.Services.Interfaces;
using Vigilante.Utilities;

namespace Vigilante.Services;

/// <summary>
/// Service for managing Qdrant snapshots in S3-compatible storage.
/// 
/// Configuration:
/// - Bucket name is configurable via appsettings.json under Qdrant:S3:BucketName
/// - The bucket can have any name (e.g., "snapshots", "qdrant-backups", etc.)
/// - Inside the bucket, snapshots are stored under the "snapshots/" folder prefix (S3Constants.SnapshotsFolder)
/// 
/// Path structure in S3:
/// {BucketName}/{SnapshotsFolder}/{collection-name}/{snapshot-file}
/// 
/// Example with BucketName="my-backups":
/// my-backups/snapshots/some-vectors~~20251210/some-vectors-3372865182647577-2025-12-10-11-18-48.snapshot
/// </summary>
public class S3SnapshotService(
    IS3ConfigurationProvider configProvider,
    ILogger<S3SnapshotService> logger) : IS3SnapshotService, IDisposable
{
    private IAmazonS3? _s3Client;
    private S3Options? _currentConfig;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Lists ALL snapshots from S3 storage
    /// Returns list of tuples: (collectionName, snapshotName, sizeBytes)
    /// This includes snapshots for deleted/old collection versions
    /// </summary>
    public async Task<List<(string CollectionName, string SnapshotName, long SizeBytes)>> ListAllSnapshotsAsync(
        string? namespaceParameter = null,
        CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateS3ClientAsync(namespaceParameter, cancellationToken);
        if (client == null)
        {
            logger.LogWarning("S3 client is not available, cannot list snapshots");
            return new List<(string, string, long)>();
        }

        try
        {
            // List all objects in the bucket with the configured snapshots folder prefix
            // Objects are stored under '{SnapshotsFolder}/' prefix in the bucket
            var prefix = $"{S3Constants.SnapshotsFolder}/";
            
            logger.LogInformation("Listing ALL S3 objects with prefix: {Prefix} in bucket: {BucketName}", 
                prefix, _currentConfig!.BucketName);
            
            var request = new ListObjectsV2Request
            {
                BucketName = _currentConfig!.BucketName,
                Prefix = prefix
            };

            var response = await client.ListObjectsV2Async(request, cancellationToken);
            
            logger.LogInformation("S3 ListObjects response: KeyCount={KeyCount}, IsTruncated={IsTruncated}", 
                response.KeyCount, response.IsTruncated);
            
            if (response.S3Objects == null || response.KeyCount == 0)
            {
                logger.LogInformation("No objects found in S3 snapshots bucket");
                return new List<(string, string, long)>();
            }
            
            logger.LogInformation("S3Objects count: {Count}", response.S3Objects.Count);
            
            // Log first few keys to understand the structure
            var firstKeys = response.S3Objects.Take(3).Select(o => o.Key).ToArray();
            logger.LogInformation("First few S3 keys: {Keys}", string.Join(", ", firstKeys));
            
            var snapshots = new List<(string, string, long)>();
            
            foreach (var obj in response.S3Objects)
            {
                // Skip folder markers (objects ending with /)
                if (obj.Key.EndsWith("/"))
                    continue;

                // Debug: Log the actual key
                logger.LogDebug("S3 Object Key: {Key}", obj.Key);

                // Parse key: {SnapshotsFolder}/{collection-name}/{snapshot-filename}
                var parts = obj.Key.Split('/');
                if (parts.Length != 3)
                    continue;

                // Decode collection name and snapshot name (replace %7E back to ~)
                // parts[0] = SnapshotsFolder (e.g., "snapshots")
                // parts[1] = collection name (may be URL-encoded)
                // parts[2] = snapshot filename (may be URL-encoded)
                var encodedCollectionName = parts[1];
                var collectionName = Uri.UnescapeDataString(encodedCollectionName);
                
                var encodedSnapshotName = parts[2];
                var snapshotName = Uri.UnescapeDataString(encodedSnapshotName); // Decode the snapshot filename too
                var sizeBytes = obj.Size ?? 0L;

                snapshots.Add((collectionName, snapshotName, sizeBytes));
            }

            logger.LogInformation("Found {Count} total snapshots in S3 storage", snapshots.Count);

            return snapshots;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list all snapshots from S3");
            return new List<(string, string, long)>();
        }
    }

    /// <summary>
    /// Lists all snapshots for a given collection from S3
    /// Returns list of tuples: (snapshotName, sizeBytes)
    /// </summary>
    public async Task<List<(string Name, long SizeBytes)>> ListSnapshotsAsync(
        string collectionName,
        string? namespaceParameter = null,
        CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateS3ClientAsync(namespaceParameter, cancellationToken);
        if (client == null)
        {
            logger.LogWarning("S3 client is not available, cannot list snapshots");
            return new List<(string, long)>();
        }

        try
        {
            // Path structure: {SnapshotsFolder}/{collection-name}/
            // Important: In S3, ONLY tildes (~) are URL-encoded as %7E
            // Other special characters like dots, underscores remain as-is
            // This matches how Qdrant stores snapshots in S3
            
            // Use full collection name (including version) for searching
            // Path in S3: {SnapshotsFolder}/{full-collection-name-with-version}/{snapshot-files}
            // URL-encode the collection name properly
            // NOTE: Uri.EscapeDataString() does NOT encode tildes (~), but S3 stores them as %7E
            var encodedCollectionName = Uri.EscapeDataString(collectionName).Replace("~", "%7E");
            
            var prefix = $"{S3Constants.SnapshotsFolder}/{encodedCollectionName}";
            
            logger.LogInformation("Listing S3 objects with prefix: {Prefix} (collection: {CollectionName}) in bucket: {BucketName}", 
                prefix, collectionName, _currentConfig!.BucketName);
            
            var request = new ListObjectsV2Request
            {
                BucketName = _currentConfig!.BucketName,
                Prefix = prefix
            };

            var response = await client.ListObjectsV2Async(request, cancellationToken);
            
            logger.LogInformation("S3 ListObjects response: KeyCount={KeyCount}, IsTruncated={IsTruncated}", 
                response.KeyCount, response.IsTruncated);
            
            // S3Objects can be null if prefix doesn't exist or bucket is empty
            if (response.S3Objects == null || response.KeyCount == 0)
            {
                logger.LogInformation("No objects found for collection {CollectionName} in S3", collectionName);
                return new List<(string, long)>();
            }
            
            logger.LogInformation("S3Objects count: {Count}", response.S3Objects.Count);
            
            // Extract snapshot name and size from S3 objects
            var snapshots = response.S3Objects
                .Select(obj => (
                    Name: Path.GetFileName(obj.Key),
                    SizeBytes: obj.Size ?? 0L // S3Object.Size is long?, use 0 if null
                ))
                .Where(s => !string.IsNullOrEmpty(s.Name))
                .ToList();

            logger.LogInformation("Found {Count} snapshots for collection {CollectionName} in S3", 
                snapshots.Count, collectionName);

            return snapshots;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list snapshots for collection {CollectionName} from S3", collectionName);
            return new List<(string, long)>();
        }
    }

    /// <summary>
    /// Downloads a snapshot from S3 to a stream
    /// </summary>
    public async Task<Stream?> DownloadSnapshotAsync(
        string collectionName,
        string snapshotName,
        string? namespaceParameter = null,
        CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateS3ClientAsync(namespaceParameter, cancellationToken);
        if (client == null)
        {
            logger.LogWarning("S3 client not available, cannot download snapshot");
            return null;
        }

        try
        {
            // Path structure: {SnapshotsFolder}/{collection-name}/{snapshot-file}
            // NOTE: Uri.EscapeDataString() does NOT encode tildes (~), but S3 stores them as %7E
            var encodedCollectionName = Uri.EscapeDataString(collectionName).Replace("~", "%7E");
            var encodedSnapshotName = Uri.EscapeDataString(snapshotName).Replace("~", "%7E");
            var key = $"{S3Constants.SnapshotsFolder}/{encodedCollectionName}/{encodedSnapshotName}";
            
            logger.LogDebug("Downloading snapshot from S3: {Key} in bucket: {BucketName}", key, _currentConfig!.BucketName);
            
            var request = new GetObjectRequest
            {
                BucketName = _currentConfig!.BucketName,
                Key = key
            };

            var response = await client.GetObjectAsync(request, cancellationToken);
            
            logger.LogInformation("Downloaded snapshot {SnapshotName} for collection {CollectionName} from S3", 
                snapshotName, collectionName);

            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogWarning("Snapshot {SnapshotName} for collection {CollectionName} not found in S3", 
                snapshotName, collectionName);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to download snapshot {SnapshotName} for collection {CollectionName} from S3", 
                snapshotName, collectionName);
            return null;
        }
    }

    /// <summary>
    /// Deletes a snapshot from S3 storage
    /// </summary>
    public async Task<bool> DeleteSnapshotAsync(
        string collectionName,
        string snapshotName,
        string? namespaceParameter = null,
        CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateS3ClientAsync(namespaceParameter, cancellationToken);
        if (client == null)
        {
            logger.LogWarning("S3 client not available, cannot delete snapshot");
            return false;
        }

        try
        {
            // Path structure: {SnapshotsFolder}/{collection-name}/{snapshot-file}
            // NOTE: Uri.EscapeDataString() does NOT encode tildes (~), but S3 stores them as %7E
            var encodedCollectionName = Uri.EscapeDataString(collectionName).Replace("~", "%7E");
            var encodedSnapshotName = Uri.EscapeDataString(snapshotName).Replace("~", "%7E");
            var key = $"{S3Constants.SnapshotsFolder}/{encodedCollectionName}/{encodedSnapshotName}";
            
            logger.LogDebug("Deleting snapshot from S3: {Key} in bucket: {BucketName}", key, _currentConfig!.BucketName);
            
            var request = new DeleteObjectRequest
            {
                BucketName = _currentConfig!.BucketName,
                Key = key
            };

            await client.DeleteObjectAsync(request, cancellationToken);
            
            logger.LogInformation("Deleted snapshot {SnapshotName} for collection {CollectionName} from S3", 
                snapshotName, collectionName);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete snapshot {SnapshotName} for collection {CollectionName} from S3", 
                snapshotName, collectionName);
            return false;
        }
    }

    /// <summary>
    /// Checks if S3 storage is configured and available
    /// </summary>
    public async Task<bool> IsAvailableAsync(
        string? namespaceParameter = null,
        CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateS3ClientAsync(namespaceParameter, cancellationToken);
        return client != null;
    }

    /// <summary>
    /// Generates a presigned URL for downloading a snapshot from S3
    /// </summary>
    public async Task<string?> GetPresignedDownloadUrlAsync(
        string collectionName,
        string snapshotName,
        TimeSpan expiration,
        string? namespaceParameter = null,
        CancellationToken cancellationToken = default)
    {
        var s3Options = await configProvider.GetS3ConfigurationAsync(namespaceParameter, cancellationToken);
        if (s3Options == null || !s3Options.IsConfigured())
        {
            logger.LogWarning("S3 not configured, cannot generate presigned URL");
            return null;
        }

        try
        {
            // IMPORTANT: S3 keys are stored with URL-encoded tildes (%7E)
            // We need to find the actual key as stored in S3, then use it for the presigned URL
            // The collectionName and snapshotName from the API may or may not be encoded
            
            // First, decode inputs in case they're already encoded
            var decodedCollectionName = Uri.UnescapeDataString(collectionName);
            var decodedSnapshotName = Uri.UnescapeDataString(snapshotName);
            
            logger.LogDebug("GetPresignedDownloadUrlAsync - Input collection: '{CollectionName}', decoded: '{DecodedCollection}'", 
                collectionName, decodedCollectionName);
            logger.LogDebug("GetPresignedDownloadUrlAsync - Input snapshot: '{SnapshotName}', decoded: '{DecodedSnapshot}'", 
                snapshotName, decodedSnapshotName);
            
            // List objects to find the actual key in S3
            var client = await GetOrCreateS3ClientAsync(namespaceParameter, cancellationToken);
            if (client == null)
            {
                logger.LogWarning("S3 client not available, cannot generate presigned URL");
                return null;
            }
            
            // Encode the collection name for searching (tildes become %7E)
            // NOTE: Uri.EscapeDataString() does NOT encode tildes (~) because they are unreserved characters in RFC 3986
            // But S3 stores them as %7E, so we need to manually encode them
            var encodedCollectionName = Uri.EscapeDataString(decodedCollectionName).Replace("~", "%7E");
            var searchPrefix = $"{S3Constants.SnapshotsFolder}/{encodedCollectionName}/";
            
            logger.LogInformation("Searching for S3 object - Decoded collection: '{DecodedCollection}', Encoded: '{EncodedCollection}', Search prefix: '{Prefix}'", 
                decodedCollectionName, encodedCollectionName, searchPrefix);
            
            var listRequest = new ListObjectsV2Request
            {
                BucketName = s3Options.BucketName,
                Prefix = searchPrefix,
                MaxKeys = 100
            };
            
            var listResponse = await client.ListObjectsV2Async(listRequest, cancellationToken);
            
            logger.LogInformation("ListObjects returned {Count} objects for prefix {Prefix}", 
                listResponse.S3Objects?.Count ?? 0, searchPrefix);
            
            // Log all keys found
            if (listResponse.S3Objects != null && listResponse.S3Objects.Count > 0)
            {
                foreach (var obj in listResponse.S3Objects)
                {
                    logger.LogDebug("Found S3 object: {Key}", obj.Key);
                }
            }
            
            // Find the actual key in S3 - it should have the encoded collection name
            string? actualKey = null;
            foreach (var obj in listResponse.S3Objects ?? new List<S3Object>())
            {
                var fileName = Path.GetFileName(obj.Key);
                
                // The filename in S3 might have encoded tildes (%7E), so we need to:
                // 1. Decode both the S3 filename and the requested snapshot name
                // 2. Compare the decoded versions
                var fileNameDecoded = Uri.UnescapeDataString(fileName);
                
                logger.LogDebug("Comparing S3 file '{FileName}' (decoded: '{FileNameDecoded}') with requested '{SnapshotName}'", 
                    fileName, fileNameDecoded, decodedSnapshotName);
                
                if (fileNameDecoded == decodedSnapshotName)
                {
                    actualKey = obj.Key;
                    logger.LogInformation("Found matching S3 object: {Key}", actualKey);
                    break;
                }
            }
            
            if (actualKey == null)
            {
                var availableFiles = (listResponse.S3Objects ?? new List<S3Object>())
                    .Select(o => {
                        var fn = Path.GetFileName(o.Key);
                        return $"{fn} (decoded: {Uri.UnescapeDataString(fn)})";
                    })
                    .ToArray();
                    
                logger.LogWarning("Snapshot '{SnapshotName}' not found in S3 for collection '{CollectionName}'. Search prefix: '{SearchPrefix}'. Available files ({Count}): {AvailableFiles}", 
                    decodedSnapshotName, 
                    decodedCollectionName,
                    searchPrefix,
                    availableFiles.Length,
                    string.Join(", ", availableFiles));
                return null;
            }
            
            logger.LogDebug("Found S3 object key: {Key}", actualKey);
            
            // The actualKey is like: "snapshots/collection%7E%7Eversion/file.snapshot"
            // For the presigned URL, we need to pass the full key including "snapshots/"
            // The AwsSignatureV4 utility will URL-encode it properly (so %7E becomes %257E)
            
            // Use manual AWS Signature V4 implementation for S3-compatible storage
            // AWS SDK doesn't work correctly with custom endpoints for presigned URLs
            var url = AwsSignatureV4.GeneratePresignedUrl(
                s3Options.EndpointUrl!,
                s3Options.BucketName!,
                actualKey, // Use the full key as stored in S3
                s3Options.AccessKey!.Trim(),
                s3Options.SecretKey!.Trim(),
                s3Options.Region ?? S3Constants.DefaultRegion,
                (int)expiration.TotalSeconds);
            
            logger.LogInformation("Generated presigned URL for snapshot {SnapshotName} in collection {CollectionName}, expires in {Expiration}", 
                decodedSnapshotName, decodedCollectionName, expiration);
            logger.LogDebug("Generated presigned URL: {Url}", url);
            
            return url;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to generate presigned URL for snapshot {SnapshotName} in collection {CollectionName}", 
                snapshotName, collectionName);
            return null;
        }
    }

    private async Task<IAmazonS3?> GetOrCreateS3ClientAsync(
        string? namespaceParameter,
        CancellationToken cancellationToken)
    {
        // Return existing client if config hasn't changed
        if (_s3Client != null && _currentConfig != null)
        {
            return _s3Client;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            if (_s3Client != null && _currentConfig != null)
            {
                return _s3Client;
            }

            var s3Options = await configProvider.GetS3ConfigurationAsync(namespaceParameter, cancellationToken);
            if (s3Options == null || !s3Options.IsConfigured())
            {
                logger.LogDebug("S3 not configured");
                return null;
            }

            _currentConfig = s3Options;

            // Log credential info (first 4 characters only for security)
            logger.LogDebug("Creating S3 client with AccessKey: {AccessKeyPrefix}*** (length: {AccessKeyLength}), SecretKey: {SecretKeyPrefix}*** (length: {SecretKeyLength})", 
                s3Options.AccessKey?.Length > 4 ? s3Options.AccessKey.Substring(0, 4) : "???",
                s3Options.AccessKey?.Length ?? 0,
                s3Options.SecretKey?.Length > 4 ? s3Options.SecretKey.Substring(0, 4) : "???",
                s3Options.SecretKey?.Length ?? 0);

            var config = new AmazonS3Config
            {
                ServiceURL = s3Options.EndpointUrl,
                ForcePathStyle = true,
                UseHttp = s3Options.EndpointUrl?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ?? false,
                // For S3-compatible storage, only set AuthenticationRegion, NOT RegionEndpoint
                // Setting RegionEndpoint causes AWS-specific auth that breaks custom S3 storage
                AuthenticationRegion = s3Options.Region ?? S3Constants.DefaultRegion
            };

            var accessKey = s3Options.AccessKey?.Trim();
            var secretKey = s3Options.SecretKey?.Trim();
            
            if (string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey))
            {
                logger.LogError("AccessKey or SecretKey is null or empty after trimming");
                return null;
            }

            // Use BasicAWSCredentials for S3-compatible storage
            var credentials = new BasicAWSCredentials(accessKey, secretKey);
            
            _s3Client = new AmazonS3Client(credentials, config);

            logger.LogInformation("S3 client created for endpoint {EndpointUrl}", s3Options.EndpointUrl);

            return _s3Client;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _s3Client?.Dispose();
        _lock.Dispose();
    }
}

