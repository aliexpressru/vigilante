using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;
using Vigilante.Controllers;
using Vigilante.Models;
using Vigilante.Models.Requests;
using Vigilante.Models.Responses;
using Vigilante.Services.Interfaces;

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
        var page = new LogPage(true, null,
        [
            new LogEntry(ts1, "msg1", "pod-1"),
            new LogEntry(ts2, "msg2", "pod-1")
        ], "next", true);
        logReader.GetQdrantPodLogsAsync("pod-1", Arg.Any<LogQuery>(), Arg.Any<CancellationToken>()).Returns(page);

        var result = await controller.GetQdrantLogs(request, CancellationToken.None);

        result.Should().BeAssignableTo<OkObjectResult>();
        var ok = (OkObjectResult)result;
        var response = ok.Value as V1LogsPageResponse;
        response.Should().NotBeNull();
        using (new AssertionScope())
        {
            response!.Success.Should().BeTrue();
            response.Logs.Count.Should().Be(2);
            response.Logs[0].Message.Should().Be("msg1");
            response.Logs[0].Timestamp.Should().BeCloseTo(ts1, TimeSpan.FromSeconds(1));
            response.Continuation.Should().Be("next");
            response.Truncated.Should().BeTrue();
        }
    }

    [Test]
    public async Task GetQdrantLogs_UsesPodNameFromBody()
    {
        var logReader = Substitute.For<ILogReader>();
        var logger = Substitute.For<ILogger<LogsController>>();
        var controller = new LogsController(logReader, logger);
        var request = new V1GetQdrantLogsRequest { PodName = "pod-from-body", Limit = 1 };
        var page = new LogPage(true, null, [], null, false);
        LogQuery? capturedQuery = null;
        logReader
            .GetQdrantPodLogsAsync("pod-from-body", Arg.Do<LogQuery>(q => capturedQuery = q), Arg.Any<CancellationToken>())
            .Returns(page);

        await controller.GetQdrantLogs(request, CancellationToken.None);

        await logReader.Received(1).GetQdrantPodLogsAsync("pod-from-body", Arg.Any<LogQuery>(), Arg.Any<CancellationToken>());
        capturedQuery.Should().NotBeNull();
        capturedQuery!.Limit.Should().Be(1);
    }

    [Test]
    public async Task GetQdrantLogs_MapsFiltersToQuery()
    {
        var logReader = Substitute.For<ILogReader>();
        var logger = Substitute.For<ILogger<LogsController>>();
        var controller = new LogsController(logReader, logger);
        var request = new V1GetQdrantLogsRequest
        {
            PodName = "pod-1",
            Namespace = "ns",
            Limit = 50,
            Continuation = "tok",
            Levels = LogLevelFilter.Info | LogLevelFilter.Error,
            SearchText = "cluster"
        };
        var page = new LogPage(true, null, [], null, false);
        LogQuery? capturedQuery = null;
        logReader.GetQdrantPodLogsAsync("pod-1", Arg.Do<LogQuery>(q => capturedQuery = q), Arg.Any<CancellationToken>())
            .Returns(page);

        await controller.GetQdrantLogs(request, CancellationToken.None);

        capturedQuery.Should().NotBeNull();
        using (new AssertionScope())
        {
            capturedQuery!.Namespace.Should().Be("ns");
            capturedQuery.Limit.Should().Be(50);
            capturedQuery.Continuation.Should().Be("tok");
            capturedQuery.Levels.Should().Be(LogLevelFilter.Info | LogLevelFilter.Error);
            capturedQuery.SearchText.Should().Be("cluster");
        }
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

        result.Should().BeAssignableTo<ObjectResult>();
        var obj = (ObjectResult)result;
        obj.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    [Test]
    public async Task GetVigilanteLogs_ValidRequest_ReturnsOk()
    {
        var logReader = Substitute.For<ILogReader>();
        var logger = Substitute.For<ILogger<LogsController>>();
        var controller = new LogsController(logReader, logger);
        var ts = DateTime.UtcNow;
        var request = new V1GetVigilanteLogsRequest { Limit = 3, Continuation = "tok" };
        var page = new LogPage(true, null,
        [
            new LogEntry(ts, "service", "vigilante")
        ], null, false);
        logReader.GetServiceLogsAsync(Arg.Any<LogQuery>(), Arg.Any<CancellationToken>()).Returns(page);

        var result = await controller.GetVigilanteLogs(request, CancellationToken.None);

        result.Should().BeAssignableTo<OkObjectResult>();
        var ok = (OkObjectResult)result;
        var response = ok.Value as V1LogsPageResponse;
        response.Should().NotBeNull();
        using (new AssertionScope())
        {
            response!.Logs.Count.Should().Be(1);
            response.Logs[0].Source.Should().Be("vigilante");
            response.Logs[0].Timestamp.Should().BeCloseTo(ts, TimeSpan.FromSeconds(1));
        }
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

        result.Should().BeAssignableTo<ObjectResult>();
        var obj = (ObjectResult)result;
        obj.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }
}
