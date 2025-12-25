using System.IO;
using System.Text;
using k8s;
using k8s.Autorest;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Vigilante.Constants;
using Vigilante.Services;
using Vigilante.Services.Models;

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
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(Path.GetTempPath());
        var reader = new LogReader(kube, logger, env);
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

        Assert.That(page.Success, Is.True);
        Assert.That(page.Logs.Count, Is.EqualTo(2));
        Assert.That(page.Logs[0].Message, Is.EqualTo("first"));
        Assert.That(page.Logs[0].Source, Is.EqualTo("pod-1"));
        Assert.That(page.Truncated, Is.False);
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

        Assert.That(receivedSinceSeconds, Is.Not.Null);
        Assert.That(receivedSinceSeconds, Is.GreaterThan(0));
        Assert.That(receivedTail, Is.EqualTo(2)); // limit+1
        Assert.That(page.Logs.Count, Is.EqualTo(1));
        Assert.That(page.Logs[0].Message, Is.EqualTo("new"));
        Assert.That(page.Truncated, Is.True);
        Assert.That(page.Continuation, Is.Not.Null);
    }

    [Test]
    public async Task GetQdrantPodLogsAsync_NoKubernetes_ReturnsFailure()
    {
        var logger = Substitute.For<ILogger<LogReader>>();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(Path.GetTempPath());
        var reader = new LogReader(null, logger, env);

        var page = await reader.GetQdrantPodLogsAsync("pod-1", new LogQuery(null, 10), CancellationToken.None);

        Assert.That(page.Success, Is.False);
        Assert.That(page.Logs, Is.Empty);
        Assert.That(page.Error, Does.Contain("Kubernetes"));
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

        Assert.That(capturedNs, Is.EqualTo(KubernetesConstants.DefaultNamespace));
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

        Assert.That(receivedSinceSeconds, Is.EqualTo(1));
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
            .ReturnsForAnyArgs(Task.FromResult(new HttpOperationResponse<Stream> { Body = null }));

        var page = await reader.GetQdrantPodLogsAsync("pod-1", new LogQuery("ns", 1), CancellationToken.None);

        Assert.That(page.Success, Is.False);
        Assert.That(page.Error, Does.Contain("empty response body"));
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

        Assert.That(page.Success, Is.False);
        Assert.That(page.Error, Does.Contain("boom"));
    }
}
