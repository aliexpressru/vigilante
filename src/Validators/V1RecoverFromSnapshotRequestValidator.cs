using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1RecoverFromSnapshotRequestValidator : AbstractValidator<V1RecoverFromSnapshotRequest>
{
    public V1RecoverFromSnapshotRequestValidator()
    {
        RuleFor(x => x.CollectionName)
            .NotEmpty();
        
        RuleFor(x => x.SnapshotName)
            .NotEmpty();
        
        RuleFor(x => x.TargetNodeUrl)
            .NotEmpty()
            .Must(BeAValidUrl);
        
        RuleFor(x => x.Source)
            .NotEmpty()
            .Must(BeAValidSource);
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

