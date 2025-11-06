using Aer.QdrantClient.Http.Models.Responses.Base;

namespace Vigilante.Extensions;

public static class QdrantResponseExtensions
{
    /// <summary>
    /// Checks if the Qdrant operation was accepted (for async operations with wait=false)
    /// or completed successfully (for sync operations with wait=true).
    /// </summary>
    /// <param name="response">The Qdrant response to check</param>
    /// <returns>True if the operation was accepted or successful, false otherwise</returns>
    public static bool IsAcceptedOrSuccess<T>(this QdrantResponseBase<T>? response)
    {
        if (response?.Status == null)
            return false;
            
        // Check for "accepted" status (async operations with wait=false)
        if (response.Status.RawStatusString?.Equals("accepted", StringComparison.OrdinalIgnoreCase) == true)
            return true;
            
        // Check for successful completion (sync operations with wait=true)
        return response.Status.IsSuccess;
    }
    
    /// <summary>
    /// Checks if the Qdrant operation was accepted for async execution (wait=false).
    /// </summary>
    /// <param name="response">The Qdrant response to check</param>
    /// <returns>True if the operation was accepted, false otherwise</returns>
    public static bool IsAccepted<T>(this QdrantResponseBase<T>? response)
    {
        return response?.Status?.RawStatusString?.Equals("accepted", StringComparison.OrdinalIgnoreCase) == true;
    }
}

