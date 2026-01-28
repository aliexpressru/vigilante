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

        RuleFor(x => x.Source)
            .IsInEnum()
            .WithMessage("Invalid snapshot source");

        // NodeUrls is required for QdrantApi source
        When(x => x.Source == SnapshotSource.QdrantApi, () =>
        {
            RuleFor(x => x.NodeUrls)
                .NotNull()
                .WithMessage("NodeUrls list is required for QdrantApi source")
                .Must(list => list != null && list.Count > 0)
                .WithMessage("NodeUrls list must contain at least one node URL");

            RuleForEach(x => x.NodeUrls)
                .NotEmpty()
                .WithMessage("Node URL cannot be empty")
                .Must(uri => Uri.TryCreate(uri, UriKind.Absolute, out _))
                .WithMessage("Node URL must be a valid absolute URI");
        });

        // Pods list is required for KubernetesStorage source
        When(x => x.Source == SnapshotSource.KubernetesStorage, () =>
        {
            RuleFor(x => x.Pods)
                .NotNull()
                .WithMessage("Pods list is required for KubernetesStorage source")
                .Must(list => list != null && list.Count > 0)
                .WithMessage("Pods list must contain at least one pod");

            RuleForEach(x => x.Pods)
                .ChildRules(pod =>
                {
                    pod.RuleFor(p => p.PodName)
                        .NotEmpty()
                        .WithMessage("Pod name is required");

                    pod.RuleFor(p => p.PodNamespace)
                        .NotEmpty()
                        .WithMessage("Pod namespace is required");
                });
        });

        // For S3Storage source, no additional validation needed (single operation)
    }
}
