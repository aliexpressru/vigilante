using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1RecoverFromDiskSnapshotRequestValidator : AbstractValidator<V1RecoverFromDiskSnapshotRequest>
{
    public V1RecoverFromDiskSnapshotRequestValidator()
    {
        RuleFor(x => x.NodeUrl)
            .NotEmpty()
            .Must(url => Uri.TryCreate(url, UriKind.Absolute, out _));
        
        RuleFor(x => x.CollectionName)
            .NotEmpty();
        
        RuleFor(x => x.SnapshotName)
            .NotEmpty()
            .Must(name => name!.EndsWith(".snapshot"))
            .WithMessage("SnapshotName must end with .snapshot extension");
    }
}

