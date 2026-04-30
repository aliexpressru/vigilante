using FluentValidation;
using Vigilante.Models;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1GetVigilanteLogsRequestValidator : AbstractValidator<V1GetVigilanteLogsRequest>
{
    public V1GetVigilanteLogsRequestValidator()
    {
        RuleFor(x => x.Limit)
            .GreaterThan(0)
            .LessThanOrEqualTo(1000);

        RuleFor(x => x.SearchText)
            .MaximumLength(512)
            .When(x => !string.IsNullOrWhiteSpace(x.SearchText));

        RuleFor(x => x.Levels)
            .Must(levels => levels is null || (levels.Value & ~LogLevelFilter.All) == 0)
            .WithMessage("Levels contains unsupported flags");
    }
}

