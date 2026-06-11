using Aer.QdrantClient.Http.Models.Shared;
using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1RecoverRequestValidator : AbstractValidator<V1RecoverRequest>
{
    public V1RecoverRequestValidator()
    {
        RuleFor(x => x.CollectionName)
            .NotEmpty();

        RuleFor(x => x.TargetNodeUrl)
            .NotEmpty()
            .Must(BeValidUrl);

        RuleFor(x => x.SnapshotPriority)
            .IsEnumName(typeof(SnapshotPriority), caseSensitive: false)
            .When(x => !string.IsNullOrWhiteSpace(x.SnapshotPriority))
            .WithMessage($"SnapshotPriority must be one of: {string.Join(", ", Enum.GetNames<SnapshotPriority>())}");

        RuleFor(x => x.SnapshotUrl)
            .NotEmpty()
            .Must(BeValidUrl)
            .When(x => IsUrlRecovery(x));

        RuleFor(x => x.Source)
            .NotEmpty()
            .Must(BeValidSource)
            .When(x => !IsUrlRecovery(x));

        RuleFor(x => x.SnapshotName)
            .NotEmpty()
            .When(x => !IsUrlRecovery(x));
    }

    private static bool IsUrlRecovery(V1RecoverRequest request) => !string.IsNullOrWhiteSpace(request.SnapshotUrl);

    private static bool BeValidUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out _);

    private static bool BeValidSource(string? source) =>
        source is "KubernetesStorage" or "QdrantApi" or "S3Storage";
}
