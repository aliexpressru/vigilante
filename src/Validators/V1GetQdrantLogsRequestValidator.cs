using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1GetQdrantLogsRequestValidator : AbstractValidator<V1GetQdrantLogsRequest>
{
    public V1GetQdrantLogsRequestValidator()
    {
        RuleFor(x => x.PodName)
            .NotEmpty()
            .WithMessage("Pod name is required");
        
        RuleFor(x => x.Limit)
            .GreaterThan(0)
            .LessThanOrEqualTo(1000);
    }
}
