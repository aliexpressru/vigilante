using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1RestoreReplicationFactorCancelRequestValidator : AbstractValidator<V1RestoreReplicationFactorCancelRequest>
{
    public V1RestoreReplicationFactorCancelRequestValidator()
    {
        RuleFor(x => x.CollectionName)
            .NotEmpty()
            .WithMessage("CollectionName is required.");
    }
}
