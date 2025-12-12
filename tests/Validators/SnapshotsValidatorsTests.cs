using FluentValidation.TestHelper;
using NUnit.Framework;
using Vigilante.Models.Requests;
using Vigilante.Validators;

namespace Aer.Vigilante.Tests.Validators;

[TestFixture]
public class SnapshotsValidatorsTests
{
    #region V1RecoverFromSnapshotRequestValidator Tests

    private V1RecoverFromSnapshotRequestValidator _recoverFromSnapshotValidator = null!;

    [SetUp]
    public void Setup()
    {
        _recoverFromSnapshotValidator = new V1RecoverFromSnapshotRequestValidator();
    }

    [Test]
    public void RecoverFromSnapshotValidator_ValidRequest_PassesValidation()
    {
        // Arrange
        var request = new V1RecoverFromSnapshotRequest
        {
            CollectionName = "test_collection",
            SnapshotName = "snapshot.snapshot",
            TargetNodeUrl = "http://node1:6333",
            Source = "KubernetesStorage"
        };

        // Act
        var result = _recoverFromSnapshotValidator.TestValidate(request);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Test]
    public void RecoverFromSnapshotValidator_ValidRequest_WithSourceCollectionName_PassesValidation()
    {
        // Arrange
        var request = new V1RecoverFromSnapshotRequest
        {
            CollectionName = "new_collection",
            SnapshotName = "snapshot.snapshot",
            TargetNodeUrl = "http://node1:6333",
            Source = "S3Storage",
            SourceCollectionName = "original_collection"
        };

        // Act
        var result = _recoverFromSnapshotValidator.TestValidate(request);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Test]
    public void RecoverFromSnapshotValidator_EmptyCollectionName_FailsValidation()
    {
        // Arrange
        var request = new V1RecoverFromSnapshotRequest
        {
            CollectionName = "",
            SnapshotName = "snapshot.snapshot",
            TargetNodeUrl = "http://node1:6333",
            Source = "KubernetesStorage"
        };

        // Act
        var result = _recoverFromSnapshotValidator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(r => r.CollectionName);
    }

    [Test]
    public void RecoverFromSnapshotValidator_EmptySnapshotName_FailsValidation()
    {
        // Arrange
        var request = new V1RecoverFromSnapshotRequest
        {
            CollectionName = "test_collection",
            SnapshotName = "",
            TargetNodeUrl = "http://node1:6333",
            Source = "KubernetesStorage"
        };

        // Act
        var result = _recoverFromSnapshotValidator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(r => r.SnapshotName);
    }

    [Test]
    public void RecoverFromSnapshotValidator_EmptyTargetNodeUrl_FailsValidation()
    {
        // Arrange
        var request = new V1RecoverFromSnapshotRequest
        {
            CollectionName = "test_collection",
            SnapshotName = "snapshot.snapshot",
            TargetNodeUrl = "",
            Source = "KubernetesStorage"
        };

        // Act
        var result = _recoverFromSnapshotValidator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(r => r.TargetNodeUrl);
    }

    [Test]
    public void RecoverFromSnapshotValidator_InvalidTargetNodeUrl_FailsValidation()
    {
        // Arrange
        var request = new V1RecoverFromSnapshotRequest
        {
            CollectionName = "test_collection",
            SnapshotName = "snapshot.snapshot",
            TargetNodeUrl = "not-a-valid-url",
            Source = "KubernetesStorage"
        };

        // Act
        var result = _recoverFromSnapshotValidator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(r => r.TargetNodeUrl);
    }

    [Test]
    public void RecoverFromSnapshotValidator_EmptySource_FailsValidation()
    {
        // Arrange
        var request = new V1RecoverFromSnapshotRequest
        {
            CollectionName = "test_collection",
            SnapshotName = "snapshot.snapshot",
            TargetNodeUrl = "http://node1:6333",
            Source = ""
        };

        // Act
        var result = _recoverFromSnapshotValidator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(r => r.Source);
    }

    [Test]
    public void RecoverFromSnapshotValidator_InvalidSource_FailsValidation()
    {
        // Arrange
        var request = new V1RecoverFromSnapshotRequest
        {
            CollectionName = "test_collection",
            SnapshotName = "snapshot.snapshot",
            TargetNodeUrl = "http://node1:6333",
            Source = "InvalidSource"
        };

        // Act
        var result = _recoverFromSnapshotValidator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(r => r.Source);
    }

    [TestCase("KubernetesStorage")]
    [TestCase("QdrantApi")]
    [TestCase("S3Storage")]
    public void RecoverFromSnapshotValidator_ValidSources_PassValidation(string source)
    {
        // Arrange
        var request = new V1RecoverFromSnapshotRequest
        {
            CollectionName = "test_collection",
            SnapshotName = "snapshot.snapshot",
            TargetNodeUrl = "http://node1:6333",
            Source = source
        };

        // Act
        var result = _recoverFromSnapshotValidator.TestValidate(request);

        // Assert
        result.ShouldNotHaveValidationErrorFor(r => r.Source);
    }

    #endregion

    #region V1RecoverFromUrlRequestValidator Tests

    private V1RecoverFromUrlRequestValidator _recoverFromUrlValidator = null!;

    [SetUp]
    public void SetupRecoverFromUrlValidator()
    {
        _recoverFromUrlValidator = new V1RecoverFromUrlRequestValidator();
    }

    [Test]
    public void RecoverFromUrlValidator_ValidRequest_PassesValidation()
    {
        // Arrange
        var request = new V1RecoverFromUrlRequest
        {
            NodeUrl = "http://node1:6333",
            CollectionName = "test_collection",
            SnapshotUrl = "https://s3.example.com/bucket/snapshot.snapshot"
        };

        // Act
        var result = _recoverFromUrlValidator.TestValidate(request);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Test]
    public void RecoverFromUrlValidator_EmptyNodeUrl_FailsValidation()
    {
        // Arrange
        var request = new V1RecoverFromUrlRequest
        {
            NodeUrl = "",
            CollectionName = "test_collection",
            SnapshotUrl = "https://s3.example.com/bucket/snapshot.snapshot"
        };

        // Act
        var result = _recoverFromUrlValidator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(r => r.NodeUrl);
    }

    [Test]
    public void RecoverFromUrlValidator_InvalidNodeUrl_FailsValidation()
    {
        // Arrange
        var request = new V1RecoverFromUrlRequest
        {
            NodeUrl = "not-a-valid-url",
            CollectionName = "test_collection",
            SnapshotUrl = "https://s3.example.com/bucket/snapshot.snapshot"
        };

        // Act
        var result = _recoverFromUrlValidator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(r => r.NodeUrl);
    }

    [Test]
    public void RecoverFromUrlValidator_EmptyCollectionName_FailsValidation()
    {
        // Arrange
        var request = new V1RecoverFromUrlRequest
        {
            NodeUrl = "http://node1:6333",
            CollectionName = "",
            SnapshotUrl = "https://s3.example.com/bucket/snapshot.snapshot"
        };

        // Act
        var result = _recoverFromUrlValidator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(r => r.CollectionName);
    }

    [Test]
    public void RecoverFromUrlValidator_EmptySnapshotUrl_FailsValidation()
    {
        // Arrange
        var request = new V1RecoverFromUrlRequest
        {
            NodeUrl = "http://node1:6333",
            CollectionName = "test_collection",
            SnapshotUrl = ""
        };

        // Act
        var result = _recoverFromUrlValidator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(r => r.SnapshotUrl);
    }

    [Test]
    public void RecoverFromUrlValidator_InvalidSnapshotUrl_FailsValidation()
    {
        // Arrange
        var request = new V1RecoverFromUrlRequest
        {
            NodeUrl = "http://node1:6333",
            CollectionName = "test_collection",
            SnapshotUrl = "not-a-valid-url"
        };

        // Act
        var result = _recoverFromUrlValidator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(r => r.SnapshotUrl);
    }

    #endregion
}

