using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1GetVigilanteLogsRequestValidator : AbstractValidator<V1GetVigilanteLogsRequest>
{
    public V1GetVigilanteLogsRequestValidator()
    {
        RuleFor(x => x.Limit)
            .GreaterThan(0)
            .LessThanOrEqualTo(1000);
    }
}

