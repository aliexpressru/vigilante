using FluentValidation;
using Vigilante.Models.Enums;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1DeleteCollectionRequestValidator : AbstractValidator<V1DeleteCollectionRequest>
{
    public V1DeleteCollectionRequestValidator()
    {
        RuleFor(x => x.CollectionName)
            .NotEmpty()
            .WithMessage("Collection name is required");
        
        RuleFor(x => x.DeletionType)
            .IsInEnum()
            .WithMessage("Invalid deletion type");
        
        // When DeletionType = Api, NodeUrls list is required and must not be empty
        When(x => x.DeletionType == CollectionDeletionType.Api, () =>
        {
            RuleFor(x => x.NodeUrls)
                .NotNull()
                .WithMessage("NodeUrls list is required for API deletion")
                .Must(list => list != null && list.Count > 0)
                .WithMessage("NodeUrls list must contain at least one node URL");
            
            RuleForEach(x => x.NodeUrls)
                .NotEmpty()
                .WithMessage("Node URL cannot be empty");
        });
        
        // When DeletionType = Disk, Pods list is required and must not be empty
        When(x => x.DeletionType == CollectionDeletionType.Disk, () =>
        {
            RuleFor(x => x.Pods)
                .NotNull()
                .WithMessage("Pods list is required for disk deletion")
                .Must(list => list != null && list.Count > 0)
                .WithMessage("Pods list must contain at least one pod");
            
            RuleForEach(x => x.Pods)
                .ChildRules(pod =>
                {
                    pod.RuleFor(p => p.PodName)
                        .NotEmpty()
                        .WithMessage("Pod name is required");
                    
                    pod.RuleFor(p => p.PodNamespace)
                        .NotEmpty()
                        .WithMessage("Pod namespace is required");
                });
        });
    }
}
