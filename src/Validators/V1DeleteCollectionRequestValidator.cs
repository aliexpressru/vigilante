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
        
        // When SingleNode = true and DeletionType = Api, NodeUrl is required
        When(x => x.SingleNode && x.DeletionType == CollectionDeletionType.Api, () =>
        {
            RuleFor(x => x.NodeUrl)
                .NotEmpty()
                .WithMessage("NodeUrl is required when deleting from a single node via API");
        });
        
        // When SingleNode = true and DeletionType = Disk, PodName and PodNamespace are required
        When(x => x.SingleNode && x.DeletionType == CollectionDeletionType.Disk, () =>
        {
            RuleFor(x => x.PodName)
                .NotEmpty()
                .WithMessage("PodName is required when deleting from a single node disk");
            
            RuleFor(x => x.PodNamespace)
                .NotEmpty()
                .WithMessage("PodNamespace is required when deleting from a single node disk");
        });
    }
}

