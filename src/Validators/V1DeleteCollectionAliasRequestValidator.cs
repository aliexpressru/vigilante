using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1DeleteCollectionAliasRequestValidator : AbstractValidator<V1DeleteCollectionAliasRequest>
{
    public V1DeleteCollectionAliasRequestValidator()
    {
        RuleFor(x => x.AliasName)
            .NotEmpty()
            .WithMessage("Alias name is required");
    }
}
