using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1RecoverFromDiskSnapshotRequestValidator : AbstractValidator<V1RecoverFromDiskSnapshotRequest>
{
    public V1RecoverFromDiskSnapshotRequestValidator()
    {
        RuleFor(x => x.NodeUrl)
            .NotEmpty()
            .WithMessage("NodeUrl is required")
            .Must(url => Uri.TryCreate(url, UriKind.Absolute, out _))
            .WithMessage("NodeUrl must be a valid URL");
        
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

