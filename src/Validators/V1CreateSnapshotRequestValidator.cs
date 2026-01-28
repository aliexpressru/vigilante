using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1CreateSnapshotRequestValidator : AbstractValidator<V1CreateSnapshotRequest>
{
    public V1CreateSnapshotRequestValidator()
    {
        RuleFor(x => x.CollectionName)
            .NotEmpty()
            .WithMessage("Collection name is required");

        RuleFor(x => x.NodeUrls)
            .NotNull()
            .WithMessage("NodeUrls list is required")
            .Must(list => list != null && list.Count > 0)
            .WithMessage("NodeUrls list must contain at least one node URL");

        RuleForEach(x => x.NodeUrls)
            .NotEmpty()
            .WithMessage("Node URL cannot be empty")
            .Must(uri => Uri.TryCreate(uri, UriKind.Absolute, out _))
            .WithMessage("Node URL must be a valid absolute URI");
    }
}
