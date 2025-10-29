using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1ReplicateShardsRequestValidator : AbstractValidator<V1ReplicateShardsRequest>
{
    public V1ReplicateShardsRequestValidator()
    {
        RuleFor(x => x.SourcePeerId)
            .NotEmpty()
            .WithMessage("Source PeerId is required");

        RuleFor(x => x.TargetPeerId)
            .NotEmpty()
            .WithMessage("Target PeerId is required");

        RuleFor(x => x)
            .Must(x => x.SourcePeerId != x.TargetPeerId)
            .WithMessage("Source and Target PeerIds must be different");

        RuleFor(x => x.CollectionName)
            .NotEmpty()
            .WithMessage("Collection Name is required");

        RuleFor(x => x.ShardIdsToReplicate)
            .NotEmpty()
            .WithMessage("At least one shard ID must be selected for replication");
    }
}
