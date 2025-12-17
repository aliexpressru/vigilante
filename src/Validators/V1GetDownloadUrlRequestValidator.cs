using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1GetDownloadUrlRequestValidator : AbstractValidator<V1GetDownloadUrlRequest>
{
    public V1GetDownloadUrlRequestValidator()
    {
        RuleFor(x => x.CollectionName)
            .NotEmpty();
        
        RuleFor(x => x.SnapshotName)
            .NotEmpty();
        
        RuleFor(x => x.ExpirationHours)
            .GreaterThan(0)
            .LessThanOrEqualTo(168) // 7 days max
            .WithMessage("Expiration hours cannot exceed 168 hours (7 days)");
    }
}

