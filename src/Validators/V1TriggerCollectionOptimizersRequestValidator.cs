using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

internal class V1TriggerCollectionOptimizersRequestValidator : AbstractValidator<V1TriggerCollectionOptimizersRequest>
{
    public V1TriggerCollectionOptimizersRequestValidator()
    {
        RuleFor(x => x.CollectionName)
            .NotEmpty()
            .WithMessage("Collection name is required");
    }
}
