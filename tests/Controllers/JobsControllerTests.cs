using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Vigilante.Controllers;
using Vigilante.Models;
using Vigilante.Services.Interfaces;

namespace Aer.Vigilante.Tests.Controllers;

[TestFixture]
public class JobsControllerTests
{
    private IJobRegistry _jobRegistry = null!;
    private ILogger<JobsController> _logger = null!;
    private JobsController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _jobRegistry = Substitute.For<IJobRegistry>();
        _logger = Substitute.For<ILogger<JobsController>>();
        _controller = new JobsController(_jobRegistry, _logger);
    }

    [Test]
    public async Task GetJobsStatus_WhenNoJobs_Returns200EmptyList()
    {
        _jobRegistry.GetJobInfos().Returns(Array.Empty<JobInfoDto>());

        var result = await Task.FromResult(_controller.GetJobsStatus());

        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var ok = (OkObjectResult)result.Result!;
        var list = ok.Value as IReadOnlyList<JobInfoDto>;
        Assert.That(list, Is.Not.Null);
        Assert.That(list!, Is.Empty);
    }

    [Test]
    public async Task GetJobsStatus_WhenJobsExist_Returns200WithList()
    {
        var infos = new List<JobInfoDto>
        {
            new("job1", null, null, null),
            new("job2", "error", DateTime.UtcNow, new Dictionary<string, object?> { ["plan"] = "x" })
        };
        _jobRegistry.GetJobInfos().Returns(infos);

        var result = await Task.FromResult(_controller.GetJobsStatus());

        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var ok = (OkObjectResult)result.Result!;
        var list = ok.Value as IReadOnlyList<JobInfoDto>;
        Assert.That(list, Is.Not.Null);
        Assert.That(list!.Count, Is.EqualTo(2));
        Assert.That(list[0].Key, Is.EqualTo("job1"));
        Assert.That(list[1].Key, Is.EqualTo("job2"));
        Assert.That(list[1].ErrorMessage, Is.EqualTo("error"));
    }

    [Test]
    public void GetJobsStatus_WhenRegistryThrows_Returns500()
    {
        _jobRegistry.GetJobInfos().Returns(_ => throw new InvalidOperationException("registry broken"));

        var result = _controller.GetJobsStatus();

        Assert.That(result.Result, Is.InstanceOf<ObjectResult>());
        var objectResult = (ObjectResult)result.Result!;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
    }
}
