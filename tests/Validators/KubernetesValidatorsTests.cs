using FluentValidation.TestHelper;
using NUnit.Framework;
using Vigilante.Models.Enums;
using Vigilante.Models.Requests;
using Vigilante.Validators;

namespace Aer.Vigilante.Tests.Validators;

[TestFixture]
public class V1DeletePodRequestValidatorTests
{
    private V1DeletePodRequestValidator _validator = null!;

    [SetUp]
    public void Setup()
    {
        _validator = new V1DeletePodRequestValidator();
    }

    [Test]
    public void Validate_WithValidPodName_ShouldNotHaveErrors()
    {
        // Arrange
        var request = new V1DeletePodRequest
        {
            PodName = "qdrant-0",
            Namespace = "qdrant"
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Test]
    public void Validate_WithEmptyPodName_ShouldHaveError()
    {
        // Arrange
        var request = new V1DeletePodRequest
        {
            PodName = "",
            Namespace = "qdrant"
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.PodName);
    }

    [Test]
    public void Validate_WithNullPodName_ShouldHaveError()
    {
        // Arrange
        var request = new V1DeletePodRequest
        {
            PodName = null!,
            Namespace = "qdrant"
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.PodName);
    }

    [Test]
    public void Validate_WithoutNamespace_ShouldNotHaveErrors()
    {
        // Arrange
        var request = new V1DeletePodRequest
        {
            PodName = "qdrant-0"
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }
}

[TestFixture]
public class V1ManageStatefulSetRequestValidatorTests
{
    private V1ManageStatefulSetRequestValidator _validator = null!;

    [SetUp]
    public void Setup()
    {
        _validator = new V1ManageStatefulSetRequestValidator();
    }

    #region Rollout Operation Tests

    [Test]
    public void Validate_RolloutOperation_WithValidRequest_ShouldNotHaveErrors()
    {
        // Arrange
        var request = new V1ManageStatefulSetRequest
        {
            StatefulSetName = "qdrant",
            Namespace = "qdrant",
            OperationType = StatefulSetOperationType.Rollout
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Test]
    public void Validate_RolloutOperation_WithoutNamespace_ShouldNotHaveErrors()
    {
        // Arrange
        var request = new V1ManageStatefulSetRequest
        {
            StatefulSetName = "qdrant",
            OperationType = StatefulSetOperationType.Rollout
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Test]
    public void Validate_WithEmptyStatefulSetName_ShouldHaveError()
    {
        // Arrange
        var request = new V1ManageStatefulSetRequest
        {
            StatefulSetName = "",
            OperationType = StatefulSetOperationType.Rollout
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.StatefulSetName);
    }

    #endregion

    #region Scale Operation Tests

    [Test]
    public void Validate_ScaleOperation_WithValidReplicas_ShouldNotHaveErrors()
    {
        // Arrange
        var request = new V1ManageStatefulSetRequest
        {
            StatefulSetName = "qdrant",
            Namespace = "qdrant",
            OperationType = StatefulSetOperationType.Scale,
            Replicas = 3
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Test]
    public void Validate_ScaleOperation_WithZeroReplicas_ShouldNotHaveErrors()
    {
        // Arrange
        var request = new V1ManageStatefulSetRequest
        {
            StatefulSetName = "qdrant",
            Namespace = "qdrant",
            OperationType = StatefulSetOperationType.Scale,
            Replicas = 0
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Test]
    public void Validate_ScaleOperation_WithoutReplicas_ShouldHaveError()
    {
        // Arrange
        var request = new V1ManageStatefulSetRequest
        {
            StatefulSetName = "qdrant",
            Namespace = "qdrant",
            OperationType = StatefulSetOperationType.Scale,
            Replicas = null
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Replicas);
    }

    [Test]
    public void Validate_ScaleOperation_WithNegativeReplicas_ShouldHaveError()
    {
        // Arrange
        var request = new V1ManageStatefulSetRequest
        {
            StatefulSetName = "qdrant",
            Namespace = "qdrant",
            OperationType = StatefulSetOperationType.Scale,
            Replicas = -1
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Replicas);
    }

    #endregion
}

