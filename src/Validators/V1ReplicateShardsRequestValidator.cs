using Aer.QdrantClient.Http.Models.Shared;
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
        
        RuleFor(x => x.ShardTransferMethod)
            .Must(method => string.IsNullOrEmpty(method) || Enum.TryParse<ShardTransferMethod>(method, true, out _))
            .WithMessage($"ShardTransferMethod must be one of: {string.Join(", ", Enum.GetNames<ShardTransferMethod>())}");
    }
}


