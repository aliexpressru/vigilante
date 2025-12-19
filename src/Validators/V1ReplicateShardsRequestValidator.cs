using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1ReplicateShardsRequestValidator : AbstractValidator<V1ReplicateShardsRequest>
{
    public V1ReplicateShardsRequestValidator()
    {
        RuleFor(x => x.SourcePeerId)
            .NotEmpty();

        RuleFor(x => x.TargetPeerId)
            .NotEmpty();

        RuleFor(x => x)
            .Must(x => x.SourcePeerId != x.TargetPeerId)
            .WithMessage("Source and Target PeerIds must be different");

        RuleFor(x => x.CollectionName)
            .NotEmpty();

        RuleFor(x => x.ShardIdsToReplicate)
            .NotEmpty();
    }
}
