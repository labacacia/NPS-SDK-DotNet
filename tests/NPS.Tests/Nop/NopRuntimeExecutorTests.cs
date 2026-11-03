// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NPS.NOP;
using NPS.NOP.Frames;
using NPS.NOP.Models;
using NPS.NOP.Orchestration;
using NPS.NOP.Runtime;
using NPS.NOP.Storage;
using NPS.Samples.NopDag.Orchestration;

namespace NPS.Tests.Nop;

public sealed class NopRuntimeExecutorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nps-nop-runtime-" + Guid.NewGuid().ToString("N"));
    private SqliteNopRuntimeJournal Journal(string name = "tasks") => new(Path.Combine(_directory, name + ".sqlite"), initializeNewFile:true);
    private const string Caller = "urn:nps:agent:caller", WorkerNid = "urn:nps:agent:worker";

    private static TaskFrame Task(int count = 1, uint timeout = 10000) => new()
    {
        TaskId = Guid.NewGuid().ToString("D"), TimeoutMs = timeout, ResultTtlSeconds = 300, MaxRetries = 2,
        Dag = new()
        {
            Nodes = Enumerable.Range(0,count).Select(i => new DagNode { Id = "n"+i, Agent = WorkerNid,
                Action = "nwp://worker/run", InputFrom = i == 0 ? [] : ["n"+(i-1)],
                InputMapping = i == 0 ? null : new Dictionary<string,JsonElement> { ["input"] = JsonSerializer.SerializeToElement("$.n"+(i-1)) } }).ToArray(),
            Edges = Enumerable.Range(1,count-1).Select(i => new DagEdge { From = "n"+(i-1), To = "n"+i }).ToArray(),
        },
    };

    private static NopOrchestrator Executor(INopRuntimeJournal journal, TestWorker worker, string caller = Caller) =>
        new(new UnusedLegacyWorker(), new InMemoryNopTaskStore(), new() { EnableCallback = false,
            RuntimeBinding = new(caller, journal, worker) });

    private static AlignStreamFrame Final(DelegateFrame frame, string certainty = NopExecutionOutcome.KnownTerminal, string? error = null) => new()
    {
        TaskId = frame.ParentTaskId, SubtaskId = frame.SubtaskId, StreamId = "11111111-1111-4111-8111-111111111111",
        Seq = 0, IsFinal = true, SenderNid = frame.TargetAgentNid, ExecutionOutcome = certainty,
        Data = error is null ? JsonSerializer.SerializeToElement(new { value = 7 }) : null,
        Error = error is null ? null : new() { Code = error, Message = "Synthetic error", Retryable = true },
    };

    private sealed class TestWorker(Func<DelegateFrame, CancellationToken, IAsyncEnumerable<NopAuthenticatedFrame>> execute) : INopAuthenticatedWorkerClient
    {
        public readonly List<DelegateFrame> Calls = [];
        public IAsyncEnumerable<NopAuthenticatedFrame> DelegateAsync(DelegateFrame frame, CancellationToken ct)
        { lock (Calls) Calls.Add(frame); return execute(frame, ct); }
    }
    private static async IAsyncEnumerable<NopAuthenticatedFrame> Frames(params NopAuthenticatedFrame[] frames)
    { foreach (var frame in frames) { await System.Threading.Tasks.Task.Yield(); yield return frame; } }
    private static TestWorker Success() => new((frame,_) => Frames(new NopAuthenticatedFrame(WorkerNid,Final(frame))));

    [Fact]
    public async System.Threading.Tasks.Task Three_node_dag_maps_only_proven_outputs_and_duplicate_never_dispatches()
    {
        using var journal = Journal(); var worker = Success(); var runtime = Executor(journal, worker); var task = Task(3);
        var first = await runtime.ExecuteAsync(task); var duplicate = await runtime.ExecuteAsync(task);
        Assert.Equal(TaskState.Completed, first.FinalState); Assert.Equal(NopExecutionOutcome.KnownTerminal, first.ExecutionOutcome);
        Assert.Equal(3, worker.Calls.Count); Assert.Equal(3, first.NodeResults.Count);
        Assert.Equal(7, worker.Calls[1].Params!.Value.GetProperty("input").GetProperty("value").GetInt32());
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(duplicate));
        Assert.Equal(3, worker.Calls.Select(f => f.IdempotencyKey).Distinct().Count());
    }

    [Fact]
    public async System.Threading.Tasks.Task Concurrent_duplicate_returns_running_without_new_dispatch_or_terminal_claim()
    {
        using var journal = Journal(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<NopAuthenticatedFrame> Run(DelegateFrame frame, [EnumeratorCancellation] CancellationToken ct)
        { started.SetResult(); await release.Task.WaitAsync(ct); yield return new(WorkerNid, Final(frame)); }
        var worker = new TestWorker(Run); var runtime = Executor(journal, worker); var task = Task();
        var original = runtime.ExecuteAsync(task); await started.Task;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var duplicate = await runtime.ExecuteAsync(task,cancelled.Token);
        Assert.Equal(TaskState.Running, duplicate.FinalState); Assert.Null(duplicate.ExecutionOutcome);
        Assert.Single(worker.Calls); release.SetResult(); Assert.Equal(TaskState.Completed, (await original).FinalState);
        Assert.Equal(TaskState.Completed, (await runtime.ExecuteAsync(task)).FinalState); Assert.Single(worker.Calls);
    }

    [Fact]
    public async System.Threading.Tasks.Task Digest_conflict_does_not_mutate_original_and_caller_identity_is_independent()
    {
        using var journal = Journal(); var worker = Success(); var task = Task(); var runtime = Executor(journal, worker);
        var original = await runtime.ExecuteAsync(task);
        var conflict = await Assert.ThrowsAsync<NopRuntimeException>(() => runtime.ExecuteAsync(task with { TimeoutMs = 9999 }));
        Assert.Equal(NopErrorCodes.ReplayConflict, conflict.Code); Assert.Single(worker.Calls);
        var other = Executor(journal, worker, "urn:nps:agent:other"); Assert.Null(await other.GetStatusAsync(task.TaskId));
        await other.CancelAsync(task.TaskId); Assert.Equal(TaskState.Completed,(await runtime.ExecuteAsync(task)).FinalState);
        Assert.Equal(TaskState.Completed,(await other.ExecuteAsync(task)).FinalState); Assert.Equal(2,worker.Calls.Count);
    }

    [Theory]
    [InlineData("missing")][InlineData("peer")][InlineData("sender")][InlineData("task")][InlineData("subtask")]
    [InlineData("gap")][InlineData("repeat")][InlineData("stream")][InlineData("early-outcome")][InlineData("eof")]
    [InlineData("contradiction")][InlineData("invalid")][InlineData("unknown-retryable")][InlineData("oversized")][InlineData("null-error")][InlineData("empty-error")][InlineData("contradictory-error")]
    public async System.Threading.Tasks.Task Unproven_terminal_is_indeterminate_without_retry_or_downstream(string fault)
    {
        using var journal = Journal();
        var worker = new TestWorker((request,_) =>
        {
            var final = Final(request); var peer = WorkerNid;
            var progress = final with { IsFinal = false, ExecutionOutcome = null, Data = null };
            final = fault switch
            {
                "missing" => final with { ExecutionOutcome = null }, "peer" => final,
                "sender" => final with { SenderNid = "urn:nps:spoof" }, "task" => final with { TaskId = "wrong" },
                "subtask" => final with { SubtaskId = "wrong" }, "gap" => final with { Seq = 1 },
                "contradiction" => final with { ExecutionOutcome = NopExecutionOutcome.NotDispatched },
                "invalid" => final with { ExecutionOutcome = "safe-to-replay" },
                "unknown-retryable" => Final(request,NopExecutionOutcome.Indeterminate,NopErrorCodes.ExecutionIndeterminate),
                "contradictory-error" => Final(request,NopExecutionOutcome.NotDispatched,NopErrorCodes.ExecutionIndeterminate),
                "null-error" => final with { Data=null,Error=new() { Code=null!,Message="invalid" } },
                "empty-error" => final with { Data=null,Error=new() { Code="",Message="invalid" } },
                "oversized" => final with { Data = JsonSerializer.SerializeToElement(new string('x',70000)) }, _ => final,
            };
            if (fault == "peer") peer = "urn:nps:spoof";
            return fault switch
            {
                "eof" => Frames(), "repeat" => Frames(new NopAuthenticatedFrame(peer,progress),new NopAuthenticatedFrame(peer,final)),
                "stream" => Frames(new NopAuthenticatedFrame(peer,progress),new NopAuthenticatedFrame(peer,final with { Seq=1,StreamId="other" })),
                "early-outcome" => Frames(new NopAuthenticatedFrame(peer,progress with { ExecutionOutcome=NopExecutionOutcome.KnownTerminal })),
                _ => Frames(new NopAuthenticatedFrame(peer,final)),
            };
        });
        var runtime = Executor(journal,worker); var task = Task(2);
        var result = await runtime.ExecuteAsync(task);
        Assert.Equal(NopExecutionOutcome.Indeterminate,result.ExecutionOutcome); Assert.Equal(NopErrorCodes.ExecutionIndeterminate,result.ErrorCode);
        Assert.Empty(result.NodeResults); Assert.Single(worker.Calls);
        using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
        Assert.Equal(NopExecutionOutcome.Indeterminate,(await runtime.ExecuteAsync(task,cancelled.Token)).ExecutionOutcome); Assert.Single(worker.Calls);
    }

    [Fact]
    public async System.Threading.Tasks.Task Positive_non_dispatch_is_only_retry_path_and_reuses_exact_operation_identity()
    {
        using var journal = Journal(); var calls = 0;
        var worker = new TestWorker((frame,_) => Frames(new NopAuthenticatedFrame(WorkerNid,++calls==1
            ? Final(frame,NopExecutionOutcome.NotDispatched,NopErrorCodes.ResourceInsufficient) : Final(frame))));
        var task = Task() with { Dag = Task().Dag with { Nodes = [Task().Dag.Nodes[0] with
            { TimeoutMs=1000, RetryPolicy = new() { MaxRetries=1, InitialDelayMs=1 } }] } };
        var result = await Executor(journal,worker).ExecuteAsync(task);
        Assert.Equal(TaskState.Completed,result.FinalState); Assert.Equal(2,worker.Calls.Count);
        Assert.Equal(worker.Calls[0].SubtaskId,worker.Calls[1].SubtaskId);
        Assert.Equal(worker.Calls[0].IdempotencyKey,worker.Calls[1].IdempotencyKey);
        Assert.True(DateTimeOffset.Parse(worker.Calls[1].DeadlineAt)<=DateTimeOffset.Parse(worker.Calls[0].DeadlineAt));
    }

    [Fact]
    public async System.Threading.Tasks.Task Known_terminal_error_with_retryable_flag_does_not_reexecute_effect()
    {
        using var journal = Journal(); var worker = new TestWorker((f,_)=>Frames(new NopAuthenticatedFrame(WorkerNid,Final(f,NopExecutionOutcome.KnownTerminal,"worker-error"))));
        var result = await Executor(journal,worker).ExecuteAsync(Task());
        Assert.Equal(NopExecutionOutcome.KnownTerminal,result.ExecutionOutcome); Assert.Equal("worker-error",result.ErrorCode); Assert.Single(worker.Calls);
    }

    [Fact]
    public async System.Threading.Tasks.Task Ignored_cancellation_and_late_effect_cannot_extend_deadline_or_replace_terminal()
    {
        using var journal = Journal(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var effects = 0;
        async IAsyncEnumerable<NopAuthenticatedFrame> Run(DelegateFrame frame)
        { started.SetResult(); await release.Task; Interlocked.Increment(ref effects); yield return new(WorkerNid,Final(frame)); }
        var worker = new TestWorker((frame,_)=>Run(frame)); var runtime=Executor(journal,worker); var task=Task(2,100);
        var stopwatch=Stopwatch.StartNew(); var running=runtime.ExecuteAsync(task); await started.Task;
        var result=await running.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(NopExecutionOutcome.Indeterminate,result.ExecutionOutcome); Assert.True(stopwatch.Elapsed<TimeSpan.FromSeconds(2));
        release.SetResult(); await System.Threading.Tasks.Task.Delay(30); Assert.Equal(1,effects);
        await runtime.CancelAsync(task.TaskId); Assert.Equal(JsonSerializer.Serialize(result),JsonSerializer.Serialize(await runtime.ExecuteAsync(task)));
        Assert.Single(worker.Calls);
    }

    [Fact]
    public async System.Threading.Tasks.Task Precancel_and_unsupported_callback_are_zero_dispatch()
    {
        using var journal=Journal(); var worker=Success(); var runtime=Executor(journal,worker);
        using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
        Assert.Equal(NopExecutionOutcome.NotDispatched,(await runtime.ExecuteAsync(Task(),cancelled.Token)).ExecutionOutcome);
        Assert.Equal(NopExecutionOutcome.NotDispatched,(await runtime.ExecuteAsync(Task() with { CallbackUrl="https://example.invalid" })).ExecutionOutcome);
        Assert.Empty(worker.Calls);
    }

    [Fact]
    public async System.Threading.Tasks.Task Restart_preserves_unknown_without_replaying_and_live_second_host_is_rejected()
    {
        var task=Task(); var path=Path.Combine(_directory,"tasks.sqlite");
        using(var journal=new SqliteNopRuntimeJournal(path,initializeNewFile:true))
        {
            await Assert.ThrowsAsync<SimulatedHostExit>(()=>Executor(new CrashAfterAdmission(journal),Success()).ExecuteAsync(task));
            Assert.Throws<IOException>(()=>new SqliteNopRuntimeJournal(path));
        }
        using var reopened=new SqliteNopRuntimeJournal(path); var worker=Success();
        var result=await Executor(reopened,worker).ExecuteAsync(task);
        Assert.Equal(NopExecutionOutcome.Indeterminate,result.ExecutionOutcome); Assert.Empty(worker.Calls);
    }

    [Fact]
    public void Journal_terminal_cas_rejects_wrong_owner_and_keeps_first_winner()
    {
        using var journal=Journal(); var task=Task(); var request=Request(task); var ticket=journal.Admit(request);
        var result=new NopTaskResult { TaskId=task.TaskId,FinalState=TaskState.Failed,ExecutionOutcome=NopExecutionOutcome.Indeterminate,ErrorCode=NopErrorCodes.ExecutionIndeterminate };
        Assert.Equal(NopErrorCodes.ClaimConflict,Assert.Throws<NopRuntimeException>(()=>journal.Commit(Caller,task.TaskId,"wrong",result)).Code);
        journal.Commit(Caller,task.TaskId,ticket.OwnerToken!,result);
        var late=new NopTaskResult { TaskId=task.TaskId,FinalState=TaskState.Completed,ExecutionOutcome=NopExecutionOutcome.KnownTerminal };
        Assert.Equal(NopExecutionOutcome.Indeterminate,journal.Commit(Caller,task.TaskId,ticket.OwnerToken!,late).ExecutionOutcome);
    }

    [Fact]
    public async System.Threading.Tasks.Task Precancelled_duplicates_preserve_pending_terminal_and_unknown_certainty()
    {
        using var journal=Journal(); var worker=Success(); var runtime=Executor(journal,worker); var task=Task();
        var original=await runtime.ExecuteAsync(task); using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
        Assert.Equal(JsonSerializer.Serialize(original),JsonSerializer.Serialize(await runtime.ExecuteAsync(task,cancelled.Token)));
        var invalid=await Assert.ThrowsAsync<NopRuntimeException>(()=>runtime.ExecuteAsync(task with { TimeoutMs=0 },cancelled.Token));
        Assert.Equal(NopErrorCodes.ReplayConflict,invalid.Code); Assert.Single(worker.Calls);
        var never=Task(); var first=await runtime.ExecuteAsync(never,cancelled.Token);
        Assert.Equal(NopExecutionOutcome.NotDispatched,first.ExecutionOutcome);
        Assert.Equal(JsonSerializer.Serialize(first),JsonSerializer.Serialize(await runtime.ExecuteAsync(never)));
        Assert.Single(worker.Calls);
    }

    [Fact]
    public void Reopen_missing_database_or_schema_fails_closed_without_silent_new_history()
    {
        var path=Path.Combine(_directory,"missing.sqlite");
        using(var journal=new SqliteNopRuntimeJournal(path,initializeNewFile:true)) journal.Admit(Request(Task()));
        File.Delete(path);
        Assert.ThrowsAny<Exception>(()=>new SqliteNopRuntimeJournal(path));
        Assert.False(File.Exists(path));
        Assert.ThrowsAny<Exception>(()=>new SqliteNopRuntimeJournal(path,initializeNewFile:true));
        var schemaPath=Path.Combine(_directory,"schema.sqlite");
        using(var journal=new SqliteNopRuntimeJournal(schemaPath,initializeNewFile:true)) journal.Admit(Request(Task()));
        using(var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+schemaPath))
        { connection.Open(); using var command=connection.CreateCommand(); command.CommandText="DROP TABLE nop_tasks"; command.ExecuteNonQuery(); }
        Assert.ThrowsAny<Exception>(()=>new SqliteNopRuntimeJournal(schemaPath));
    }

    [Fact]
    public async System.Threading.Tasks.Task Concurrent_finish_cancel_preserves_one_terminal_and_cancellation_never_throws()
    {
        using var journal=Journal(); var worker=Success(); var runtime=Executor(journal,worker);
        for(var i=0;i<50;i++)
        {
            var task=Task(); var run=runtime.ExecuteAsync(task);
            await System.Threading.Tasks.Task.WhenAll(Enumerable.Range(0,4).Select(_=>System.Threading.Tasks.Task.Run(()=>runtime.CancelAsync(task.TaskId))));
            var result=await run;
            await runtime.CancelAsync(task.TaskId);
            Assert.Equal(JsonSerializer.Serialize(result),JsonSerializer.Serialize(await runtime.ExecuteAsync(task)));
        }
        Assert.InRange(worker.Calls.Count,0,50);
    }

    [Fact]
    public void Capacity_never_evicts_replay_history_and_expiry_hides_payload_despite_wall_rollback()
    {
        var clock=new RuntimeClock();
        using var journal=new SqliteNopRuntimeJournal(Path.Combine(_directory,"capacity.sqlite"),initializeNewFile:true,timeProvider:clock);
        var first=Task() with { ResultTtlSeconds=1 }; var request=Request(first); var ticket=journal.Admit(request);
        journal.Commit(Caller,first.TaskId,ticket.OwnerToken!,new() { TaskId=first.TaskId,FinalState=TaskState.Completed,
            ExecutionOutcome=NopExecutionOutcome.KnownTerminal,AggregatedResult=JsonSerializer.SerializeToElement(new { private_result=true }) });
        clock.Elapsed=TimeSpan.FromSeconds(2); clock.Wall=clock.Wall.AddSeconds(-100);
        Assert.Equal(NopErrorCodes.TaskResultExpired,Assert.Throws<NopRuntimeException>(()=>journal.Admit(request)).Code);
        for(var i=1;i<256;i++) journal.Admit(Request(Task()));
        Assert.Equal(NopErrorCodes.ReplayLimit,Assert.Throws<NopRuntimeException>(()=>journal.Admit(Request(Task()))).Code);
        Assert.Equal(NopErrorCodes.TaskResultExpired,Assert.Throws<NopRuntimeException>(()=>journal.Admit(request)).Code);
    }
    private sealed class RuntimeClock : TimeProvider
    {
        internal DateTimeOffset Wall=DateTimeOffset.UtcNow;
        internal TimeSpan Elapsed;
        public override DateTimeOffset GetUtcNow()=>Wall;
        public override long GetTimestamp()=>Elapsed.Ticks;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
    }

    [Fact]
    public void Unselected_legacy_contractless_wire_does_not_gain_a_null_runtime_member()
    {
        var request=new DelegateFrame { ParentTaskId=Guid.NewGuid().ToString("D"),SubtaskId=Guid.NewGuid().ToString("D"),
            NodeId="n0",TargetAgentNid=WorkerNid,Action="nwp://worker/run",DeadlineAt=DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"),
            DelegatedScope=JsonSerializer.SerializeToElement(new { }) };
        var frame=Final(request) with { ExecutionOutcome=null };
        var wire=new NPS.Core.Codecs.Tier2MsgPackCodec().Encode(frame);
        Assert.DoesNotContain("ExecutionOutcome",MessagePack.MessagePackSerializer.ConvertToJson(wire));
        Assert.DoesNotContain("execution_outcome",JsonSerializer.Serialize(frame));
        Assert.Contains("execution_outcome",JsonSerializer.Serialize(Final(request)));
    }

    private static NopRuntimeRequest Request(TaskFrame task)
    {
        return new(Caller,task,new string('a',64),DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddSeconds(10),
            task.Dag.Nodes.ToDictionary(n=>n.Id,_=>new NopOperationIdentity(Guid.NewGuid().ToString("D"),Guid.NewGuid().ToString("D"))));
    }
    private sealed class SimulatedHostExit : Exception;
    private sealed class CrashAfterAdmission(INopRuntimeJournal journal) : INopRuntimeJournal
    {
        public NopRuntimeAdmission? ReadReplay(string caller,string task,string digest)=>journal.ReadReplay(caller,task,digest);
        public NopRuntimeAdmission Admit(NopRuntimeRequest request) { journal.Admit(request); throw new SimulatedHostExit(); }
        public NopTaskResult Commit(string caller,string task,string token,NopTaskResult result)=>throw new InvalidOperationException();
        public NopTaskRecord? GetStatus(string caller,string task)=>journal.GetStatus(caller,task);
    }
    private sealed class UnusedLegacyWorker : INopWorkerClient
    {
        public IAsyncEnumerable<AlignStreamFrame> DelegateAsync(DelegateFrame frame,CancellationToken ct=default)=>throw new InvalidOperationException("Selected runtime must not dispatch legacy worker.");
        public System.Threading.Tasks.Task<PreflightResult> PreflightAsync(string nid,string action,long estimatedNpt=0,IReadOnlyList<string>? requiredCapabilities=null,CancellationToken ct=default)=>throw new InvalidOperationException();
    }
    public void Dispose() { if(Directory.Exists(_directory)) Directory.Delete(_directory,true); }
}
