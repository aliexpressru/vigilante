using FluentValidation;
using Vigilante.Models.Enums;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1DeleteCollectionRequestValidator : AbstractValidator<V1DeleteCollectionRequest>
{
    public V1DeleteCollectionRequestValidator()
    {
        RuleFor(x => x.CollectionName)
            .NotEmpty();
        
        RuleFor(x => x.DeletionType)
            .IsInEnum();
        
        // When SingleNode = true and DeletionType = Api, NodeUrl is required
        When(x => x.SingleNode && x.DeletionType == CollectionDeletionType.Api, () =>
        {
            RuleFor(x => x.NodeUrl)
                .NotEmpty();
        });
        
        // When SingleNode = true and DeletionType = Disk, PodName and PodNamespace are required
        When(x => x.SingleNode && x.DeletionType == CollectionDeletionType.Disk, () =>
        {
            RuleFor(x => x.PodName)
                .NotEmpty();
            
            RuleFor(x => x.PodNamespace)
                .NotEmpty();
        });
    }
}

