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
    private IDynamicConfigService _dynamicConfig = null!;
    private ISnapshotAutomationStatus _snapshotStatus = null!;
    private ILogger<JobsController> _logger = null!;
    private JobsController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _jobRegistry = Substitute.For<IJobRegistry>();
        _dynamicConfig = Substitute.For<IDynamicConfigService>();
        _dynamicConfig.GetConfigAsync(Arg.Any<CancellationToken>()).Returns(new DynamicConfig());
        _snapshotStatus = Substitute.For<ISnapshotAutomationStatus>();
        _snapshotStatus.GetDisplayMetadata().Returns(new Dictionary<string, object?> { ["phase"] = "idle" });
        _logger = Substitute.For<ILogger<JobsController>>();
        _controller = new JobsController(_jobRegistry, _dynamicConfig, _snapshotStatus, _logger);
    }

    [Test]
    public async Task GetJobsStatus_WhenNoJobs_Returns200EmptyList()
    {
        _jobRegistry.ProcessPendingJobsAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _jobRegistry.GetJobInfos().Returns(Array.Empty<JobInfoDto>());

        var result = await _controller.GetJobsStatus(CancellationToken.None);

        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var ok = (OkObjectResult)result.Result!;
        var list = ok.Value as IReadOnlyList<JobInfoDto>;
        Assert.That(list, Is.Not.Null);
        Assert.That(list!, Is.Empty);
        await _jobRegistry.Received(1).ProcessPendingJobsAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetJobsStatus_WhenJobsExist_Returns200WithList()
    {
        _jobRegistry.ProcessPendingJobsAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var infos = new List<JobInfoDto>
        {
            new("job1", null, null, null),
            new("job2", "error", DateTime.UtcNow, new Dictionary<string, object?> { ["plan"] = "x" })
        };
        _jobRegistry.GetJobInfos().Returns(infos);

        var result = await _controller.GetJobsStatus(CancellationToken.None);

        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var ok = (OkObjectResult)result.Result!;
        var list = ok.Value as IReadOnlyList<JobInfoDto>;
        Assert.That(list, Is.Not.Null);
        Assert.That(list!.Count, Is.EqualTo(2));
        Assert.That(list[0].Key, Is.EqualTo("job1"));
        Assert.That(list[1].Key, Is.EqualTo("job2"));
        Assert.That(list[1].ErrorMessage, Is.EqualTo("error"));
        await _jobRegistry.Received(1).ProcessPendingJobsAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetJobsStatus_WhenRegistryThrows_Returns500()
    {
        _jobRegistry.ProcessPendingJobsAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _jobRegistry.GetJobInfos().Returns(_ => throw new InvalidOperationException("registry broken"));

        var result = await _controller.GetJobsStatus(CancellationToken.None);

        Assert.That(result.Result, Is.InstanceOf<ObjectResult>());
        var objectResult = (ObjectResult)result.Result!;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
    }

    [Test]
    public async Task GetJobsStatus_WhenSnapshotAutomationEnabled_IncludesSnapshotAutomationRow()
    {
        _jobRegistry.ProcessPendingJobsAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _jobRegistry.GetJobInfos().Returns(Array.Empty<JobInfoDto>());
        _dynamicConfig.GetConfigAsync(Arg.Any<CancellationToken>()).Returns(new DynamicConfig
        {
            Snapshot = new SnapshotConfiguration { Schedule = new Schedule { Enabled = true } }
        });
        _snapshotStatus.GetDisplayMetadata().Returns(new Dictionary<string, object?>
        {
            ["phase"] = "idle",
            ["lastCompletedUtc"] = DateTime.UtcNow,
            ["lastRunSuccess"] = true
        });

        var result = await _controller.GetJobsStatus(CancellationToken.None);

        var ok = (OkObjectResult)result.Result!;
        var list = (IReadOnlyList<JobInfoDto>)ok.Value!;
        Assert.That(list, Has.Count.EqualTo(1));
        Assert.That(list[0].Key, Is.EqualTo("snapshot-automation"));
        Assert.That(list[0].Metadata!["phase"], Is.EqualTo("idle"));
    }
}
