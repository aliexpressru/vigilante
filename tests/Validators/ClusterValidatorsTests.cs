using FluentValidation.TestHelper;
using NUnit.Framework;
using Vigilante.Models.Requests;
using Vigilante.Validators;

namespace Aer.Vigilante.Tests.Validators;

[TestFixture]
public class V1DropShardsFromPeerRequestValidatorTests
{
    private V1DropShardsFromPeerRequestValidator _validator = null!;

    [SetUp]
    public void Setup()
    {
        _validator = new V1DropShardsFromPeerRequestValidator();
    }

    [Test]
    public void Validate_WithValidRequest_ShouldNotHaveErrors()
    {
        // Arrange
        var request = new V1DropShardsFromPeerRequest
        {
            CollectionName = "test_collection",
            PeerId = 123456789,
            ShardIds = new uint[] { 1, 2, 3 },
            IsDryRun = false
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Test]
    public void Validate_WithDryRun_ShouldNotHaveErrors()
    {
        // Arrange
        var request = new V1DropShardsFromPeerRequest
        {
            CollectionName = "test_collection",
            PeerId = 123456789,
            ShardIds = new uint[] { 1 },
            IsDryRun = true
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Test]
    public void Validate_WithEmptyCollectionName_ShouldHaveError()
    {
        // Arrange
        var request = new V1DropShardsFromPeerRequest
        {
            CollectionName = "",
            PeerId = 123456789,
            ShardIds = new uint[] { 1, 2 }
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.CollectionName)
            .WithErrorMessage("Collection name is required");
    }

    [Test]
    public void Validate_WithNullCollectionName_ShouldHaveError()
    {
        // Arrange
        var request = new V1DropShardsFromPeerRequest
        {
            CollectionName = null!,
            PeerId = 123456789,
            ShardIds = new uint[] { 1, 2 }
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.CollectionName);
    }

    [Test]
    public void Validate_WithNullPeerId_ShouldHaveError()
    {
        // Arrange
        var request = new V1DropShardsFromPeerRequest
        {
            CollectionName = "test_collection",
            PeerId = null,
            ShardIds = new uint[] { 1, 2 }
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.PeerId)
            .WithErrorMessage("Peer ID is required");
    }

    [Test]
    public void Validate_WithEmptyShardIds_ShouldHaveError()
    {
        // Arrange
        var request = new V1DropShardsFromPeerRequest
        {
            CollectionName = "test_collection",
            PeerId = 123456789,
            ShardIds = Array.Empty<uint>()
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.ShardIds)
            .WithErrorMessage("At least one shard ID must be specified");
    }

    [Test]
    public void Validate_WithNullShardIds_ShouldHaveError()
    {
        // Arrange
        var request = new V1DropShardsFromPeerRequest
        {
            CollectionName = "test_collection",
            PeerId = 123456789,
            ShardIds = null!
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.ShardIds);
    }

    [Test]
    public void Validate_WithMultipleErrors_ShouldHaveAllErrors()
    {
        // Arrange
        var request = new V1DropShardsFromPeerRequest
        {
            CollectionName = "",
            PeerId = null,
            ShardIds = Array.Empty<uint>()
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.CollectionName);
        result.ShouldHaveValidationErrorFor(x => x.PeerId);
        result.ShouldHaveValidationErrorFor(x => x.ShardIds);
    }

    [Test]
    public void Validate_WithSingleShardId_ShouldNotHaveErrors()
    {
        // Arrange
        var request = new V1DropShardsFromPeerRequest
        {
            CollectionName = "test_collection",
            PeerId = 987654321,
            ShardIds = new uint[] { 5 }
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Test]
    public void Validate_WithLargeNumberOfShardIds_ShouldNotHaveErrors()
    {
        // Arrange
        var request = new V1DropShardsFromPeerRequest
        {
            CollectionName = "test_collection",
            PeerId = 111111111,
            ShardIds = Enumerable.Range(0, 100).Select(x => (uint)x).ToArray()
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }
}

[TestFixture]
public class V1ReplicateShardsRequestValidatorTests
{
    private V1ReplicateShardsRequestValidator _validator = null!;

    [SetUp]
    public void Setup()
    {
        _validator = new V1ReplicateShardsRequestValidator();
    }

    [Test]
    public void Validate_WithValidRequest_ShouldNotHaveErrors()
    {
        // Arrange
        var request = new V1ReplicateShardsRequest
        {
            SourcePeerId = 123456789,
            TargetPeerId = 987654321,
            CollectionName = "test_collection",
            ShardIdsToReplicate = new uint[] { 1, 2, 3 },
            IsMoveShards = false
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Test]
    public void Validate_WithEmptySourcePeerId_ShouldHaveError()
    {
        // Arrange
        var request = new V1ReplicateShardsRequest
        {
            SourcePeerId = null,
            TargetPeerId = 987654321,
            CollectionName = "test_collection",
            ShardIdsToReplicate = new uint[] { 1, 2, 3 }
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.SourcePeerId);
    }

    [Test]
    public void Validate_WithEmptyTargetPeerId_ShouldHaveError()
    {
        // Arrange
        var request = new V1ReplicateShardsRequest
        {
            SourcePeerId = 123456789,
            TargetPeerId = null,
            CollectionName = "test_collection",
            ShardIdsToReplicate = new uint[] { 1, 2, 3 }
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.TargetPeerId);
    }

    [Test]
    public void Validate_WithSamePeerIds_ShouldHaveError()
    {
        // Arrange
        var request = new V1ReplicateShardsRequest
        {
            SourcePeerId = 123456789,
            TargetPeerId = 123456789,
            CollectionName = "test_collection",
            ShardIdsToReplicate = new uint[] { 1, 2, 3 }
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x)
            .WithErrorMessage("Source and Target PeerIds must be different");
    }

    [Test]
    public void Validate_WithEmptyCollectionName_ShouldHaveError()
    {
        // Arrange
        var request = new V1ReplicateShardsRequest
        {
            SourcePeerId = 123456789,
            TargetPeerId = 987654321,
            CollectionName = "",
            ShardIdsToReplicate = new uint[] { 1, 2, 3 }
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.CollectionName);
    }

    [Test]
    public void Validate_WithEmptyShardIds_ShouldHaveError()
    {
        // Arrange
        var request = new V1ReplicateShardsRequest
        {
            SourcePeerId = 123456789,
            TargetPeerId = 987654321,
            CollectionName = "test_collection",
            ShardIdsToReplicate = Array.Empty<uint>()
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.ShardIdsToReplicate);
    }

    [Test]
    public void Validate_WithValidShardTransferMethod_ShouldNotHaveErrors()
    {
        // Arrange
        var request = new V1ReplicateShardsRequest
        {
            SourcePeerId = 123456789,
            TargetPeerId = 987654321,
            CollectionName = "test_collection",
            ShardIdsToReplicate = new uint[] { 1, 2, 3 },
            ShardTransferMethod = "Snapshot"
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Test]
    public void Validate_WithInvalidShardTransferMethod_ShouldHaveError()
    {
        // Arrange
        var request = new V1ReplicateShardsRequest
        {
            SourcePeerId = 123456789,
            TargetPeerId = 987654321,
            CollectionName = "test_collection",
            ShardIdsToReplicate = new uint[] { 1, 2, 3 },
            ShardTransferMethod = "InvalidMethod"
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.ShardTransferMethod);
    }
}
