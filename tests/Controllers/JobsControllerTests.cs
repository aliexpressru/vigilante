using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using FluentAssertions;
using NUnit.Framework;
using Vigilante.Controllers;
using Vigilante.Models;
using Vigilante.Models.Requests;
using Vigilante.Services.Interfaces;
using Vigilante.Services.Jobs;

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
        _jobRegistry.ProcessPendingJobsAsync(Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>()).Returns(Task.CompletedTask);
        _jobRegistry.GetJobInfos().Returns([]);

        var result = await _controller.GetJobsStatus(CancellationToken.None);

        result.Result.Should().BeAssignableTo<OkObjectResult>();
        var ok = (OkObjectResult)result.Result!;
        var list = ok.Value as IReadOnlyList<JobInfoDto>;
        list.Should().NotBeNull();
        list!.Should().BeEmpty();
        await _jobRegistry.Received(1).ProcessPendingJobsAsync(
            Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlySet<string>?>(s => s != null && s.Contains(SnapshotAutomationJob.JobKey)));
    }

    [Test]
    public async Task GetJobsStatus_WhenJobsExist_Returns200WithList()
    {
        _jobRegistry.ProcessPendingJobsAsync(Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>()).Returns(Task.CompletedTask);
        var infos = new List<JobInfoDto>
        {
            new("job1", null, null, null),
            new("job2", "error", DateTime.UtcNow, new Dictionary<string, object?> { ["plan"] = "x" })
        };
        _jobRegistry.GetJobInfos().Returns(infos);

        var result = await _controller.GetJobsStatus(CancellationToken.None);

        result.Result.Should().BeAssignableTo<OkObjectResult>();
        var ok = (OkObjectResult)result.Result!;
        var list = ok.Value as IReadOnlyList<JobInfoDto>;
        list.Should().NotBeNull();
        list!.Count.Should().Be(2);
        list[0].Key.Should().Be("job1");
        list[1].Key.Should().Be("job2");
        list[1].ErrorMessage.Should().Be("error");
        await _jobRegistry.Received(1).ProcessPendingJobsAsync(
            Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlySet<string>?>(s => s != null && s.Contains(SnapshotAutomationJob.JobKey)));
    }

    [Test]
    public async Task GetJobsStatus_WhenRegistryThrows_Returns500()
    {
        _jobRegistry.ProcessPendingJobsAsync(Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>()).Returns(Task.CompletedTask);
        _jobRegistry.GetJobInfos().Returns(_ => throw new InvalidOperationException("registry broken"));

        var result = await _controller.GetJobsStatus(CancellationToken.None);

        result.Result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result.Result!;
        objectResult.StatusCode.Should().Be(500);
    }

    [Test]
    public async Task GetJobsStatus_WhenSnapshotAutomationEnabled_IncludesSnapshotAutomationRow()
    {
        _jobRegistry.ProcessPendingJobsAsync(Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>()).Returns(Task.CompletedTask);
        _jobRegistry.GetJobInfos().Returns([]);
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
        list.Should().HaveCount(1);
        list[0].Key.Should().Be("snapshot-automation");
        list[0].Metadata!["phase"].Should().Be("idle");
    }

    [Test]
    public async Task CancelJob_WhenKeyMissing_ReturnsBadRequest()
    {
        var result = await _controller.CancelJob(new V1CancelJobRequest { Key = " " }, CancellationToken.None);

        result.Should().BeAssignableTo<BadRequestObjectResult>();
    }

    [Test]
    public async Task CancelJob_WhenJobExists_ReturnsOk()
    {
        _jobRegistry.CancelJobAsync("job1", Arg.Any<CancellationToken>()).Returns(true);

        var result = await _controller.CancelJob(new V1CancelJobRequest { Key = "job1" }, CancellationToken.None);

        result.Should().BeAssignableTo<OkObjectResult>();
        await _jobRegistry.Received(1).CancelJobAsync("job1", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CancelJob_WhenJobMissing_ReturnsNotFound()
    {
        _jobRegistry.CancelJobAsync("missing", Arg.Any<CancellationToken>()).Returns(false);

        var result = await _controller.CancelJob(new V1CancelJobRequest { Key = "missing" }, CancellationToken.None);

        result.Should().BeAssignableTo<NotFoundObjectResult>();
        await _jobRegistry.Received(1).CancelJobAsync("missing", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CancelJob_WhenRegistryThrows_Returns500()
    {
        _jobRegistry.CancelJobAsync("job1", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new InvalidOperationException("broken")));

        var result = await _controller.CancelJob(new V1CancelJobRequest { Key = "job1" }, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result;
        objectResult.StatusCode.Should().Be(500);
    }
}
