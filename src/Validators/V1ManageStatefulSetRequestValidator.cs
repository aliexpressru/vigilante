using FluentValidation;
using Vigilante.Models.Enums;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1ManageStatefulSetRequestValidator : AbstractValidator<V1ManageStatefulSetRequest>
{
    public V1ManageStatefulSetRequestValidator()
    {
        RuleFor(x => x.StatefulSetName)
            .NotEmpty();
            
        RuleFor(x => x.OperationType)
            .IsInEnum();
            
        RuleFor(x => x.Replicas)
            .NotNull()
            .GreaterThanOrEqualTo(0)
            .When(x => x.OperationType == StatefulSetOperationType.Scale);
    }
}

