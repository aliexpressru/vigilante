using Vigilante.Models;
using Vigilante.Models.Enums;

namespace Vigilante.Services.Interfaces;

public interface IMeterService
{
    void UpdateAliveNodes(int count);

    void UpdateCollectionSize(CollectionSize collectionSize);

    void UpdateClusterNeedsAttention(bool needsAttention, ClusterAttentionReason reason = ClusterAttentionReason.None);
}