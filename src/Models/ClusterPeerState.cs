using System.Collections.Concurrent;

namespace Vigilante.Models;

public class ClusterPeerState
{
    private HashSet<string> _majorityPeerIds = new();
    private readonly Lock _syncLock = new();
    
    public IReadOnlySet<string> MajorityPeerIds => _majorityPeerIds;

    public bool TryUpdateMajorityState(IEnumerable<NodeInfo> nodes)
    {
        // Materialize the list of healthy nodes once
        var healthyNodes = nodes.Where(n => n.IsHealthy).ToList();
        if (!healthyNodes.Any())
        {
            return false;
        }

        // Group nodes by their peer lists to find the predominant set
        var peerGroups = healthyNodes
            .GroupBy(n => string.Join(",", n.CurrentPeerIds.OrderBy(p => p)))
            .Select(g => new { PeerSet = new HashSet<string>(g.First().CurrentPeerIds), Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ToList();

        var majorityGroup = peerGroups.First();
        
        // Check if the predominant peer set is present in the majority of nodes
        if (majorityGroup.Count > healthyNodes.Count / 2)
        {
            lock (_syncLock)
            {
                _majorityPeerIds = majorityGroup.PeerSet;
            }
            return true;
        }

        // Edge case: If no strict majority exists (e.g., 2 nodes with different views),
        // use the peer set with the most peers as a tie-breaker (more complete view)
        if (peerGroups.Count > 1)
        {
            var largestPeerSet = peerGroups.OrderByDescending(g => g.PeerSet.Count).First();
            lock (_syncLock)
            {
                _majorityPeerIds = largestPeerSet.PeerSet;
            }
            return true;
        }

        return false;
    }

    public bool IsNodeConsistentWithMajority(NodeInfo node, out string? inconsistencyReason)
    {
        inconsistencyReason = null;
        
        lock (_syncLock)
        {
            if (!_majorityPeerIds.Any())
            {
                // If we don't have an established majority state yet, consider the node consistent
                return true;
            }

            var expectedPeers = new HashSet<string>(_majorityPeerIds);
            expectedPeers.Remove(node.PeerId); // Remove self from expected list

            var currentPeers = new HashSet<string>(node.CurrentPeerIds);
            currentPeers.Remove(node.PeerId); // Remove self from current list

            // Check differences between expected and current state
            var unexpectedPeers = currentPeers.Except(expectedPeers).ToList();
            var missingPeers = expectedPeers.Except(currentPeers).ToList();

            if (unexpectedPeers.Any() || missingPeers.Any())
            {
                inconsistencyReason = BuildInconsistencyReason(unexpectedPeers, missingPeers);
                return false;
            }

            return true;
        }
    }

    private string BuildInconsistencyReason(List<string> unexpectedPeers, List<string> missingPeers)
    {
        var reasons = new List<string>();
        
        if (unexpectedPeers.Any())
        {
            reasons.Add($"Unexpected peers: {string.Join(", ", unexpectedPeers)}");
        }
        
        if (missingPeers.Any())
        {
            reasons.Add($"Missing peers: {string.Join(", ", missingPeers)}");
        }

        return string.Join("; ", reasons);
    }
}
