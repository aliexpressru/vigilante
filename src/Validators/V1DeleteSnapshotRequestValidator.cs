using FluentValidation;
using Vigilante.Models.Enums;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1DeleteSnapshotRequestValidator : AbstractValidator<V1DeleteSnapshotRequest>
{
    public V1DeleteSnapshotRequestValidator()
    {
        RuleFor(x => x.CollectionName)
            .NotEmpty()
            .WithMessage("Collection name is required");

        RuleFor(x => x.SnapshotName)
            .NotEmpty()
            .WithMessage("Snapshot name is required");

        // Source is required when deleting from single node
        RuleFor(x => x.Source)
            .NotEmpty()
            .WithMessage("Source is required when deleting from a single node")
            .When(x => x.SingleNode);

        // Validate Source is a valid enum value
        RuleFor(x => x.Source)
            .Must(source => Enum.TryParse<SnapshotSource>(source, out _))
            .WithMessage("Source must be a valid snapshot source (QdrantApi, KubernetesStorage, or S3)")
            .When(x => !string.IsNullOrWhiteSpace(x.Source));

        // NodeUrl is required for QdrantApi source when deleting from single node
        RuleFor(x => x.NodeUrl)
            .NotEmpty()
            .WithMessage("Node URL is required for Qdrant API source")
            .When(x => x.SingleNode && x.Source == SnapshotSource.QdrantApi.ToString());

        // Validate NodeUrl format only when it's provided
        RuleFor(x => x.NodeUrl)
            .Must(uri => Uri.TryCreate(uri, UriKind.Absolute, out _))
            .WithMessage("Node URL must be a valid URL")
            .When(x => !string.IsNullOrWhiteSpace(x.NodeUrl));

        // PodName and PodNamespace are required for KubernetesStorage source when deleting from single node
        RuleFor(x => x.PodName)
            .NotEmpty()
            .WithMessage("Pod name is required for Kubernetes storage source")
            .When(x => x.SingleNode && x.Source == SnapshotSource.KubernetesStorage.ToString());

        RuleFor(x => x.PodNamespace)
            .NotEmpty()
            .WithMessage("Pod namespace is required for Kubernetes storage source")
            .When(x => x.SingleNode && x.Source == SnapshotSource.KubernetesStorage.ToString());
    }
}

