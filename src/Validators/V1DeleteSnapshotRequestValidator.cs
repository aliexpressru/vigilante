using FluentValidation;
using Vigilante.Models.Enums;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1DeleteSnapshotRequestValidator : AbstractValidator<V1DeleteSnapshotRequest>
{
    public V1DeleteSnapshotRequestValidator()
    {
        RuleFor(x => x.CollectionName)
            .NotEmpty();

        RuleFor(x => x.SnapshotName)
            .NotEmpty();

        // Source is required when deleting from single node
        RuleFor(x => x.Source)
            .NotEmpty()
            .When(x => x.SingleNode);

        // Validate Source is a valid enum value
        RuleFor(x => x.Source)
            .Must(source => Enum.TryParse<SnapshotSource>(source, out _))
            .When(x => !string.IsNullOrWhiteSpace(x.Source));

        // NodeUrl is required for QdrantApi source when deleting from single node
        RuleFor(x => x.NodeUrl)
            .NotEmpty()
            .When(x => x.SingleNode && x.Source == SnapshotSource.QdrantApi.ToString());

        // Validate NodeUrl format only when it's provided
        RuleFor(x => x.NodeUrl)
            .Must(uri => Uri.TryCreate(uri, UriKind.Absolute, out _))
            .When(x => !string.IsNullOrWhiteSpace(x.NodeUrl));

        // PodName and PodNamespace are required for KubernetesStorage source when deleting from single node
        RuleFor(x => x.PodName)
            .NotEmpty()
            .When(x => x.SingleNode && x.Source == SnapshotSource.KubernetesStorage.ToString());

        RuleFor(x => x.PodNamespace)
            .NotEmpty()
            .When(x => x.SingleNode && x.Source == SnapshotSource.KubernetesStorage.ToString());
    }
}

