using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1SetCollectionAliasRequestValidator : AbstractValidator<V1SetCollectionAliasRequest>
{
    public V1SetCollectionAliasRequestValidator()
    {
        RuleFor(x => x.CollectionName)
            .NotEmpty()
            .WithMessage("Collection name is required");

        RuleFor(x => x.AliasName)
            .NotEmpty()
            .WithMessage("Alias name is required");
    }
}
