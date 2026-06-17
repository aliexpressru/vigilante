using System.Text;
using FluentAssertions;
using k8s;
using k8s.Autorest;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Vigilante.Constants;
using Vigilante.Models;
using Vigilante.Services;
using Vigilante.Services.Interfaces;

namespace Aer.Vigilante.Tests.Services;

[TestFixture]
public class LogReaderTests
{
    private static MemoryStream BuildLogStream(string content) => new(Encoding.UTF8.GetBytes(content));

    private static (LogReader reader, IKubernetes kube, ICoreV1Operations core) CreateReaderWithKube()
    {
        var kube = Substitute.For<IKubernetes>();
        var core = Substitute.For<ICoreV1Operations>();
        kube.CoreV1.Returns(core);
        var logger = Substitute.For<ILogger<LogReader>>();
        var kubernetesManager = Substitute.For<IKubernetesManager>();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(Path.GetTempPath());
        var reader = new LogReader(kube, kubernetesManager, logger, env);
        return (reader, kube, core);
    }

    private static Task<HttpOperationResponse<Stream>> StubLogResponse(string content)
    {
        var response = new HttpOperationResponse<Stream> { Body = BuildLogStream(content) };
        return Task.FromResult(response);
    }

    [Test]
    public async Task GetQdrantPodLogsAsync_ParsesLogs_FromKubernetes()
    {
        var (reader, _, core) = CreateReaderWithKube();
        core.ReadNamespacedPodLogWithHttpMessagesAsync(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<int?>(),
                Arg.Any<string?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(ci => StubLogResponse("2025-01-01T00:00:00Z first\n2025-01-01T00:00:01Z second"));

        var query = new LogQuery("custom-ns", 2, null);

        var page = await reader.GetQdrantPodLogsAsync("pod-1", query, CancellationToken.None);

        page.Success.Should().BeTrue();
        page.Logs.Count.Should().Be(2);
        page.Logs[0].Message.Should().Be("first");
        page.Logs[0].Source.Should().Be("pod-1");
        page.Truncated.Should().BeFalse();
    }

    [Test]
    public async Task GetQdrantPodLogsAsync_FiltersByLevel_UsingQdrantFormat()
    {
        var (reader, _, core) = CreateReaderWithKube();
        core.ReadNamespacedPodLogWithHttpMessagesAsync(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<int?>(),
                Arg.Any<string?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(ci => StubLogResponse("2025-01-01T00:00:00Z  INFO keep\n2025-01-01T00:00:01Z  ERROR skip"));

        var page = await reader.GetQdrantPodLogsAsync(
            "pod-1",
            new LogQuery("custom-ns", 10, Levels: LogLevelFilter.Info),
            CancellationToken.None);

        page.Success.Should().BeTrue();
        page.Logs.Count.Should().Be(1);
        page.Logs[0].Message.Should().Contain("INFO keep");
    }

    [Test]
    public async Task GetQdrantPodLogsAsync_FiltersByLevel_UsingVigilanteFormatAbbreviations()
    {
        var (reader, _, core) = CreateReaderWithKube();
        core.ReadNamespacedPodLogWithHttpMessagesAsync(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<int?>(),
                Arg.Any<string?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(ci => StubLogResponse("2025-01-01T00:00:00Z [09:49:33 INF] keep\n2025-01-01T00:00:01Z [09:49:33 ERR] drop"));

        var page = await reader.GetQdrantPodLogsAsync(
            "pod-1",
            new LogQuery("custom-ns", 10, Levels: LogLevelFilter.Error),
            CancellationToken.None);

        page.Success.Should().BeTrue();
        page.Logs.Count.Should().Be(1);
        page.Logs[0].Message.Should().Contain("[09:49:33 ERR]");
    }

    [Test]
    public async Task GetQdrantPodLogsAsync_FiltersBySearchText_CaseInsensitive()
    {
        var (reader, _, core) = CreateReaderWithKube();
        core.ReadNamespacedPodLogWithHttpMessagesAsync(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<int?>(),
                Arg.Any<string?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(ci => StubLogResponse("2025-01-01T00:00:00Z first line\n2025-01-01T00:00:01Z second line"));

        var page = await reader.GetQdrantPodLogsAsync(
            "pod-1",
            new LogQuery("custom-ns", 10, SearchText: "SECOND"),
            CancellationToken.None);

        page.Success.Should().BeTrue();
        page.Logs.Count.Should().Be(1);
        page.Logs[0].Message.Should().Be("second line");
    }

    [Test]
    public async Task GetQdrantPodLogsAsync_DoesNotFilter_WhenLevelsNone()
    {
        var (reader, _, core) = CreateReaderWithKube();
        core.ReadNamespacedPodLogWithHttpMessagesAsync(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<int?>(),
                Arg.Any<string?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(ci => StubLogResponse("2025-01-01T00:00:00Z  INFO first\n2025-01-01T00:00:01Z  ERROR second"));

        var page = await reader.GetQdrantPodLogsAsync(
            "pod-1",
            new LogQuery("custom-ns", 10, Levels: LogLevelFilter.None),
            CancellationToken.None);

        page.Success.Should().BeTrue();
        page.Logs.Count.Should().Be(2);
    }

    [Test]
    public async Task GetQdrantPodLogsAsync_AppliesContinuation_AndTruncates()
    {
        var log = "2025-01-01T00:00:00Z old\n2025-01-01T00:00:01Z new\n2025-01-01T00:00:02Z newer";
        var (reader, _, core) = CreateReaderWithKube();
        int? receivedSinceSeconds = null;
        int? receivedTail = null;
        core.ReadNamespacedPodLogWithHttpMessagesAsync(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<int?>(),
                Arg.Any<string?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
             .ReturnsForAnyArgs(ci =>
             {
                 // Parameter positions: name(0), namespace(1), container(2), follow(3), insecure(4), limitBytes(5), pretty(6), previous(7), sinceSeconds(8), stream(9), tailLines(10), timestamps(11), customHeaders(12), cancellationToken(13)
                 receivedSinceSeconds = ci.ArgAt<int?>(8);
                 receivedTail = ci.ArgAt<int?>(10);
                 return StubLogResponse(log);
             });

        var cursorTs = DateTime.Parse("2025-01-01T00:00:00Z", null, System.Globalization.DateTimeStyles.RoundtripKind);
        var continuation = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cursorTs:o}|pod-1"));
        var page = await reader.GetQdrantPodLogsAsync("pod-1", new LogQuery("ns", 1, continuation), CancellationToken.None);

        receivedSinceSeconds.Should().NotBeNull();
        receivedSinceSeconds.Should().BeGreaterThan(0);
        receivedTail.Should().Be(2); // limit+1
        page.Logs.Count.Should().Be(1);
        page.Logs[0].Message.Should().Be("new");
        page.Truncated.Should().BeTrue();
        page.Continuation.Should().NotBeNull();
    }

    [Test]
    public async Task GetQdrantPodLogsAsync_NoKubernetes_ReturnsFailure()
    {
        var logger = Substitute.For<ILogger<LogReader>>();
        var kubernetesManager = Substitute.For<IKubernetesManager>();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(Path.GetTempPath());
        var reader = new LogReader(null, kubernetesManager, logger, env);

        var page = await reader.GetQdrantPodLogsAsync("pod-1", new LogQuery(null, 10), CancellationToken.None);

        page.Success.Should().BeFalse();
        page.Logs.Should().BeEmpty();
        page.Error.Should().Contain("Kubernetes");
    }

    [Test]
    public async Task GetQdrantPodLogsAsync_DefaultsNamespace_WhenMissing()
    {
        var (reader, _, core) = CreateReaderWithKube();
        string? capturedNs = null;
        core.ReadNamespacedPodLogWithHttpMessagesAsync(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<int?>(),
                Arg.Any<string?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(ci =>
            {
                capturedNs = ci.ArgAt<string?>(1);
                return StubLogResponse("2025-01-01T00:00:00Z line");
            });

        await reader.GetQdrantPodLogsAsync("pod-1", new LogQuery(null, 1), CancellationToken.None);

        capturedNs.Should().Be(KubernetesConstants.DefaultNamespace);
    }

    [Test]
    public async Task GetQdrantPodLogsAsync_InvalidContinuation_UsesSinceSecondsOne()
    {
        var (reader, _, core) = CreateReaderWithKube();
        int? receivedSinceSeconds = null;
        core.ReadNamespacedPodLogWithHttpMessagesAsync(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<int?>(),
                Arg.Any<string?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(ci =>
            {
                receivedSinceSeconds = ci.ArgAt<int?>(8);
                return StubLogResponse("2025-01-01T00:00:00Z only");
            });

        await reader.GetQdrantPodLogsAsync("pod-1", new LogQuery("ns", 5, "not-base64"), CancellationToken.None);

        receivedSinceSeconds.Should().Be(1);
    }

    [Test]
    public async Task GetQdrantPodLogsAsync_ReturnsFailure_WhenResponseBodyNull()
    {
        var (reader, _, core) = CreateReaderWithKube();
        core.ReadNamespacedPodLogWithHttpMessagesAsync(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<int?>(),
                Arg.Any<string?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult(new HttpOperationResponse<Stream> { Body = null! }));

        var page = await reader.GetQdrantPodLogsAsync("pod-1", new LogQuery("ns", 1), CancellationToken.None);

        page.Success.Should().BeFalse();
        page.Error.Should().Contain("empty response body");
    }

    [Test]
    public async Task GetQdrantPodLogsAsync_ReturnsFailure_WhenKubernetesThrows()
    {
        var (reader, _, core) = CreateReaderWithKube();
        core.ReadNamespacedPodLogWithHttpMessagesAsync(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<int?>(),
                Arg.Any<string?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .ThrowsForAnyArgs(new Exception("boom"));

        var page = await reader.GetQdrantPodLogsAsync("pod-1", new LogQuery("ns", 1), CancellationToken.None);

        page.Success.Should().BeFalse();
        page.Error.Should().Contain("boom");
    }
}
