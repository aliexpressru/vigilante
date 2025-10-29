namespace Vigilante.Models;

public class CollectionSize
{
    private static readonly string[] SizeSuffixes = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };

    public string PodName { get; set; }

    public string NodeUrl { get; set; }

    public string PeerId { get; set; }

    public string CollectionName { get; set; }

    public long SizeBytes { get; set; }

    public string PrettySize
    {
        get
        {
            if (SizeBytes == 0)
                return "0 B";

            var mag = (int)Math.Log(SizeBytes, 1024);
            var adjustedSize = Math.Round(SizeBytes / Math.Pow(1024, mag), 1);

            return $"{adjustedSize} {SizeSuffixes[mag]}";
        }
    }
}