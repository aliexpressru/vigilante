using Aer.QdrantClient.Http.Models.Shared;
using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1StartReshardingRequestValidator : AbstractValidator<V1StartReshardingRequest>
{
    public V1StartReshardingRequestValidator()
    {
        RuleFor(x => x.CollectionName)
            .NotEmpty()
            .WithMessage("Collection name is required");

        RuleFor(x => x.Direction)
            .NotEmpty()
            .WithMessage("Direction is required")
            .Must(direction => Enum.TryParse<ReshardingOperationDirection>(direction, true, out _))
            .WithMessage($"Direction must be one of: {string.Join(", ", Enum.GetNames<ReshardingOperationDirection>())}");
    }
}
