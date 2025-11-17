using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1RecoverFromUrlRequestValidator : AbstractValidator<V1RecoverFromUrlRequest>
{
    public V1RecoverFromUrlRequestValidator()
    {
        RuleFor(x => x.NodeUrl)
            .NotEmpty()
            .WithMessage("NodeUrl is required")
            .Must(BeValidUrl)
            .WithMessage("NodeUrl must be a valid URL");

        RuleFor(x => x.CollectionName)
            .NotEmpty()
            .WithMessage("CollectionName is required");

        RuleFor(x => x.SnapshotUrl)
            .NotEmpty()
            .WithMessage("SnapshotUrl is required")
            .Must(BeValidUrl)
            .WithMessage("SnapshotUrl must be a valid URL");
    }

    private bool BeValidUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out _);
    }
}

