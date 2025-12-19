using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1RecoverFromUrlRequestValidator : AbstractValidator<V1RecoverFromUrlRequest>
{
    public V1RecoverFromUrlRequestValidator()
    {
        RuleFor(x => x.NodeUrl)
            .NotEmpty()
            .Must(BeValidUrl);

        RuleFor(x => x.CollectionName)
            .NotEmpty();

        RuleFor(x => x.SnapshotUrl)
            .NotEmpty()
            .Must(BeValidUrl);
    }

    private bool BeValidUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out _);
    }
}

