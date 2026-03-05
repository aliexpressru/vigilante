using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1RemovePeerRequestValidator : AbstractValidator<V1RemovePeerRequest>
{
    public V1RemovePeerRequestValidator()
    {
        RuleFor(x => x.TimeoutSeconds)
            .GreaterThan(0)
            .When(x => x.TimeoutSeconds.HasValue)
            .WithMessage("Timeout must be positive when specified");
    }
}
