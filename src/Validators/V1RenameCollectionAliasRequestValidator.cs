using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1RenameCollectionAliasRequestValidator : AbstractValidator<V1RenameCollectionAliasRequest>
{
    public V1RenameCollectionAliasRequestValidator()
    {
        RuleFor(x => x.OldAliasName)
            .NotEmpty()
            .WithMessage("Old alias name is required");

        RuleFor(x => x.NewAliasName)
            .NotEmpty()
            .WithMessage("New alias name is required");
    }
}
