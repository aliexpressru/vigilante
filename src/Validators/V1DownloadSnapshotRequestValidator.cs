using FluentValidation;
using Vigilante.Models.Enums;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1DownloadSnapshotRequestValidator : AbstractValidator<V1DownloadSnapshotRequest>
{
    public V1DownloadSnapshotRequestValidator()
    {
        RuleFor(x => x.CollectionName)
            .NotEmpty();
        
        RuleFor(x => x.SnapshotName)
            .NotEmpty();
        
        // NodeUrl is required for QdrantApi and KubernetesStorage sources
        When(x => x.Source == SnapshotSource.QdrantApi || x.Source == SnapshotSource.KubernetesStorage, () =>
        {
            RuleFor(x => x.NodeUrl)
                .NotEmpty()
                .Must(uri => Uri.TryCreate(uri, UriKind.Absolute, out _));
        });
        
        // PodName and PodNamespace are required for KubernetesStorage source
        RuleFor(x => x.PodName)
            .NotEmpty()
            .When(x => x.Source == SnapshotSource.KubernetesStorage);
        
        RuleFor(x => x.PodNamespace)
            .NotEmpty()
            .When(x => x.Source == SnapshotSource.KubernetesStorage);
    }
}

