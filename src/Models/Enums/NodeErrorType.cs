namespace Vigilante.Models.Enums;

public enum NodeErrorType
{
    None = 0,
    Timeout,
    ConnectionError,
    InvalidResponse,
    ClusterSplit
}

