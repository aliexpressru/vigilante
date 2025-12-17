using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1GetSnapshotsInfoRequestValidator : AbstractValidator<V1GetSnapshotsInfoRequest>
{
    public V1GetSnapshotsInfoRequestValidator()
    {
        RuleFor(x => x.Page)
            .GreaterThan(0);
        
        RuleFor(x => x.PageSize)
            .GreaterThan(0)
            .LessThanOrEqualTo(1000);
        
        RuleFor(x => x.NameFilter)
            .MaximumLength(500)
            .When(x => !string.IsNullOrWhiteSpace(x.NameFilter));
    }
}

