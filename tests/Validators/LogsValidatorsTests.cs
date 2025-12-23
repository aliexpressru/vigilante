using FluentValidation.TestHelper;
using NUnit.Framework;
using Vigilante.Models.Requests;
using Vigilante.Validators;

namespace Aer.Vigilante.Tests.Validators;

[TestFixture]
public class LogsValidatorsTests
{
    private V1GetQdrantLogsRequestValidator _qdrantValidator = null!;
    private V1GetVigilanteLogsRequestValidator _vigilanteValidator = null!;

    [SetUp]
    public void SetUp()
    {
        _qdrantValidator = new V1GetQdrantLogsRequestValidator();
        _vigilanteValidator = new V1GetVigilanteLogsRequestValidator();
    }

    [Test]
    public void Qdrant_Should_fail_when_pod_name_missing()
    {
        var request = new V1GetQdrantLogsRequest { PodName = "  " };

        var result = _qdrantValidator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(r => r.PodName)
            .WithErrorMessage("Pod name is required");
    }

    [Test]
    public void Qdrant_Should_fail_when_limit_not_positive()
    {
        var request = new V1GetQdrantLogsRequest { PodName = "pod", Limit = 0 };

        var result = _qdrantValidator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(r => r.Limit);
    }

    [Test]
    public void Qdrant_Should_fail_when_limit_exceeds_upper_bound()
    {
        var request = new V1GetQdrantLogsRequest { PodName = "pod", Limit = 1001 };

        var result = _qdrantValidator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(r => r.Limit);
    }

    [Test]
    public void Qdrant_Should_pass_for_valid_request()
    {
        var request = new V1GetQdrantLogsRequest
        {
            PodName = "pod",
            Namespace = "ns",
            Limit = 200,
            Continuation = "tok"
        };

        var result = _qdrantValidator.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Test]
    public void Vigilante_Should_fail_when_limit_not_positive()
    {
        var request = new V1GetVigilanteLogsRequest { Limit = 0 };

        var result = _vigilanteValidator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(r => r.Limit);
    }

    [Test]
    public void Vigilante_Should_fail_when_limit_exceeds_upper_bound()
    {
        var request = new V1GetVigilanteLogsRequest { Limit = 1001 };

        var result = _vigilanteValidator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(r => r.Limit);
    }

    [Test]
    public void Vigilante_Should_pass_for_valid_request()
    {
        var request = new V1GetVigilanteLogsRequest
        {
            Namespace = "ns",
            Limit = 200,
            Continuation = "tok"
        };

        var result = _vigilanteValidator.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }
}
