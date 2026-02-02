using FluentValidation;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1DropShardsFromPeerRequestValidator : AbstractValidator<V1DropShardsFromPeerRequest>
{
    public V1DropShardsFromPeerRequestValidator()
    {
        RuleFor(x => x.CollectionName)
            .NotEmpty()
            .WithMessage("Collection name is required");

        RuleFor(x => x.PeerId)
            .NotEmpty()
            .WithMessage("Peer ID is required");

        RuleFor(x => x.ShardIds)
            .NotEmpty()
            .WithMessage("At least one shard ID must be specified");
    }
}
