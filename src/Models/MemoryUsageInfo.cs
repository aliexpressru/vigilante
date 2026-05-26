using Vigilante.Extensions;

namespace Vigilante.Models;

public sealed class MemoryUsageInfo
{
    public ulong DiskBytes { get; init; }

    public ulong RamBytes { get; init; }

    public ulong CachedBytes { get; init; }

    public ulong ExpectedCacheBytes { get; init; }

    public string PrettyDisk => ((long)DiskBytes).ToPrettySize();

    public string PrettyRam => ((long)RamBytes).ToPrettySize();

    public string PrettyCached => ((long)CachedBytes).ToPrettySize();

    public string PrettyExpectedCache => ((long)ExpectedCacheBytes).ToPrettySize();
}
