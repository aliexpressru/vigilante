using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Vigilante.Controllers;
using Vigilante.Models.Requests;
using Vigilante.Models.Responses;
using Vigilante.Services.Interfaces;
using Vigilante.Services.Models;

namespace Aer.Vigilante.Tests.Controllers;

[TestFixture]
public class LogsControllerTests
{
    [Test]
    public async Task GetQdrantLogs_ValidRequest_MapsResponse()
    {
        var logReader = Substitute.For<ILogReader>();
        var logger = Substitute.For<ILogger<LogsController>>();
        var controller = new LogsController(logReader, logger);
        var ts1 = DateTime.UtcNow.AddSeconds(-1);
        var ts2 = DateTime.UtcNow;
        var request = new V1GetQdrantLogsRequest { PodName = "pod-1", Limit = 2, Continuation = "tok" };
        var page = new LogPage(true, null, new[]
        {
            new LogEntry(ts1, "msg1", "pod-1"),
            new LogEntry(ts2, "msg2", "pod-1")
        }, "next", true);
        logReader.GetQdrantPodLogsAsync("pod-1", Arg.Any<LogQuery>(), Arg.Any<CancellationToken>()).Returns(page);

        var result = await controller.GetQdrantLogs(request, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var ok = (OkObjectResult)result;
        var response = ok.Value as V1LogsPageResponse;
        Assert.That(response, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response!.Success, Is.True);
            Assert.That(response.Logs.Count, Is.EqualTo(2));
            Assert.That(response.Logs[0].Message, Is.EqualTo("msg1"));
            Assert.That(response.Logs[0].Timestamp, Is.EqualTo(ts1).Within(TimeSpan.FromSeconds(1)));
            Assert.That(response.Continuation, Is.EqualTo("next"));
            Assert.That(response.Truncated, Is.True);
        });
    }

    [Test]
    public async Task GetQdrantLogs_UsesPodNameFromBody()
    {
        var logReader = Substitute.For<ILogReader>();
        var logger = Substitute.For<ILogger<LogsController>>();
        var controller = new LogsController(logReader, logger);
        var request = new V1GetQdrantLogsRequest { PodName = "pod-from-body", Limit = 1 };
        var page = new LogPage(true, null, Array.Empty<LogEntry>(), null, false);
        LogQuery? capturedQuery = null;
        logReader
            .GetQdrantPodLogsAsync("pod-from-body", Arg.Do<LogQuery>(q => capturedQuery = q), Arg.Any<CancellationToken>())
            .Returns(page);

        await controller.GetQdrantLogs(request, CancellationToken.None);

        await logReader.Received(1).GetQdrantPodLogsAsync("pod-from-body", Arg.Any<LogQuery>(), Arg.Any<CancellationToken>());
        Assert.That(capturedQuery, Is.Not.Null);
        Assert.That(capturedQuery!.Limit, Is.EqualTo(1));
    }

    [Test]
    public async Task GetQdrantLogs_Exception_Returns500()
    {
        var logReader = Substitute.For<ILogReader>();
        var logger = Substitute.For<ILogger<LogsController>>();
        var controller = new LogsController(logReader, logger);
        var request = new V1GetQdrantLogsRequest { PodName = "pod-err" };
        logReader.GetQdrantPodLogsAsync(Arg.Any<string>(), Arg.Any<LogQuery>(), Arg.Any<CancellationToken>())
            .Returns<Task<LogPage>>(_ => throw new InvalidOperationException("boom"));

        var result = await controller.GetQdrantLogs(request, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var obj = (ObjectResult)result;
        Assert.That(obj.StatusCode, Is.EqualTo(StatusCodes.Status500InternalServerError));
    }

    [Test]
    public async Task GetVigilanteLogs_ValidRequest_ReturnsOk()
    {
        var logReader = Substitute.For<ILogReader>();
        var logger = Substitute.For<ILogger<LogsController>>();
        var controller = new LogsController(logReader, logger);
        var ts = DateTime.UtcNow;
        var request = new V1GetVigilanteLogsRequest { Limit = 3, Continuation = "tok" };
        var page = new LogPage(true, null, new[]
        {
            new LogEntry(ts, "service", "vigilante")
        }, null, false);
        logReader.GetServiceLogsAsync(Arg.Any<LogQuery>(), Arg.Any<CancellationToken>()).Returns(page);

        var result = await controller.GetVigilanteLogs(request, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var ok = (OkObjectResult)result;
        var response = ok.Value as V1LogsPageResponse;
        Assert.That(response, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response!.Logs.Count, Is.EqualTo(1));
            Assert.That(response.Logs[0].Source, Is.EqualTo("vigilante"));
            Assert.That(response.Logs[0].Timestamp, Is.EqualTo(ts).Within(TimeSpan.FromSeconds(1)));
        });
    }

    [Test]
    public async Task GetVigilanteLogs_Exception_Returns500()
    {
        var logReader = Substitute.For<ILogReader>();
        var logger = Substitute.For<ILogger<LogsController>>();
        var controller = new LogsController(logReader, logger);
        var request = new V1GetVigilanteLogsRequest();
        logReader.GetServiceLogsAsync(Arg.Any<LogQuery>(), Arg.Any<CancellationToken>())
            .Returns<Task<LogPage>>(_ => throw new Exception("fail"));

        var result = await controller.GetVigilanteLogs(request, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var obj = (ObjectResult)result;
        Assert.That(obj.StatusCode, Is.EqualTo(StatusCodes.Status500InternalServerError));
    }
}
