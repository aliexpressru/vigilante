using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1MultiRecoverRequestValidator : AbstractValidator<V1MultiRecoverRequest>
{
    public V1MultiRecoverRequestValidator()
    {
        RuleFor(x => x.TargetCollectionName)
            .NotEmpty()
            .WithMessage("TargetCollectionName is required.");

        RuleFor(x => x.Items)
            .NotEmpty()
            .WithMessage("Items list must contain at least one recovery item.");

        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.TargetNodeUrl)
                .NotEmpty()
                .WithMessage("TargetNodeUrl is required.");

            item.RuleFor(i => i.SnapshotName)
                .NotEmpty()
                .WithMessage("SnapshotName is required.");

            item.RuleFor(i => i.SnapshotSource)
                .NotEmpty()
                .WithMessage("SnapshotSource is required.");
        });
    }
}
