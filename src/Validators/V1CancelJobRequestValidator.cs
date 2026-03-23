using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1CancelJobRequestValidator : AbstractValidator<V1CancelJobRequest>
{
    public V1CancelJobRequestValidator()
    {
        RuleFor(x => x.Key)
            .NotEmpty()
            .WithMessage("Job key is required");
    }
}
