using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NSubstitute;
using FluentAssertions;
using NUnit.Framework;
using Vigilante.Services;
using Vigilante.Services.Interfaces;
using Vigilante.Services.Jobs.Snapshots;

namespace Aer.Vigilante.Tests.Services;

#pragma warning disable CA2012 // Use ValueTasks correctly | Justification - ValueTask returned form DisposeAsync is inspected in tests;

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

        added.Should().BeTrue();
        var pending = _registry.GetPendingJobs();
        pending.Should().HaveCount(1);
        pending[0].Job.Key.Should().Be("job1");
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

        first.Should().BeTrue();
        second.Should().BeFalse();
        var pending = _registry.GetPendingJobs();
        pending.Should().HaveCount(1);
        cts2.Dispose();
    }

    [Test]
    public void HasPendingSnapshotCreationForCollection_MatchesSnapshotCreatePrefixCaseInsensitive()
    {
        var job = CreateFakeJob(PendingSnapshotCreationJob.KeyPrefix + "MyCollection");
        _registry.TryAddJob(job, new CancellationTokenSource());

        _registry.HasPendingSnapshotCreationForCollection("mycollection").Should().BeTrue();
        _registry.HasPendingSnapshotCreationForCollection("MyCollection").Should().BeTrue();
        _registry.HasPendingSnapshotCreationForCollection("other").Should().BeFalse();
    }

    [Test]
    public void HasPendingMultiSnapshotRecoveryForCollection_MatchesMultiRecoveryPrefixCaseInsensitive()
    {
        var job = CreateFakeJob(RecoverFromMultipleSnapshotsJob.JobKeyPrefix + "MyCollection");
        _registry.TryAddJob(job, new CancellationTokenSource());

        _registry.HasPendingMultiSnapshotRecoveryForCollection("mycollection").Should().BeTrue();
        _registry.HasPendingMultiSnapshotRecoveryForCollection("MyCollection").Should().BeTrue();
        _registry.HasPendingMultiSnapshotRecoveryForCollection("other").Should().BeFalse();
    }

    [Test]
    public void HasPendingMultiSnapshotRecoveryForCollection_WhenNoJobsRegistered_ReturnsFalse()
    {
        _registry.HasPendingMultiSnapshotRecoveryForCollection("col1").Should().BeFalse();
    }

    [Test]
    public void HasPendingMultiSnapshotRecoveryForCollection_WhenOnlySnapshotCreateJobExists_ReturnsFalse()
    {
        var job = CreateFakeJob(PendingSnapshotCreationJob.KeyPrefix + "col1");
        _registry.TryAddJob(job, new CancellationTokenSource());

        _registry.HasPendingMultiSnapshotRecoveryForCollection("col1").Should().BeFalse();
    }

    [Test]
    public void HasPendingSnapshotRecoveryForCollection_MatchesRecoveryPrefixCaseInsensitive()
    {
        var job = CreateFakeJob(RecoverFromSnapshotJob.JobKeyPrefix + "MyCollection");
        _registry.TryAddJob(job, new CancellationTokenSource());

        _registry.HasPendingSnapshotRecoveryForCollection("mycollection").Should().BeTrue();
        _registry.HasPendingSnapshotRecoveryForCollection("MyCollection").Should().BeTrue();
        _registry.HasPendingSnapshotRecoveryForCollection("other").Should().BeFalse();
    }

    [Test]
    public void HasPendingSnapshotRecoveryForCollection_WhenNoJobsRegistered_ReturnsFalse()
    {
        _registry.HasPendingSnapshotRecoveryForCollection("col1").Should().BeFalse();
    }

    [Test]
    public void HasPendingSnapshotRecoveryForCollection_WhenOnlyMultiRecoveryJobExists_ReturnsFalse()
    {
        var job = CreateFakeJob(RecoverFromMultipleSnapshotsJob.JobKeyPrefix + "col1");
        _registry.TryAddJob(job, new CancellationTokenSource());

        _registry.HasPendingSnapshotRecoveryForCollection("col1").Should().BeFalse();
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
        pending.Should().HaveCount(2);
    }

    [Test]
    public async Task RemoveJobAsync_RemovesJobAndDisposes()
    {
        var job = CreateFakeJob("job1");
        var cts = new CancellationTokenSource();
        _registry.TryAddJob(job, cts);

        await _registry.RemoveJobAsync("job1");

        _registry.GetPendingJobs().Should().HaveCount(0);
        await job.Received(1).DisposeAsync();
    }

    [Test]
    public async Task RemoveJobAsync_WhenKeyMissing_DoesNotThrow()
    {
        await _registry.RemoveJobAsync("missing");
        _registry.GetPendingJobs().Should().HaveCount(0);
    }

    [Test]
    public async Task CancelJobAsync_WhenJobExists_ReturnsTrue()
    {
        var job = CreateFakeJob("job1");
        var cts = new CancellationTokenSource();
        _registry.TryAddJob(job, cts);

        var cancelled = await _registry.CancelJobAsync("job1");

        cancelled.Should().BeTrue();
        _registry.GetPendingJobs().Should().HaveCount(0);
    }

    [Test]
    public async Task CancelJobAsync_WhenKeyMissing_ReturnsFalse()
    {
        var cancelled = await _registry.CancelJobAsync("missing");
        cancelled.Should().BeFalse();
    }

    [Test]
    public void RecordJobFailure_StoredForGetActiveErrorsAndPruneExpired()
    {
        _registry.RecordJobFailure("job1", "error message");

        var errors = _registry.GetActiveErrorsAndPruneExpired(DateTime.UtcNow, TimeSpan.FromMinutes(5));

        errors.Should().HaveCount(1);
        errors[0].Key.Should().Be("job1");
        errors[0].Message.Should().Be("error message");
    }

    [Test]
    public void GetActiveErrorsAndPruneExpired_WhenExpired_PrunesAndReturnsEmpty()
    {
        _registry.RecordJobFailure("job1", "old error");
        var now = DateTime.UtcNow;
        var errors = _registry.GetActiveErrorsAndPruneExpired(now.AddMinutes(10), TimeSpan.FromMinutes(5));

        errors.Should().HaveCount(0);
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
        infos.Should().HaveCount(2);
        var job1Info = infos.Single(i => i.Key == "job1");
        job1Info.ErrorMessage.Should().BeNull();
        job1Info.Metadata.Should().NotBeNull();
        var job2Info = infos.Single(i => i.Key == "job2");
        job2Info.ErrorMessage.Should().Be("job2 failed");
        job2Info.Metadata.Should().BeNull();
    }

    [Test]
    public void GetJobInfos_WhenJobHasRecordedError_IncludesError()
    {
        var job = CreateFakeJob("job1");
        var cts = new CancellationTokenSource();
        _registry.TryAddJob(job, cts);
        _registry.RecordJobFailure("job1", "failed");

        var infos = _registry.GetJobInfos();

        infos.Should().HaveCount(1);
        infos[0].ErrorMessage.Should().Be("failed");
    }

    [Test]
    public async Task ProcessPendingJobsAsync_WhenJobCompletes_RemovesJob()
    {
        var job = CreateFakeJob("job1");
        var cts = new CancellationTokenSource();
        _registry.TryAddJob(job, cts);

        await _registry.ProcessPendingJobsAsync(CancellationToken.None);

        _registry.GetPendingJobs().Should().HaveCount(0);
        await job.Received(1).AdvanceAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessPendingJobsAsync_WhenJobCompletesWithFailure_RecordsErrorAndRemovesJob()
    {
        var job = CreateFakeJob("job1");
        var cts = new CancellationTokenSource();
        job.AdvanceAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((false, false, (string?)"timeout")));
        _registry.TryAddJob(job, cts);

        await _registry.ProcessPendingJobsAsync(CancellationToken.None);

        _registry.GetPendingJobs().Should().BeEmpty();
        var infos = _registry.GetJobInfos();
        infos.Any(i => i.Key == "job1" && i.ErrorMessage == "timeout").Should().BeTrue();
    }

    [Test]
    public async Task ProcessPendingJobsAsync_WhenJobKeyExcluded_LeavesJobPendingAndDoesNotAdvance()
    {
        var job = CreateFakeJob("snapshot-automation");
        var cts = new CancellationTokenSource();
        _registry.TryAddJob(job, cts);

        await _registry.ProcessPendingJobsAsync(CancellationToken.None, new HashSet<string> { "snapshot-automation" });

        _registry.GetPendingJobs().Should().HaveCount(1);
        await job.DidNotReceive().AdvanceAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: monitor and JobsController can call ProcessPendingJobsAsync concurrently.
    /// Without per-key gate, both would run AdvanceAsync; the first RemoveJobAsync cancels CTS and aborts the second (TaskCanceledException on HTTP).
    /// </summary>
    [Test]
    public async Task ProcessPendingJobsAsync_TwoConcurrentCallsSameKey_AdvanceAsyncRunsOnce()
    {
        var job = Substitute.For<IJob>();
        job.Key.Returns("snapshot-automation");
        job.IsWaitingForReady.Returns(false);
        job.CheckReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<bool?>(true));
        job.GetMetadata().Returns((IReadOnlyDictionary<string, object?>?)null);
        job.DisposeAsync().Returns(ValueTask.CompletedTask);

        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var advanceStarted = 0;
        job.AdvanceAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref advanceStarted);
                return unblock.Task.ContinueWith(
                    static _ => (false, true, (string?)null),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            });

        _registry.TryAddJob(job, new CancellationTokenSource());

        var process1 = _registry.ProcessPendingJobsAsync(CancellationToken.None);
        await Task.Delay(100);
        advanceStarted.Should().Be(1, "first processor should be inside AdvanceAsync");

        var process2 = _registry.ProcessPendingJobsAsync(CancellationToken.None);
        await Task.Delay(100);
        advanceStarted.Should().Be(1, "second call must wait on gate, not run AdvanceAsync in parallel");

        unblock.SetResult();
        await Task.WhenAll(process1, process2);

        advanceStarted.Should().Be(1);
        _registry.GetPendingJobs().Should().BeEmpty();
        await job.Received(1).AdvanceAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessPendingJobsAsync_SecondWaitsThenSeesRemovedJob_DoesNotThrow()
    {
        var job = Substitute.For<IJob>();
        job.Key.Returns("j");
        job.IsWaitingForReady.Returns(false);
        job.CheckReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<bool?>(true));
        job.GetMetadata().Returns((IReadOnlyDictionary<string, object?>?)null);
        job.DisposeAsync().Returns(ValueTask.CompletedTask);

        var step = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        job.AdvanceAsync(Arg.Any<CancellationToken>())
            .Returns(_ => step.Task.ContinueWith(
                static _ => (false, true, (string?)null),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default));

        _registry.TryAddJob(job, new CancellationTokenSource());

        var a = _registry.ProcessPendingJobsAsync(CancellationToken.None);
        var b = _registry.ProcessPendingJobsAsync(CancellationToken.None);

        await Task.Delay(50);
        step.SetResult();

        Func<Task> act = async () => await Task.WhenAll(a, b);
        await act.Should().NotThrowAsync();
        _registry.GetPendingJobs().Should().BeEmpty();
    }

    [Test]
    public async Task ProcessPendingJobsAsync_DifferentKeys_AdvanceAsyncRunsInParallel()
    {
        var jobA = CreateFakeJob("a");
        var jobB = CreateFakeJob("b");
        var entered = new ConcurrentBag<string>();

        jobA.AdvanceAsync(Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                entered.Add("a");
                await Task.Delay(150);
                return (false, true, (string?)null);
            });
        jobB.AdvanceAsync(Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                entered.Add("b");
                await Task.Delay(150);
                return (false, true, (string?)null);
            });

        _registry.TryAddJob(jobA, new CancellationTokenSource());
        _registry.TryAddJob(jobB, new CancellationTokenSource());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _registry.ProcessPendingJobsAsync(CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(250,
            "jobs with different keys should not serialize each other");
        await jobA.Received(1).AdvanceAsync(Arg.Any<CancellationToken>());
        await jobB.Received(1).AdvanceAsync(Arg.Any<CancellationToken>());
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

#pragma warning restore CA2012 // Use ValueTasks correctly
