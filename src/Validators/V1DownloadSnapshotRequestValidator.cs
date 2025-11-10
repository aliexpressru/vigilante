using FluentValidation;
using Vigilante.Models.Enums;
using Vigilante.Models.Requests;

namespace Vigilante.Validators;

public class V1DownloadSnapshotRequestValidator : AbstractValidator<V1DownloadSnapshotRequest>
{
    public V1DownloadSnapshotRequestValidator()
    {
        RuleFor(x => x.CollectionName)
            .NotEmpty()
            .WithMessage("Collection name is required");
        
        RuleFor(x => x.SnapshotName)
            .NotEmpty()
            .WithMessage("Snapshot name is required");
        
        RuleFor(x => x.DownloadType)
            .IsInEnum()
            .WithMessage("Download type must be either Api or Disk");
        
        // Validate NodeUrl when DownloadType is Api
        When(x => x.DownloadType == SnapshotDownloadType.Api, () =>
        {
            RuleFor(x => x.NodeUrl)
                .NotEmpty()
                .WithMessage("Node URL is required when download type is Api")
                .Must(uri => Uri.TryCreate(uri, UriKind.Absolute, out _))
                .WithMessage("Node URL must be a valid URL");
        });
        
        // Validate PodName and PodNamespace when DownloadType is Disk
        When(x => x.DownloadType == SnapshotDownloadType.Disk, () =>
        {
            RuleFor(x => x.PodName)
                .NotEmpty()
                .WithMessage("Pod name is required when download type is Disk");
            
            RuleFor(x => x.PodNamespace)
                .NotEmpty()
                .WithMessage("Pod namespace is required when download type is Disk");
        });
    }
}

