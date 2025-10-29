using System.Diagnostics.Metrics;
using Vigilante.Models;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

public class MeterService : IMeterService
{
    public const string MeterName = "vigilante";
    private int _aliveNodesCount;
    private readonly Dictionary<(string Pod, string Collection), long> _collectionSizes = new();
    
    public MeterService(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        
        meter.CreateObservableGauge(
            name: $"{MeterName}_healthy_nodes",
            observeValue: () => _aliveNodesCount,
            unit: "{nodes}",
            description: "Current number of alive nodes in the cluster");

        meter.CreateObservableGauge(
            name: $"{MeterName}_collection_size_bytes",
            description: "Size of Qdrant collection in bytes",
            unit: "B",
            observeValues: GetCollectionSizeMeasurements);
    }

    private IEnumerable<Measurement<long>> GetCollectionSizeMeasurements()
    {
        return _collectionSizes.Select(pair => new Measurement<long>(
            pair.Value,
            new KeyValuePair<string, object?>[]
            {
                new("pod", pair.Key.Pod),
                new("collection", pair.Key.Collection)
            }));
    }
    
    public void UpdateAliveNodes(int count)
    {
        Interlocked.Exchange(ref _aliveNodesCount, count);
    }

    public void UpdateCollectionSize(CollectionSize collectionSize)
    {
        _collectionSizes[(collectionSize.PodName, collectionSize.CollectionName)] = collectionSize.SizeBytes;
    }
}