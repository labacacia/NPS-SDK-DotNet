// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NPS.Core.Alpha20;
using NPS.NWP.Nwm;

namespace NPS.NWP.ActionNode;

/// <summary>Validated immutable manifest snapshot used for HTTP disclosure/version responses.</summary>
public sealed record ActionNodeManifestSnapshot(NeuralWebManifest Manifest, string Json, uint Version);

/// <summary>
/// Bounded explicit manifest publication for an already-qualified Action route. The host
/// supplies immutable snapshots and persists the revision across restart; this class rejects
/// observed rollback or content changes under the same version. It does not enable features
/// or prove an all-backend guarantee from caller metadata.
/// </summary>
public sealed class ActionNodeManifestSource(Func<NeuralWebManifest> current)
{
    private readonly object _gate = new();
    private uint _version;
    private string? _hash;

    /// <summary>Read current metadata, validating bounds, node binding, feature syntax and revision.</summary>
    public ActionNodeManifestSnapshot Read(string expectedNodeId)
    {
        lock (_gate)
        {
            try
            {
                var manifest = current();
                if (manifest.NodeId != expectedNodeId || manifest.ManifestVersion is not (> 0)
                    || manifest.Actions is null || manifest.NodeType is not ("action" or "complex" or "bridge")) throw Unavailable();
                if (manifest.Profiles?.FeatureNegotiation is { } profile)
                {
                    if (Encoding.UTF8.GetByteCount(profile.GetRawText()) > 16384
                        || profile.ValueKind != JsonValueKind.Object
                        || profile.EnumerateObject().Count() != 2
                        || !profile.TryGetProperty("version", out var revision) || revision.GetRawText() != "1"
                        || !profile.TryGetProperty("feature_offer", out var offer)) throw Unavailable();
                    var block = "{\"feature_negotiation\":1,\"feature_offer\":" + offer.GetRawText() + "}";
                    _ = FeatureWireParser.ParseOffer(block);
                }
                var json = JsonSerializer.Serialize(manifest, ActionNodeMiddleware.Json);
                if (Encoding.UTF8.GetByteCount(json) > 65536) throw Unavailable();
                var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
                var version = manifest.ManifestVersion.Value;
                if (version < _version || version == _version && hash != _hash) throw Unavailable();
                _version = version;
                _hash = hash;
                return new(manifest, json, version);
            }
            catch (ActionNodeException) { throw; }
            catch { throw Unavailable(); }
        }
    }

    private static ActionNodeException Unavailable() => new(503, "NPS-SERVER-UNAVAILABLE",
        "NWP-NODE-UNAVAILABLE", "Manifest unavailable.");
}
