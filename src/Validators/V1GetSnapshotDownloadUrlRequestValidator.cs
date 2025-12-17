using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1GetSnapshotDownloadUrlRequestValidator : AbstractValidator<V1GetSnapshotDownloadUrlRequest>
{
    public V1GetSnapshotDownloadUrlRequestValidator()
    {
        RuleFor(x => x.CollectionName)
            .NotEmpty();
        
        RuleFor(x => x.SnapshotName)
            .NotEmpty();
        
        RuleFor(x => x.ExpirationSeconds)
            .GreaterThan(0)
            .LessThanOrEqualTo(604800) // 7 days max
            .WithMessage("Expiration seconds cannot exceed 7 days (604800 seconds)");
    }
}

