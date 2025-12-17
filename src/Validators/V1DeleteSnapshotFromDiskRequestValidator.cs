using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1DeleteSnapshotFromDiskRequestValidator : AbstractValidator<V1DeleteSnapshotFromDiskRequest>
{
    public V1DeleteSnapshotFromDiskRequestValidator()
    {
        RuleFor(x => x.PodName)
            .NotEmpty();
        
        RuleFor(x => x.PodNamespace)
            .NotEmpty();
        
        RuleFor(x => x.CollectionName)
            .NotEmpty();
        
        RuleFor(x => x.SnapshotName)
            .NotEmpty()
            .Must(name => name!.EndsWith(".snapshot"))
            .WithMessage("SnapshotName must end with .snapshot extension");
    }
}

