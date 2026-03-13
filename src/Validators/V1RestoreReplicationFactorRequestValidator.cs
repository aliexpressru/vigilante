using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1RestoreReplicationFactorRequestValidator : AbstractValidator<V1RestoreReplicationFactorRequest>
{
    public V1RestoreReplicationFactorRequestValidator()
    {
        RuleFor(x => x.CollectionName)
            .NotEmpty()
            .WithMessage("CollectionName is required.");
    }
}
