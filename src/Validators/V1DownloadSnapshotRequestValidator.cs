using FluentValidation;
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
        
        RuleFor(x => x.NodeUrl)
            .NotEmpty()
            .WithMessage("Node URL is required")
            .Must(uri => Uri.TryCreate(uri, UriKind.Absolute, out _))
            .WithMessage("Node URL must be a valid URL");
        
        RuleFor(x => x.PodName)
            .NotEmpty()
            .WithMessage("Pod name is required");
        
        RuleFor(x => x.PodNamespace)
            .NotEmpty()
            .WithMessage("Pod namespace is required");
    }
}

