using FluentValidation.TestHelper;
using NUnit.Framework;
using Vigilante.Models.Requests;
using Vigilante.Validators;
using FluentAssertions;

namespace Aer.Vigilante.Tests.Validators;

[TestFixture]
public class SnapshotsValidatorsTests
{
    #region V1RecoverRequestValidator Tests

    private V1RecoverRequestValidator _recoverValidator = null!;

    [SetUp]
    public void SetupRecoverValidator()
    {
        _recoverValidator = new V1RecoverRequestValidator();
    }

    [Test]
    public void RecoverValidator_SnapshotRecovery_InvalidSource_FailsValidation()
    {
        var request = new V1RecoverRequest
        {
            CollectionName = "test_collection",
            TargetNodeUrl = "http://node1:6333",
            SnapshotName = "snapshot.snapshot",
            Source = "InvalidSource",
            SnapshotUrl = null
        };

        var result = _recoverValidator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(r => r.Source);
    }

    [Test]
    public void RecoverValidator_SnapshotRecovery_ValidSource_PassesValidation()
    {
        var request = new V1RecoverRequest
        {
            CollectionName = "test_collection",
            TargetNodeUrl = "http://node1:6333",
            SnapshotName = "snapshot.snapshot",
            Source = "KubernetesStorage"
        };

        var result = _recoverValidator.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(r => r.Source);
    }

    [Test]
    public void RecoverValidator_UrlRecovery_WithoutSource_PassesValidation()
    {
        var request = new V1RecoverRequest
        {
            CollectionName = "test_collection",
            TargetNodeUrl = "http://node1:6333",
            SnapshotUrl = "https://s3.example.com/bucket/snapshot.snapshot"
        };

        var result = _recoverValidator.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(r => r.Source);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Test]
    public void RecoverValidator_SnapshotRecovery_WithoutSnapshotName_FailsValidation()
    {
        var request = new V1RecoverRequest
        {
            CollectionName = "test_collection",
            TargetNodeUrl = "http://node1:6333",
            Source = "KubernetesStorage"
        };

        var result = _recoverValidator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(r => r.SnapshotName);
    }

    [Test]
    public void RecoverValidator_SnapshotRecovery_WithoutSource_FailsValidation()
    {
        var request = new V1RecoverRequest
        {
            CollectionName = "test_collection",
            TargetNodeUrl = "http://node1:6333",
            SnapshotName = "snapshot.snapshot"
        };

        var result = _recoverValidator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(r => r.Source);
    }

    #endregion

    #region V1MultiRecoverRequestValidator Tests

    private V1MultiRecoverRequestValidator _multiRecoverValidator = null!;

    [SetUp]
    public void SetupMultiRecoverValidator()
    {
        _multiRecoverValidator = new V1MultiRecoverRequestValidator();
    }

    [Test]
    public void MultiRecoverValidator_EmptyTargetCollectionName_FailsValidation()
    {
        var request = new V1MultiRecoverRequest
        {
            TargetCollectionName = "",
            Items = [new V1MultiRecoverItem { TargetNodeUrl = "http://node1:6333", SnapshotName = "snap.snapshot", SnapshotSource = "KubernetesStorage" }]
        };

        var result = _multiRecoverValidator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(r => r.TargetCollectionName);
    }

    [Test]
    public void MultiRecoverValidator_EmptyItemsList_FailsValidation()
    {
        var request = new V1MultiRecoverRequest
        {
            TargetCollectionName = "my-collection",
            Items = []
        };

        var result = _multiRecoverValidator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(r => r.Items);
    }

    [Test]
    public void MultiRecoverValidator_ItemWithEmptyTargetNodeUrl_FailsValidation()
    {
        var request = new V1MultiRecoverRequest
        {
            TargetCollectionName = "my-collection",
            Items = [new V1MultiRecoverItem { TargetNodeUrl = "", SnapshotName = "snap.snapshot", SnapshotSource = "KubernetesStorage" }]
        };

        var result = _multiRecoverValidator.TestValidate(request);

        result.IsValid.Should().BeFalse();
    }

    [Test]
    public void MultiRecoverValidator_ItemWithEmptySnapshotName_FailsValidation()
    {
        var request = new V1MultiRecoverRequest
        {
            TargetCollectionName = "my-collection",
            Items = [new V1MultiRecoverItem { TargetNodeUrl = "http://node1:6333", SnapshotName = "", SnapshotSource = "KubernetesStorage" }]
        };

        var result = _multiRecoverValidator.TestValidate(request);

        result.IsValid.Should().BeFalse();
    }

    [Test]
    public void MultiRecoverValidator_ItemWithEmptySnapshotSource_FailsValidation()
    {
        var request = new V1MultiRecoverRequest
        {
            TargetCollectionName = "my-collection",
            Items = [new V1MultiRecoverItem { TargetNodeUrl = "http://node1:6333", SnapshotName = "snap.snapshot", SnapshotSource = "" }]
        };

        var result = _multiRecoverValidator.TestValidate(request);

        result.IsValid.Should().BeFalse();
    }

    [Test]
    public void MultiRecoverValidator_ValidRequest_PassesValidation()
    {
        var request = new V1MultiRecoverRequest
        {
            TargetCollectionName = "my-collection",
            SnapshotPriority = "Snapshot",
            Items =
            [
                new V1MultiRecoverItem { TargetNodeUrl = "http://node1:6333", SnapshotName = "snap.snapshot", SnapshotSource = "KubernetesStorage" },
                new V1MultiRecoverItem { TargetNodeUrl = "http://node2:6333", SnapshotName = "snap2.snapshot", SnapshotSource = "S3Storage" }
            ]
        };

        var result = _multiRecoverValidator.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    #endregion
}

