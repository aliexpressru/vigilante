using System.Security.Cryptography;
using System.Text;

namespace Vigilante.Utilities;

/// <summary>
/// Utility class for generating AWS Signature Version 4 signatures for presigned URLs
/// This is specifically designed for S3-compatible storage with custom endpoints
/// </summary>
public static class AwsSignatureV4
{
    /// <summary>
    /// Generates a presigned URL for S3-compatible storage
    /// Note: objectKey should already be properly URL-encoded to match S3 storage format (e.g., tildes as %7E)
    /// This method will further encode the key for use in the URL path (so %7E becomes %257E)
    /// </summary>
    public static string GeneratePresignedUrl(
        string endpoint,
        string bucketName,
        string objectKey,
        string accessKey,
        string secretKey,
        string region,
        int expirationSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToString("yyyyMMddTHHmmssZ");
        var datestamp = now.ToString("yyyyMMdd");
        
        // Build the canonical URI
        // The objectKey comes in already encoded (e.g., snapshots/collection%7E%7Eversion/file.snapshot)
        // For the URL path, we need to encode each path segment separately
        // So %7E in the key becomes %257E in the final URL
        var pathSegments = objectKey.Split('/');
        var encodedSegments = pathSegments.Select(segment => Uri.EscapeDataString(segment));
        var encodedObjectKey = string.Join("/", encodedSegments);
        var canonicalUri = $"/{bucketName}/{encodedObjectKey}";
        
        // Build credential scope
        var credentialScope = $"{datestamp}/{region}/s3/aws4_request";
        
        // Build the canonical query string
        var queryParams = new SortedDictionary<string, string>
        {
            { "X-Amz-Algorithm", "AWS4-HMAC-SHA256" },
            { "X-Amz-Credential", $"{accessKey}/{credentialScope}" },
            { "X-Amz-Date", timestamp },
            { "X-Amz-Expires", expirationSeconds.ToString() },
            { "X-Amz-SignedHeaders", "host" }
        };
        
        var canonicalQueryString = string.Join("&",
            queryParams.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
        
        // Extract host from endpoint
        var uri = new Uri(endpoint);
        var host = uri.Host;
        if ((uri.Scheme == "https" && uri.Port != 443) || (uri.Scheme == "http" && uri.Port != 80))
        {
            host = $"{host}:{uri.Port}";
        }
        
        // Build canonical request
        var canonicalHeaders = $"host:{host}\n";
        var signedHeaders = "host";
        
        var canonicalRequest = string.Join("\n",
            "GET",
            canonicalUri,
            canonicalQueryString,
            canonicalHeaders,
            signedHeaders,
            "UNSIGNED-PAYLOAD");
        
        // Create string to sign
        using var sha256 = SHA256.Create();
        var canonicalRequestHash = BitConverter.ToString(
            sha256.ComputeHash(Encoding.UTF8.GetBytes(canonicalRequest)))
            .Replace("-", "").ToLowerInvariant();
        
        var stringToSign = string.Join("\n",
            "AWS4-HMAC-SHA256",
            timestamp,
            credentialScope,
            canonicalRequestHash);
        
        // Calculate signature
        var signature = CalculateSignature(secretKey, datestamp, region, stringToSign);
        
        // Build final URL
        var url = $"{endpoint.TrimEnd('/')}{canonicalUri}?{canonicalQueryString}&X-Amz-Signature={signature}";
        
        return url;
    }
    
    private static string CalculateSignature(string secretKey, string datestamp, string region, string stringToSign)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes($"AWS4{secretKey}"));
        
        var dateKey = hmac.ComputeHash(Encoding.UTF8.GetBytes(datestamp));
        
        hmac.Key = dateKey;
        var dateRegionKey = hmac.ComputeHash(Encoding.UTF8.GetBytes(region));
        
        hmac.Key = dateRegionKey;
        var dateRegionServiceKey = hmac.ComputeHash(Encoding.UTF8.GetBytes("s3"));
        
        hmac.Key = dateRegionServiceKey;
        var signingKey = hmac.ComputeHash(Encoding.UTF8.GetBytes("aws4_request"));
        
        hmac.Key = signingKey;
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
        
        return BitConverter.ToString(signature).Replace("-", "").ToLowerInvariant();
    }
}

