using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1RecoverFromSnapshotRequestValidator : AbstractValidator<V1RecoverFromSnapshotRequest>
{
    public V1RecoverFromSnapshotRequestValidator()
    {
        RuleFor(x => x.CollectionName)
            .NotEmpty()
            .WithMessage("Collection name is required");
        
        RuleFor(x => x.SnapshotName)
            .NotEmpty()
            .WithMessage("Snapshot name is required");
        
        RuleFor(x => x.TargetNodeUrl)
            .NotEmpty()
            .WithMessage("Target node URL is required")
            .Must(BeAValidUrl)
            .WithMessage("Target node URL must be a valid URL");
        
        RuleFor(x => x.Source)
            .NotEmpty()
            .WithMessage("Source is required")
            .Must(BeAValidSource)
            .WithMessage("Source must be 'KubernetesStorage', 'QdrantApi', or 'S3Storage'");
    }
    
    private bool BeAValidUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out _);
    }
    
    private bool BeAValidSource(string source)
    {
        return source == "KubernetesStorage" || source == "QdrantApi" || source == "S3Storage";
    }
}

