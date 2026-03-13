using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Vigilante.Models;
using Vigilante.Services;
using Vigilante.Services.Interfaces;

namespace Aer.Vigilante.Tests.Services;

[TestFixture]
public class JobRegistryTests
{
    private JobRegistry _registry = null!;

    [SetUp]
    public void SetUp()
    {
        var logger = Substitute.For<ILogger<JobRegistry>>();
        _registry = new JobRegistry(logger);
    }

    [Test]
    public void TryAddJob_WhenEmpty_ReturnsTrue()
    {
        var job = CreateFakeJob("job1");
        var cts = new CancellationTokenSource();

        var added = _registry.TryAddJob(job, cts);

        Assert.That(added, Is.True);
        var pending = _registry.GetPendingJobs();
        Assert.That(pending, Has.Count.EqualTo(1));
        Assert.That(pending[0].Job.Key, Is.EqualTo("job1"));
    }

    [Test]
    public void TryAddJob_WhenSameKeyExists_ReturnsFalse()
    {
        var job1 = CreateFakeJob("job1");
        var job2 = CreateFakeJob("job1");
        var cts1 = new CancellationTokenSource();
        var cts2 = new CancellationTokenSource();

        var first = _registry.TryAddJob(job1, cts1);
        var second = _registry.TryAddJob(job2, cts2);

        Assert.That(first, Is.True);
        Assert.That(second, Is.False);
        var pending = _registry.GetPendingJobs();
        Assert.That(pending, Has.Count.EqualTo(1));
        cts2.Dispose();
    }

    [Test]
    public void TryAddJob_DifferentKeys_AllAdded()
    {
        var job1 = CreateFakeJob("job1");
        var job2 = CreateFakeJob("job2");
        var cts1 = new CancellationTokenSource();
        var cts2 = new CancellationTokenSource();

        _registry.TryAddJob(job1, cts1);
        _registry.TryAddJob(job2, cts2);

        var pending = _registry.GetPendingJobs();
        Assert.That(pending, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task RemoveJobAsync_RemovesJobAndDisposes()
    {
        var job = CreateFakeJob("job1");
        var cts = new CancellationTokenSource();
        _registry.TryAddJob(job, cts);

        await _registry.RemoveJobAsync("job1");

        Assert.That(_registry.GetPendingJobs(), Has.Count.EqualTo(0));
        await job.Received(1).DisposeAsync();
    }

    [Test]
    public async Task RemoveJobAsync_WhenKeyMissing_DoesNotThrow()
    {
        await _registry.RemoveJobAsync("missing");
        Assert.That(_registry.GetPendingJobs(), Has.Count.EqualTo(0));
    }

    [Test]
    public async Task CancelJobAsync_WhenJobExists_ReturnsTrue()
    {
        var job = CreateFakeJob("job1");
        var cts = new CancellationTokenSource();
        _registry.TryAddJob(job, cts);

        var cancelled = await _registry.CancelJobAsync("job1");

        Assert.That(cancelled, Is.True);
        Assert.That(_registry.GetPendingJobs(), Has.Count.EqualTo(0));
    }

    [Test]
    public async Task CancelJobAsync_WhenKeyMissing_ReturnsFalse()
    {
        var cancelled = await _registry.CancelJobAsync("missing");
        Assert.That(cancelled, Is.False);
    }

    [Test]
    public void RecordJobFailure_StoredForGetActiveErrorsAndPruneExpired()
    {
        _registry.RecordJobFailure("job1", "error message");

        var errors = _registry.GetActiveErrorsAndPruneExpired(DateTime.UtcNow, TimeSpan.FromMinutes(5));

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0].Key, Is.EqualTo("job1"));
        Assert.That(errors[0].Message, Is.EqualTo("error message"));
    }

    [Test]
    public void GetActiveErrorsAndPruneExpired_WhenExpired_PrunesAndReturnsEmpty()
    {
        _registry.RecordJobFailure("job1", "old error");
        var now = DateTime.UtcNow;
        var errors = _registry.GetActiveErrorsAndPruneExpired(now.AddMinutes(10), TimeSpan.FromMinutes(5));

        Assert.That(errors, Has.Count.EqualTo(0));
    }

    [Test]
    public void GetJobInfos_ReturnsJobKeysAndErrors()
    {
        var job = CreateFakeJob("job1");
        job.GetMetadata().Returns(new Dictionary<string, object?> { ["k"] = "v" });
        var cts = new CancellationTokenSource();
        _registry.TryAddJob(job, cts);
        _registry.RecordJobFailure("job2", "job2 failed");

        var infos = _registry.GetJobInfos();

        // job1 is active; job2 is recently failed (no job in registry) — both are returned so frontend can show them
        Assert.That(infos, Has.Count.EqualTo(2));
        var job1Info = infos.Single(i => i.Key == "job1");
        Assert.That(job1Info.ErrorMessage, Is.Null);
        Assert.That(job1Info.Metadata, Is.Not.Null);
        var job2Info = infos.Single(i => i.Key == "job2");
        Assert.That(job2Info.ErrorMessage, Is.EqualTo("job2 failed"));
        Assert.That(job2Info.Metadata, Is.Null);
    }

    [Test]
    public void GetJobInfos_WhenJobHasRecordedError_IncludesError()
    {
        var job = CreateFakeJob("job1");
        var cts = new CancellationTokenSource();
        _registry.TryAddJob(job, cts);
        _registry.RecordJobFailure("job1", "failed");

        var infos = _registry.GetJobInfos();

        Assert.That(infos, Has.Count.EqualTo(1));
        Assert.That(infos[0].ErrorMessage, Is.EqualTo("failed"));
    }

    [Test]
    public async Task ProcessPendingJobsAsync_WhenJobCompletes_RemovesJob()
    {
        var job = CreateFakeJob("job1");
        var cts = new CancellationTokenSource();
        _registry.TryAddJob(job, cts);

        await _registry.ProcessPendingJobsAsync(CancellationToken.None);

        Assert.That(_registry.GetPendingJobs(), Has.Count.EqualTo(0));
        await job.Received(1).AdvanceAsync(Arg.Any<CancellationToken>());
    }

    private static IJob CreateFakeJob(string key)
    {
        var job = Substitute.For<IJob>();
        job.Key.Returns(key);
        job.IsWaitingForReady.Returns(false);
        job.CheckReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<bool?>(true));
        job.AdvanceAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult((false, true, (string?)null)));
        job.GetMetadata().Returns((IReadOnlyDictionary<string, object?>?)null);
        job.DisposeAsync().Returns(ValueTask.CompletedTask);
        return job;
    }
}
