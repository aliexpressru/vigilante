using Vigilante.Models;

namespace Vigilante.Services.Interfaces;

public interface IMeterService
{
    void UpdateAliveNodes(int count);
    void UpdateCollectionSize(CollectionSize collectionSize);
    void UpdateClusterNeedsAttention(bool needsAttention);
}

