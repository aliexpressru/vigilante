using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1AbortShardTransferRequestValidator : AbstractValidator<V1AbortShardTransferRequest>
{
    public V1AbortShardTransferRequestValidator()
    {
        RuleFor(x => x.CollectionName)
            .NotEmpty()
            .WithMessage("Collection name is required");

        RuleFor(x => x.SourcePeerId)
            .NotEmpty()
            .WithMessage("Source peer ID is required");

        RuleFor(x => x.TargetPeerId)
            .NotEmpty()
            .WithMessage("Target peer ID is required");

        RuleFor(x => x.ShardId)
            .NotEmpty()
            .WithMessage("Shard ID is required");
    }
}

