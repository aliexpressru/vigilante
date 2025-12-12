using FluentValidation;
using Vigilante.Models.Enums;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1DownloadSnapshotRequestValidator : AbstractValidator<V1DownloadSnapshotRequest>
{
    public V1DownloadSnapshotRequestValidator()
    {
        RuleFor(x => x.CollectionName)
            .NotEmpty()
            .WithMessage("Collection name is required");
        
        RuleFor(x => x.SnapshotName)
            .NotEmpty()
            .WithMessage("Snapshot name is required");
        
        // NodeUrl is required for QdrantApi and KubernetesStorage sources
        When(x => x.Source == SnapshotSource.QdrantApi || x.Source == SnapshotSource.KubernetesStorage, () =>
        {
            RuleFor(x => x.NodeUrl)
                .NotEmpty()
                .WithMessage("Node URL is required for Qdrant API and Kubernetes storage sources")
                .Must(uri => Uri.TryCreate(uri, UriKind.Absolute, out _))
                .WithMessage("Node URL must be a valid URL");
        });
        
        // PodName and PodNamespace are required for KubernetesStorage source
        RuleFor(x => x.PodName)
            .NotEmpty()
            .WithMessage("Pod name is required for Kubernetes storage source")
            .When(x => x.Source == SnapshotSource.KubernetesStorage);
        
        RuleFor(x => x.PodNamespace)
            .NotEmpty()
            .WithMessage("Pod namespace is required for Kubernetes storage source")
            .When(x => x.Source == SnapshotSource.KubernetesStorage);
    }
}

