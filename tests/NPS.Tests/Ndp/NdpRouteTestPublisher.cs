// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPS.NDP.Discovery;
using NPS.NDP.Registry;
using NSec.Cryptography;

namespace NPS.Tests.Ndp;

internal sealed class NdpRouteTestPublisher(string nid) : IDisposable
{
    private readonly Key _key = Key.Create(SignatureAlgorithm.Ed25519);
    public string Nid { get; } = nid;
    public string PublicKey => Encode(_key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    public NdpRouteAuthority Authority(DateTimeOffset now, ulong epoch = 1, Uri? endpoint = null) =>
        new(Nid, PublicKey, "orders-cluster", epoch, endpoint ?? new("https://localhost:8443/gateway"),
            new string('A', 64), "policy-1", now.AddHours(1));

    public byte[] Announce(DateTimeOffset now, ulong sequence = 1, ulong epoch = 1, uint ttl = 120,
        string? health = null, string? lastSeen = null, Uri? endpoint = null, string[]? capabilities = null,
        string cluster = "orders-cluster", bool badSignature = false)
    {
        var address = endpoint ?? new Uri("https://localhost:8443/gateway");
        var body = JsonSerializer.SerializeToNode(new
        {
            frame = "0x30", nid = Nid, node_type = "action", addresses = new[] { new { host = address.Host, port = address.Port, protocol = "https" } },
            capabilities = capabilities ?? ["data-results"], ttl, timestamp = now.ToString("O"), graph_seq = sequence,
            cluster_anchor = cluster, cluster_epoch = epoch, node_roles = new[] { "anchor", "action" }, health, last_seen = lastSeen,
        })!.AsObject();
        var canonical = NdpAnnounceCanonicalizer.CanonicalJson(JsonSerializer.SerializeToElement(body));
        body["signature"] = badSignature ? "ed25519:invalid" : Encode(SignatureAlgorithm.Ed25519.Sign(_key, Encoding.UTF8.GetBytes(canonical)));
        return Encoding.UTF8.GetBytes(body.ToJsonString());
    }

    public static byte[] Advisory(byte[] bytes, string? health = null, DateTimeOffset? lastSeen = null)
    {
        var value = JsonNode.Parse(bytes)!.AsObject();
        value["health"] = health;
        value["last_seen"] = lastSeen?.ToString("O");
        return Encoding.UTF8.GetBytes(value.ToJsonString());
    }

    private static string Encode(byte[] value) => "ed25519:" + Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public void Dispose() => _key.Dispose();
}

internal sealed class NdpRouteTestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
    private long _ticks;
    public override DateTimeOffset GetUtcNow() => _now;
    public override long GetTimestamp() => _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan elapsed, TimeSpan? wall = null) { _ticks += elapsed.Ticks; _now += wall ?? elapsed; }
}
