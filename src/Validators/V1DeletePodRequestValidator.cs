using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1DeletePodRequestValidator : AbstractValidator<V1DeletePodRequest>
{
    public V1DeletePodRequestValidator()
    {
        RuleFor(x => x.PodName)
            .NotEmpty();
    }
}

