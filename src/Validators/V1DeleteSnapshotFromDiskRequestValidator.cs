using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1DeleteSnapshotFromDiskRequestValidator : AbstractValidator<V1DeleteSnapshotFromDiskRequest>
{
    public V1DeleteSnapshotFromDiskRequestValidator()
    {
        RuleFor(x => x.PodName)
            .NotEmpty()
            .WithMessage("PodName is required");
        
        RuleFor(x => x.PodNamespace)
            .NotEmpty()
            .WithMessage("PodNamespace is required");
        
        RuleFor(x => x.CollectionName)
            .NotEmpty()
            .WithMessage("CollectionName is required");
        
        RuleFor(x => x.SnapshotName)
            .NotEmpty()
            .WithMessage("SnapshotName is required")
            .Must(name => name!.EndsWith(".snapshot"))
            .WithMessage("SnapshotName must end with .snapshot extension");
    }
}

