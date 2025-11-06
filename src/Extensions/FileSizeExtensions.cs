namespace Vigilante.Extensions;

public static class FileSizeExtensions
{
    private static readonly string[] SizeSuffixes = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };

    /// <summary>
    /// Converts bytes to a human-readable format (e.g., "1.2 GB")
    /// </summary>
    public static string ToPrettySize(this long sizeBytes)
    {
        if (sizeBytes == 0)
            return "0 B";

        var mag = (int)Math.Log(sizeBytes, 1024);
        var adjustedSize = Math.Round(sizeBytes / Math.Pow(1024, mag), 1);

        return $"{adjustedSize} {SizeSuffixes[mag]}";
    }
}

