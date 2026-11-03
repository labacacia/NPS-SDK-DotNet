// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core.Frames;
using NPS.NWP.Frames;

namespace NPS.NWP.Bridge;

/// <summary>Dispatch delegate used by inbound Bridge server adapters to reach a co-hosted node.</summary>
public delegate Task<IFrame> BridgeServerActionDispatcher(
    ActionFrame frame,
    CancellationToken cancellationToken = default);

/// <summary>Query delegate used by inbound Bridge server adapters to reach a co-hosted Memory Node.</summary>
public delegate Task<IFrame> BridgeServerQueryDispatcher(
    QueryFrame frame,
    CancellationToken cancellationToken = default);

/// <summary>Action exposed by inbound Bridge server adapters as an MCP tool / A2A skill.</summary>
public sealed record BridgeServerAction
{
    /// <summary>NPS action identifier dispatched to the local node.</summary>
    public required string ActionId { get; init; }

    /// <summary>Human-readable display name for A2A AgentCard entries.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Short action/tool description.</summary>
    public string? Description { get; init; }

    /// <summary>JSON Schema describing input arguments.</summary>
    public JsonElement? InputSchema { get; init; }

    /// <summary>Whether generated <see cref="ActionFrame"/> values should request async execution.</summary>
    public bool Async { get; init; }

    /// <summary>Optional A2A skill tags.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Effective display name for A2A AgentCard skills.</summary>
    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(DisplayName) ? ActionId : DisplayName!;
}

/// <summary>
/// Transport-independent configuration of a Bridge Node's inbound surface
/// (NWP §2.1 inbound profile, NPS-CR-0010).
///
/// <para>
/// This type deliberately knows nothing about ASP.NET: the inbound protocol servers
/// (<see cref="McpInboundServer"/>, <see cref="A2aInboundServer"/>) are written against it alone.
/// The hosting concerns — paths, an <c>HttpContext</c>-bound auth verifier, body limits — live in
/// <c>BridgeServerOptions</c>, which derives from this. Keeping the servers off the derived type
/// means they never touch <c>HttpContext</c>, so they can be driven directly (e.g. over stdio, or
/// from a test) without a web host.
/// </para>
/// </summary>
public class BridgeInboundOptions
{
    /// <summary>Identifier of the co-hosted node. Namespaces its MCP resource URIs and tool names.</summary>
    public string NodeId { get; set; } = "nps-bridge-server";

    /// <summary>Server name returned by MCP <c>initialize</c> and the A2A AgentCard.</summary>
    public string ServerName { get; set; } = "nps-bridge-server";

    /// <summary>Server version returned by MCP <c>initialize</c> and the A2A AgentCard.</summary>
    public string ServerVersion { get; set; } = "1.0.0-alpha.20";

    /// <summary>Server description returned by the A2A AgentCard.</summary>
    public string? Description { get; set; } = "NPS Bridge Node inbound surface.";

    /// <summary>
    /// Role of the co-hosted node projected by the in-process backend. Defaults to
    /// <see cref="NwpNodeRole.Action"/> — the only shape the pre-CR-0010 Bridge server supported.
    /// Set to <see cref="NwpNodeRole.Complex"/> or <see cref="NwpNodeRole.Memory"/> together with
    /// <see cref="QueryAsync"/> to project resources as well as tools.
    /// </summary>
    public NwpNodeRole NodeRole { get; set; } = NwpNodeRole.Action;

    /// <summary>Actions exposed as MCP tools and A2A skills.</summary>
    public IList<BridgeServerAction> Actions { get; } = new List<BridgeServerAction>();

    /// <summary>Local NPS action dispatcher. Required for a co-hosted invokable node.</summary>
    public BridgeServerActionDispatcher? DispatchAsync { get; set; }

    /// <summary>
    /// Local NPS query dispatcher. Set this to project a co-hosted Memory / Complex Node onto the
    /// inbound protocol's read surface (MCP <c>resources/*</c>). Leaving it unset is conformant —
    /// the resource methods are still served, over an empty set (NWP §16.1.2).
    /// </summary>
    public BridgeServerQueryDispatcher? QueryAsync { get; set; }

    /// <summary>
    /// Remote NWP nodes this Bridge fronts over HTTP — the standalone-gateway shape. May be
    /// combined with a co-hosted node.
    /// </summary>
    public IList<NwpUpstream> Upstreams { get; } = new List<NwpUpstream>();

    /// <summary>
    /// Max rows a single MCP <c>resources/read</c> may pull from a Memory Node. NWP's own page cap
    /// is 1000; mirror it conservatively here.
    /// </summary>
    public uint ResourceReadLimit { get; set; } = 100;

    /// <summary>
    /// External protocols this Bridge Node <b>serves inbound</b>, announced as NDP
    /// <c>bridge_inbound_protocols</c> (NPS-4 §3.1). A request for a protocol absent from this set
    /// MUST be rejected with <c>NWP-BRIDGE-DIRECTION-UNSUPPORTED</c> (NWP §16.1.2 MUST-5).
    /// </summary>
    public IList<string> InboundProtocols { get; } = new List<string> { "mcp", "a2a" };

    /// <summary>
    /// Whether this Bridge Node requires an authenticated caller. Advertised on the A2A AgentCard,
    /// so it is part of the inbound protocol surface, not merely host configuration — the verifier
    /// that enforces it lives in the hosting layer, but the declaration belongs here.
    /// </summary>
    public bool RequireAuth { get; set; } = true;

    /// <summary>Add an exposed local action and return these options for chaining.</summary>
    public BridgeInboundOptions AddAction(
        string actionId,
        string? description = null,
        JsonElement? inputSchema = null,
        bool async = false,
        string? displayName = null,
        IReadOnlyList<string>? tags = null)
    {
        Actions.Add(new BridgeServerAction
        {
            ActionId = actionId,
            DisplayName = displayName,
            Description = description,
            InputSchema = inputSchema,
            Async = async,
            Tags = tags,
        });
        return this;
    }

    /// <summary>Whether this Bridge Node declares <paramref name="protocol"/> on its inbound surface.</summary>
    public bool ServesInbound(string protocol) =>
        InboundProtocols.Any(p => string.Equals(p, protocol, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Builds the <see cref="INwpBackend"/> set an inbound Bridge server serves. Both shapes are
/// supported and may be combined: a co-hosted node reached in-process, and any number of remote
/// nodes reached over HTTP. (NPS-CR-0010)
/// </summary>
public static class BridgeServerBackends
{
    /// <summary>Materialise the backends declared by <paramref name="options"/>.</summary>
    /// <param name="options">Inbound Bridge options.</param>
    /// <param name="httpClient">
    /// Client used for HTTP upstreams. Required when <see cref="BridgeInboundOptions.Upstreams"/>
    /// is non-empty.
    /// </param>
    public static IReadOnlyList<INwpBackend> Create(BridgeInboundOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var backends = new List<INwpBackend>();

        // Note the Actions check: a deployment that declares actions but forgets the dispatcher
        // still gets an in-process backend, so its tools appear in tools/list and a call fails
        // loudly with NWP-BRIDGE-SERVER-DISPATCHER-MISSING. Omitting the backend instead would
        // make a misconfiguration look like "this node simply exposes nothing".
        if (options.DispatchAsync is not null || options.QueryAsync is not null || options.Actions.Count > 0)
        {
            backends.Add(new InProcessNwpBackend(
                new NwpNodeDescriptor
                {
                    Name = options.NodeId,
                    Role = options.NodeRole,
                    DisplayName = options.ServerName,
                    Description = options.Description,
                },
                options.Actions.Select(a => new NwpActionDescriptor
                {
                    ActionId = a.ActionId,
                    Description = a.Description,
                    InputSchema = a.InputSchema,
                    Async = a.Async,
                    Tags = a.Tags,
                }).ToArray(),
                options.DispatchAsync is null
                    ? null
                    : (frame, ct) => options.DispatchAsync(frame, ct),
                options.QueryAsync is null
                    ? null
                    : (frame, ct) => options.QueryAsync(frame, ct)));
        }

        if (options.Upstreams.Count > 0)
        {
            if (httpClient is null)
            {
                throw new InvalidOperationException(
                    "BridgeInboundOptions.Upstreams is non-empty but no HttpClient was supplied. " +
                    "An HTTP-fronted Bridge Node needs a client to reach its upstream nodes.");
            }

            foreach (var upstream in options.Upstreams)
                backends.Add(new HttpNwpBackend(httpClient, upstream));
        }

        return backends;
    }
}
