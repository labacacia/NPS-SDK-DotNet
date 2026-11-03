// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.NWP.ActionNode;
using NPS.NWP.Frames;
using NPS.NWP.Http;
using NPS.NWP.Native;
using NPS.NWP.Results;

namespace NPS.Tests.Nwp;

// Real framed dispatcher/codec with scripted transport, not native mTLS/NIP qualification.
public sealed partial class NwpNativeNodeServerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeActionAuthorizesBeforeExecutionAndPreservesControlledDenial(bool deny)
    {
        var codec = MakeCodec();
        var provider = new NativeAdmissionProbe(deny);
        var options = new ActionNodeOptions
        {
            NodeId = "urn:nps:node:fixture:native-actions",
            PathPrefix = "/native-actions",
            Actions = new Dictionary<string, ActionSpec> { ["orders.ping"] = new() { Async = false } },
        };
        var server = new NwpNativeNodeServer(codec, new() { ActionOptions = options }, actionProvider: provider);
        var input = codec.Encode(new ActionFrame { ActionId = "orders.ping" }, EncodingTier.MsgPack);
        using var stream = new ScriptedDuplexStream(input);
        await server.ServeAsync(stream, EncodingTier.MsgPack);
        if (deny)
        {
            Assert.Equal(0, provider.Executions);
            var error = Assert.IsType<ErrorFrame>(codec.Decode(stream.Written));
            Assert.Equal(NpsStatusCodes.AuthForbidden, error.Status);
            Assert.Equal(NwpErrorCodes.AuthNidScopeViolation, error.Error);
            Assert.Equal("Action scope denied.", error.Message);
        }
        else
        {
            Assert.Equal(new[] { "authorize", "execute" }, provider.Order);
            Assert.Equal(1, provider.Executions);
            Assert.Same(provider.AdmittedContext, provider.ExecutedContext);
            Assert.Null(provider.AdmittedContext!.HttpContext);
            Assert.Equal((ulong)NPS.Core.Codecs.NpsFrameCodec.PeekHeader(input).PayloadLength, provider.AdmittedContext.WireInputBytes);
            Assert.IsType<CapsFrame>(codec.Decode(stream.Written));
        }
        Assert.Equal(1, provider.Admissions);
    }

    [Theory]
    [InlineData(EncodingTier.Json)]
    [InlineData(EncodingTier.MsgPack)]
    public async Task UnqualifiedNativeResultRouteRejectsBeforeHandlerVerifierOrCapture(EncodingTier tier)
    {
        var codec = MakeCodec();
        var captures = 0;
        var verifications = 0;
        var store = new ResultResourceStore(new() { IssuerNid = "urn:nps:node:fixture:native-results" }, (_, _) => true);
        var result = new ResultActionProvider(store, new Dictionary<string, ResultActionSource>
        {
            ["orders"] = new(_ => { }, (_, _, _) =>
            {
                captures++;
                return ValueTask.FromResult(new ResultSnapshot("source-1", "sha256:" + new string('a', 64),
                    JsonSerializer.SerializeToElement(new[] { new { id = "protected-row" } })));
            }),
        }, (_, _, _) =>
        {
            verifications++;
            return ValueTask.FromResult<ResultActionAdmission?>(new(new("tenant", "owner", "v1"), "sha256:" + new string('b', 64)));
        }, qualifiedHttpRouteEnabled: () => true);
        var options = new ActionNodeOptions { NodeId = "urn:nps:node:fixture:native-results", PathPrefix = "/native-results",
            Actions = new Dictionary<string, ActionSpec>() };
        result.ConfigureNode(options);
        var counted = new NativeResultCounter(result);
        var server = new NwpNativeNodeServer(codec, new() { ActionOptions = options }, actionProvider: counted);
        var frame = new ActionFrame { ActionId = "result.create", IdempotencyKey = Guid.NewGuid().ToString(),
            Params = JsonSerializer.SerializeToElement(new { source_id = "orders" }) };
        using var stream = new ScriptedDuplexStream(codec.Encode(frame, tier));
        await server.ServeAsync(stream, tier);
        Assert.Equal(0, counted.Executions);
        Assert.Equal(0, verifications);
        Assert.Equal(0, captures);
        var error = Assert.IsType<ErrorFrame>(codec.Decode(stream.Written));
        Assert.Equal(NpsStatusCodes.ClientConflict, error.Status);
        Assert.Equal(NwpErrorCodes.FeatureRequiredUnsupported, error.Error);
        Assert.DoesNotContain("protected-row", JsonSerializer.Serialize(error));
    }

    private sealed class NativeAdmissionProbe(bool deny) : IActionNodeProvider
    {
        public int Admissions { get; private set; }
        public int Executions { get; private set; }
        public List<string> Order { get; } = [];
        public ActionContext? AdmittedContext { get; private set; }
        public ActionContext? ExecutedContext { get; private set; }
        public Task AuthorizeAsync(ActionFrame frame, ActionContext context, CancellationToken ct = default)
        {
            Admissions++;
            AdmittedContext = context;
            Order.Add("authorize");
            if (deny) throw new ActionNodeException(403, NpsStatusCodes.AuthForbidden,
                NwpErrorCodes.AuthNidScopeViolation, "Action scope denied.");
            return Task.CompletedTask;
        }
        public Task<ActionExecutionResult> ExecuteAsync(ActionFrame frame, ActionContext context, CancellationToken ct = default)
        {
            Executions++;
            ExecutedContext = context;
            Order.Add("execute");
            return Task.FromResult(new ActionExecutionResult { Result = JsonSerializer.SerializeToElement(new { status = "ok" }) });
        }
    }

    private sealed class NativeResultCounter(ResultActionProvider inner) : IActionNodeProvider
    {
        public int Executions { get; private set; }
        public Task AuthorizeAsync(ActionFrame frame, ActionContext context, CancellationToken ct = default) => inner.AuthorizeAsync(frame, context, ct);
        public Task<ActionExecutionResult> ExecuteAsync(ActionFrame frame, ActionContext context, CancellationToken ct = default)
        { Executions++; return inner.ExecuteAsync(frame, context, ct); }
    }
}
