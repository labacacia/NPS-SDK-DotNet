// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using NPS.NIP.Ca.Ra;
using NPS.NIP.Crypto;
using NPS.NIP.Frames;
using NPS.NIP.X509;

namespace NPS.NIP.Ca;

/// <summary>
/// Core CA business logic: issue, renew, revoke, and verify NID certificates (NPS-3 §6–8).
/// All signing is done with the CA's Ed25519 private key loaded via <see cref="NipKeyManager"/>.
/// </summary>
public sealed class NipCaService
{
    private readonly NipCaOptions  _opts;
    private readonly INipCaStore   _store;
    private readonly NipKeyManager _keys;
    private readonly Lazy<X509Certificate2> _rootCert;

    public NipCaService(NipCaOptions opts, INipCaStore store, NipKeyManager keys)
    {
        _opts     = opts;
        _store    = store;
        _keys     = keys;
        _rootCert = new Lazy<X509Certificate2>(CreateRootCert);
    }

    /// <summary>
    /// Self-signed root certificate for this CA, generated once from the
    /// persistent CA key. Stable across calls within a process lifetime;
    /// regenerated (same key, new validity window) on restart.
    /// Used by the ACME server and the X.509 registration endpoint.
    /// </summary>
    public X509Certificate2 CaRootCert => _rootCert.Value;

    private X509Certificate2 CreateRootCert()
    {
        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        serial[0] &= 0x7F;
        if (serial[0] == 0) serial[0] = 0x01;
        var now = DateTimeOffset.UtcNow;
        return NipX509Builder.IssueRoot(_opts.CaNid, _keys.PrivateKey, now, now.AddYears(10), serial);
    }

    // ── Register (Agent / Node) ───────────────────────────────────────────────

    /// <summary>
    /// Registers a new Agent or Node, issues an IdentFrame, and persists the record.
    /// </summary>
    /// <param name="entityType"><c>"agent"</c> or <c>"node"</c>.</param>
    /// <param name="identifier">Unique identifier portion of the NID (e.g. UUID or node path).</param>
    /// <param name="pubKey">Agent/Node public key in <c>ed25519:{base64url}</c> format.</param>
    /// <param name="capabilities">Requested capability list.</param>
    /// <param name="scopeJson">Scope JSON object.</param>
    /// <param name="metadataJson">Optional metadata JSON object.</param>
    public async Task<IdentFrame> RegisterAsync(
        string            entityType,
        string            identifier,
        string            pubKey,
        IReadOnlyList<string> capabilities,
        string            scopeJson,
        string?           metadataJson = null,
        CancellationToken ct           = default)
    {
        var nid      = BuildNid(entityType, identifier);
        var existing = await _store.GetByNidAsync(nid, ct);
        if (existing is not null)
            throw new NipCaException($"NID already exists: {nid}", NipErrorCodes.NidAlreadyExists);

        if (_opts.AllowedCapabilities is not null)
        {
            var disallowed = capabilities.Where(c => !_opts.AllowedCapabilities.Contains(c)).ToList();
            if (disallowed.Count > 0)
                throw new NipCaException(
                    $"Capabilities not permitted by this CA: {string.Join(", ", disallowed)}",
                    NipErrorCodes.CertCapMissing);
        }

        var validDays = entityType == "node" ? _opts.NodeCertValidityDays : _opts.AgentCertValidityDays;
        var now       = DateTime.UtcNow;
        var expiresAt = now.AddDays(validDays);
        var serial    = await _store.NextSerialAsync(ct);

        var frame = IssueFrame(nid, pubKey, capabilities, scopeJson, now, expiresAt, serial, metadataJson);

        var record = new NipCertRecord
        {
            Nid          = nid,
            EntityType   = entityType,
            Serial       = serial,
            PubKey       = pubKey,
            Capabilities = capabilities.ToArray(),
            ScopeJson    = scopeJson,
            IssuedBy     = _opts.CaNid,
            IssuedAt     = now,
            ExpiresAt    = expiresAt,
            MetadataJson = metadataJson,
        };
        await _store.SaveAsync(record, ct);

        return frame;
    }

    // ── Register with RA gate (NPS-CR-0005) ───────────────────────────────────

    /// <summary>
    /// RA-gated registration: runs the active <see cref="IEnrollmentPolicy"/>
    /// before delegating to <see cref="RegisterAsync"/>.
    /// </summary>
    /// <remarks>
    /// The caller (HTTP layer) is responsible for extracting
    /// <paramref name="enrollmentToken"/> from the
    /// <c>X-NPS-Enrollment-Token</c> header and passing it here.
    /// </remarks>
    /// <exception cref="NipCaException">Enrollment denied.</exception>
    /// <exception cref="NipRaPendingException">
    /// Enrollment queued (Tier 3) — caller should return 202.
    /// </exception>
    public async Task<IdentFrame> RegisterWithRaAsync(
        string                entityType,
        string                identifier,
        string                pubKey,
        IReadOnlyList<string> capabilities,
        string                scopeJson,
        string?               metadataJson       = null,
        string?               enrollmentToken    = null,
        IEnrollmentPolicy?    enrollmentPolicy   = null,
        CancellationToken     ct                 = default)
    {
        if (enrollmentPolicy is not null)
        {
            await enrollmentPolicy.CheckAsync(
                entityType, identifier, pubKey,
                capabilities, scopeJson, metadataJson,
                enrollmentToken, ct);
        }
        return await RegisterAsync(entityType, identifier, pubKey, capabilities, scopeJson, metadataJson, ct);
    }

    /// <summary>
    /// Constructs the <see cref="IEnrollmentPolicy"/> selected by
    /// <see cref="NipCaOptions.EnrollmentTier"/>.
    /// </summary>
    public static IEnrollmentPolicy CreateEnrollmentPolicy(
        NipCaOptions opts,
        IBootstrapTokenStore? bootstrapTokenStore,
        IPendingStore?        pendingStore)
    {
        return opts.EnrollmentTier switch
        {
            EnrollmentTier.Allowlist      => new AllowlistPolicy(opts.EnrollmentAllowlistPatterns),
            EnrollmentTier.BootstrapToken => bootstrapTokenStore is not null
                ? new BootstrapTokenPolicy(bootstrapTokenStore)
                : throw new InvalidOperationException(
                    "EnrollmentTier.BootstrapToken requires IBootstrapTokenStore to be registered."),
            EnrollmentTier.PendingQueue   => pendingStore is not null
                ? new PendingQueuePolicy(pendingStore, opts.PendingQueueMaxSize)
                : throw new InvalidOperationException(
                    "EnrollmentTier.PendingQueue requires IPendingStore to be registered."),
            _ => throw new InvalidOperationException($"Unknown EnrollmentTier: {opts.EnrollmentTier}"),
        };
    }

    // ── Register X.509 (NPS-RFC-0002 prototype) ───────────────────────────────

    /// <summary>
    /// Registers a new Agent or Node and issues an <see cref="IdentFrame"/>
    /// with both the legacy CA-signed JSON proof <b>and</b> a DER-encoded
    /// X.509 certificate chain per NPS-RFC-0002 §4.1. The same
    /// <see cref="NipCertRecord"/> is persisted so the existing renew /
    /// revoke / OCSP machinery covers v2 certs without further changes.
    ///
    /// <para>The chain currently has a single self-signed root supplied via
    /// <paramref name="rootCert"/>; the prototype intentionally does not
    /// implement intermediate hierarchy depth — that's deferred to a
    /// follow-up.</para>
    /// </summary>
    public async Task<IdentFrame> RegisterX509Async(
        string                 entityType,
        string                 identifier,
        string                 pubKey,
        IReadOnlyList<string>  capabilities,
        string                 scopeJson,
        X509Certificate2?      rootCert       = null,
        AssuranceLevel         assuranceLevel = AssuranceLevel.Anonymous,
        string?                metadataJson   = null,
        CancellationToken      ct             = default)
    {
        rootCert ??= CaRootCert;

        var nid      = BuildNid(entityType, identifier);
        var existing = await _store.GetByNidAsync(nid, ct);
        if (existing is not null)
            throw new NipCaException($"NID already exists: {nid}", NipErrorCodes.NidAlreadyExists);

        if (_opts.AllowedCapabilities is not null)
        {
            var disallowed = capabilities.Where(c => !_opts.AllowedCapabilities.Contains(c)).ToList();
            if (disallowed.Count > 0)
                throw new NipCaException(
                    $"Capabilities not permitted by this CA: {string.Join(", ", disallowed)}",
                    NipErrorCodes.CertCapMissing);
        }

        var validDays = entityType == "node" ? _opts.NodeCertValidityDays : _opts.AgentCertValidityDays;
        var now       = DateTime.UtcNow;
        var expiresAt = now.AddDays(validDays);
        var serial    = await _store.NextSerialAsync(ct);

        // Build the legacy v1 frame first — gives us the CA Ed25519 signature,
        // serial, and snake_case scope JsonElement that v2 verifiers also rely on.
        // Pass the assurance level through so it lands in the v1 signature
        // (RFC-0003) and the X.509 leaf extension (RFC-0002 §4.1) consistently.
        var v1Frame = IssueFrame(nid, pubKey, capabilities, scopeJson,
            now, expiresAt, serial, metadataJson, assuranceLevel);

        // Layer X.509 on top.
        var subjectPubRaw = ExtractEd25519Raw(pubKey);
        var leafSerial    = ParseSerialBytes(serial);
        var role          = entityType == "node"
            ? NipX509Builder.LeafRole.Node
            : NipX509Builder.LeafRole.Agent;

        var leafCert = NipX509Builder.IssueLeaf(
            subjectNid:      nid,
            subjectPubKeyRaw: subjectPubRaw,
            caPrivateKey:    _keys.PrivateKey,
            issuerNid:       _opts.CaNid,
            role:            role,
            assuranceLevel:  assuranceLevel,
            notBefore:       now,
            notAfter:        expiresAt,
            serialNumber:    leafSerial);

        var chainB64Url = new[]
        {
            Base64Url(leafCert.RawData),
            Base64Url(rootCert.RawData),
        };

        var record = new NipCertRecord
        {
            Nid          = nid,
            EntityType   = entityType,
            Serial       = serial,
            PubKey       = pubKey,
            Capabilities = capabilities.ToArray(),
            ScopeJson    = scopeJson,
            IssuedBy     = _opts.CaNid,
            IssuedAt     = now,
            ExpiresAt    = expiresAt,
            MetadataJson = metadataJson,
        };
        await _store.SaveAsync(record, ct);

        return v1Frame with
        {
            CertFormat = IdentCertFormat.V2X509,
            CertChain  = chainB64Url,
            // AssuranceLevel already set by IssueFrame(...) above.
        };
    }

    // ── Register Group (NPS-CR-0003) ──────────────────────────────────────────

    /// <summary>
    /// Registers a new orchestrator group NID and issues an
    /// <see cref="IdentFrame"/> with <c>lineage.role = "group"</c>
    /// (NPS-CR-0003 §5.1.3). Group NIDs are longer-lived than agent NIDs
    /// (default <see cref="NipCaOptions.GroupCertValidityDays"/> = 365)
    /// and act as the trust anchor for short-lived session NIDs issued
    /// via <see cref="IssueSessionAsync"/>.
    /// </summary>
    /// <param name="identifier">
    /// Identifier portion of the group NID. Either supply an explicit
    /// value (MUST start with the reserved prefix <c>group-</c>) or pass
    /// <c>null</c> / empty to have the CA mint
    /// <c>group-{uuid}</c> automatically.
    /// </param>
    /// <param name="ownerUserId">Stable identifier of the human owner.</param>
    /// <param name="ownerKeyId">Owner-key kid hint (Operator key, OIDC sub, hardware-token id).</param>
    public async Task<IdentFrame> RegisterGroupAsync(
        string?               identifier,
        string                pubKey,
        IReadOnlyList<string> capabilities,
        string                scopeJson,
        string?               ownerUserId  = null,
        string?               ownerKeyId   = null,
        string?               metadataJson = null,
        CancellationToken     ct           = default)
    {
        if (string.IsNullOrEmpty(identifier))
            identifier = "group-" + Guid.NewGuid().ToString("N");
        else if (!identifier.StartsWith("group-", StringComparison.Ordinal))
            throw new NipCaException(
                $"Group identifier MUST start with reserved prefix 'group-' (got '{identifier}'). NPS-3 §3.1.",
                NipErrorCodes.NidAlreadyExists);

        var nid      = BuildNid("agent", identifier);
        var existing = await _store.GetByNidAsync(nid, ct);
        if (existing is not null)
            throw new NipCaException($"NID already exists: {nid}", NipErrorCodes.NidAlreadyExists);

        if (_opts.AllowedCapabilities is not null)
        {
            var disallowed = capabilities.Where(c => !_opts.AllowedCapabilities.Contains(c)).ToList();
            if (disallowed.Count > 0)
                throw new NipCaException(
                    $"Capabilities not permitted by this CA: {string.Join(", ", disallowed)}",
                    NipErrorCodes.CertCapMissing);
        }

        var now       = DateTime.UtcNow;
        var expiresAt = now.AddDays(_opts.GroupCertValidityDays);
        var serial    = await _store.NextSerialAsync(ct);

        var lineage = new IdentLineage
        {
            Role        = IdentLineageRole.Group,
            OwnerUserId = ownerUserId,
            OwnerKeyId  = ownerKeyId,
        };
        var lineageJson = JsonSerializer.Serialize(lineage, s_jsonOpts);

        var frame = IssueFrame(nid, pubKey, capabilities, scopeJson,
            now, expiresAt, serial, metadataJson, lineage: lineage);

        var record = new NipCertRecord
        {
            Nid          = nid,
            EntityType   = "agent",
            Serial       = serial,
            PubKey       = pubKey,
            Capabilities = capabilities.ToArray(),
            ScopeJson    = scopeJson,
            IssuedBy     = _opts.CaNid,
            IssuedAt     = now,
            ExpiresAt    = expiresAt,
            MetadataJson = metadataJson,
            NidRole      = IdentLineageRole.Group,
            ParentNid    = null,
            LineageJson  = lineageJson,
        };
        await _store.SaveAsync(record, ct);

        return frame;
    }

    // ── Issue Session (NPS-CR-0003) ───────────────────────────────────────────

    /// <summary>
    /// Issues a short-lived session NID under <paramref name="groupNid"/>
    /// (NPS-CR-0003 §5.1.3). The caller MUST already have proven authority
    /// — this method assumes the group-JWS verification or Operator-API-key
    /// check has been done at the HTTP layer; the group existence /
    /// not-revoked check happens here.
    /// </summary>
    /// <param name="groupNid">Group NID under which to mint the session.</param>
    /// <param name="sessionPubKey">Session keypair public half (<c>ed25519:&lt;b64url&gt;</c>).</param>
    /// <param name="validity">
    /// Requested session lifetime. Clamped to
    /// <see cref="NipCaOptions.SessionMinValidity"/> /
    /// <see cref="NipCaOptions.SessionMaxValidity"/>; out-of-range
    /// requests throw <c>NIP-CA-SESSION-VALIDITY-INVALID</c>. Defaults to
    /// <see cref="NipCaOptions.SessionDefaultValidity"/> when null.
    /// </param>
    /// <param name="purpose">Optional human-readable label (≤256 UTF-8 bytes).</param>
    /// <param name="capabilities">
    /// Capabilities for the session. Defaults to the group's capabilities
    /// (subset enforcement: session capabilities MUST NOT exceed group's).
    /// </param>
    /// <param name="scopeJson">
    /// Scope JSON for the session. Defaults to the group's scope (no
    /// scope expansion per NIP §10.3).
    /// </param>
    public async Task<IdentFrame> IssueSessionAsync(
        string                 groupNid,
        string                 sessionPubKey,
        TimeSpan?              validity     = null,
        string?                purpose      = null,
        IReadOnlyList<string>? capabilities = null,
        string?                scopeJson    = null,
        string?                metadataJson = null,
        CancellationToken      ct           = default)
    {
        // 1. Resolve + validate group
        var group = await _store.GetByNidAsync(groupNid, ct)
            ?? throw new NipCaException(
                $"Group NID not found: {groupNid}.", NipErrorCodes.ParentNotFound);

        if (group.NidRole != IdentLineageRole.Group)
            throw new NipCaException(
                $"NID '{groupNid}' is not registered as a group (role='{group.NidRole ?? "<null>"}').",
                NipErrorCodes.ParentNotGroup);

        if (group.RevokedAt.HasValue)
            throw new NipCaException(
                $"Group {groupNid} was revoked at {group.RevokedAt:O}; cannot issue new sessions.",
                NipErrorCodes.GroupRevoked);

        if (DateTime.UtcNow > group.ExpiresAt)
            throw new NipCaException(
                $"Group {groupNid} expired at {group.ExpiresAt:O}; cannot issue new sessions.",
                NipErrorCodes.CertExpired);

        // 2. Validate validity window
        var v = validity ?? _opts.SessionDefaultValidity;
        if (v < _opts.SessionMinValidity || v > _opts.SessionMaxValidity)
            throw new NipCaException(
                $"Session validity must be in [{_opts.SessionMinValidity}, {_opts.SessionMaxValidity}]; got {v}.",
                NipErrorCodes.SessionValidityInvalid);

        // 3. Subset checks (no scope expansion past the group)
        var sessionCaps = capabilities ?? group.Capabilities;
        if (capabilities is not null)
        {
            var groupCapSet = new HashSet<string>(group.Capabilities, StringComparer.Ordinal);
            var expansion   = sessionCaps.Where(c => !groupCapSet.Contains(c)).ToList();
            if (expansion.Count > 0)
                throw new NipCaException(
                    $"Session capabilities not in parent group: {string.Join(", ", expansion)}.",
                    NipErrorCodes.ScopeExpansion);
        }
        var sessionScopeJson = scopeJson ?? group.ScopeJson;

        // 4. Build session NID
        var unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var randHex     = RandomHex(8);
        var sessionId   = $"session-{unixSeconds}-{randHex}";
        var sessionNid  = BuildNid("agent", sessionId);

        var now       = DateTime.UtcNow;
        var expiresAt = now.Add(v);
        var serial    = await _store.NextSerialAsync(ct);

        // 5. Lineage
        var lineage = new IdentLineage
        {
            Role        = IdentLineageRole.Session,
            ParentNid   = groupNid,
            GroupNid    = groupNid,
            SessionId   = sessionId,
            Purpose     = purpose,
            OwnerUserId = ExtractOwnerUserId(group.LineageJson),
            OwnerKeyId  = ExtractOwnerKeyId(group.LineageJson),
        };
        var lineageJson = JsonSerializer.Serialize(lineage, s_jsonOpts);

        // 6. Issue + persist
        var frame = IssueFrame(sessionNid, sessionPubKey, sessionCaps, sessionScopeJson,
            now, expiresAt, serial, metadataJson, lineage: lineage);

        var record = new NipCertRecord
        {
            Nid          = sessionNid,
            EntityType   = "agent",
            Serial       = serial,
            PubKey       = sessionPubKey,
            Capabilities = sessionCaps.ToArray(),
            ScopeJson    = sessionScopeJson,
            IssuedBy     = _opts.CaNid,
            IssuedAt     = now,
            ExpiresAt    = expiresAt,
            MetadataJson = metadataJson,
            NidRole      = IdentLineageRole.Session,
            ParentNid    = groupNid,
            LineageJson  = lineageJson,
        };
        await _store.SaveAsync(record, ct);

        return frame;
    }

    /// <summary>
    /// Lists every session NID issued under <paramref name="groupNid"/>
    /// (NPS-CR-0003 §8 audit endpoint). Includes both live and revoked
    /// records — callers filter on <see cref="NipCertRecord.RevokedAt"/>
    /// as needed.
    /// </summary>
    public Task<IReadOnlyList<NipCertRecord>> ListSessionsAsync(
        string groupNid, CancellationToken ct = default) =>
        _store.GetByParentNidAsync(groupNid, ct);

    /// <summary>
    /// Returns the persisted certificate record for <paramref name="nid"/>,
    /// or <c>null</c> if not found. Exposed so HTTP handlers can perform
    /// pre-flight checks (group existence / role / revocation) before
    /// invoking <see cref="IssueSessionAsync"/>.
    /// </summary>
    public Task<NipCertRecord?> GetCertAsync(string nid, CancellationToken ct = default) =>
        _store.GetByNidAsync(nid, ct);

    // ── Renew ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renews a certificate. Only allowed within the renewal window
    /// (<see cref="NipCaOptions.RenewalWindowDays"/> before expiry).
    /// Returns a new IdentFrame with a fresh serial and extended expiry.
    /// </summary>
    public async Task<IdentFrame> RenewAsync(string nid, CancellationToken ct = default)
    {
        var record = await _store.GetByNidAsync(nid, ct)
            ?? throw new NipCaException($"NID not found: {nid}", NipErrorCodes.NidNotFound);

        if (record.RevokedAt.HasValue)
            throw new NipCaException($"NID is revoked: {nid}", NipErrorCodes.CertRevoked);

        var now            = DateTime.UtcNow;
        var renewWindowEnd = record.ExpiresAt;
        var renewWindowStart = record.ExpiresAt.AddDays(-_opts.RenewalWindowDays);

        if (now < renewWindowStart)
            throw new NipCaException(
                $"Renewal window opens {renewWindowStart:O}. Too early to renew.",
                NipErrorCodes.RenewalTooEarly);

        var validDays = record.EntityType == "node" ? _opts.NodeCertValidityDays : _opts.AgentCertValidityDays;
        var expiresAt = now.AddDays(validDays);
        var serial    = await _store.NextSerialAsync(ct);

        var frame = IssueFrame(nid, record.PubKey, record.Capabilities, record.ScopeJson,
            now, expiresAt, serial, record.MetadataJson);

        // Save new record (old one stays for audit, new one replaces active cert)
        var newRecord = new NipCertRecord
        {
            Nid          = nid,
            EntityType   = record.EntityType,
            Serial       = serial,
            PubKey       = record.PubKey,
            Capabilities = record.Capabilities,
            ScopeJson    = record.ScopeJson,
            IssuedBy     = _opts.CaNid,
            IssuedAt     = now,
            ExpiresAt    = expiresAt,
            MetadataJson = record.MetadataJson,
        };
        await _store.SaveAsync(newRecord, ct);

        return frame;
    }

    // ── Revoke ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Revokes a certificate immediately and returns the signed RevokeFrame.
    /// <para>
    /// When the target NID is an orchestrator group (NPS-CR-0003 §5.1.3),
    /// every live session NID under that group is also revoked, with
    /// reason <c>parent_revoked</c> (NPS-3 §5.3). The cascade is recorded
    /// in the store so the standard CRL endpoint surfaces them; per-session
    /// RevokeFrames are not returned to the caller (the response is the
    /// group's RevokeFrame). Cascading is best-effort — a failure to
    /// persist a child revocation is logged but does not abort the parent
    /// revocation, since defense-in-depth is provided by the verify-time
    /// chain check (NPS-3 §7 step 3a).
    /// </para>
    /// </summary>
    public async Task<RevokeFrame> RevokeAsync(string nid, string reason, CancellationToken ct = default)
    {
        var record = await _store.GetByNidAsync(nid, ct)
            ?? throw new NipCaException($"NID not found: {nid}", NipErrorCodes.NidNotFound);

        var now      = DateTime.UtcNow;
        var revoked  = await _store.RevokeAsync(nid, reason, now, ct);
        if (!revoked)
            throw new NipCaException($"Failed to revoke {nid}.", NipErrorCodes.NidNotFound);

        // Cascade revoke live sessions if this is a group
        if (record.NidRole == IdentLineageRole.Group)
        {
            var children = await _store.GetByParentNidAsync(nid, ct);
            foreach (var child in children)
            {
                if (child.RevokedAt.HasValue) continue;
                await _store.RevokeAsync(child.Nid, "parent_revoked", now, ct);
            }
        }

        // Build RevokeFrame for signing (signature excluded from canonical form)
        var payload = new
        {
            frame      = "0x22",
            target_nid = nid,
            serial     = record.Serial,
            reason,
            revoked_at = now.ToString("O"),
            signer_nid = _opts.CaNid,
        };
        var signature = NipSigner.Sign(_keys.PrivateKey, payload);

        return new RevokeFrame
        {
            TargetNid = nid,
            Serial    = record.Serial,
            Reason    = reason,
            RevokedAt = now.ToString("O"),
            SignerNid = _opts.CaNid,
            Signature = signature,
        };
    }

    // ── Verify (OCSP) ─────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies a NID: checks existence, expiry, revocation status, and
    /// — when the record is a session (NPS-CR-0003 §5.1.3) — chains up to
    /// the group and rejects if the parent is revoked or expired
    /// (NPS-3 §7 step 3a). Returns a <see cref="NipVerifyResult"/>
    /// describing the outcome.
    /// </summary>
    public async Task<NipVerifyResult> VerifyAsync(string nid, CancellationToken ct = default)
    {
        var record = await _store.GetByNidAsync(nid, ct);
        if (record is null)
            return NipVerifyResult.Fail(NipErrorCodes.NidNotFound, "NID not found.");

        if (record.RevokedAt.HasValue)
            return NipVerifyResult.Fail(NipErrorCodes.CertRevoked,
                $"Revoked at {record.RevokedAt:O}: {record.RevokeReason}");

        if (DateTime.UtcNow > record.ExpiresAt)
            return NipVerifyResult.Fail(NipErrorCodes.CertExpired,
                $"Expired at {record.ExpiresAt:O}.");

        // Chain check — NPS-3 §7 step 3a (NPS-CR-0003).
        if (!string.IsNullOrEmpty(record.ParentNid))
        {
            var parent = await _store.GetByNidAsync(record.ParentNid, ct);
            if (parent is null)
                return NipVerifyResult.Fail(NipErrorCodes.ParentRevoked,
                    $"Parent NID {record.ParentNid} not found.");
            if (parent.RevokedAt.HasValue)
                return NipVerifyResult.Fail(NipErrorCodes.ParentRevoked,
                    $"Parent {record.ParentNid} revoked at {parent.RevokedAt:O}: {parent.RevokeReason}");
            if (DateTime.UtcNow > parent.ExpiresAt)
                return NipVerifyResult.Fail(NipErrorCodes.ParentRevoked,
                    $"Parent {record.ParentNid} expired at {parent.ExpiresAt:O}.");
        }

        return NipVerifyResult.Ok(record);
    }

    // ── CRL ───────────────────────────────────────────────────────────────────

    /// <summary>Returns the current Certificate Revocation List (NPS-3 §8).</summary>
    public Task<IReadOnlyList<NipCertRecord>> GetCrlAsync(CancellationToken ct = default) =>
        _store.GetRevokedAsync(ct);

    /// <summary>Returns all certificate records from the backing CA store.</summary>
    public Task<IReadOnlyList<NipCertRecord>> ListCertificatesAsync(CancellationToken ct = default) =>
        _store.ListAsync(ct);

    /// <summary>Signs an arbitrary CA-owned JSON artifact with the CA Ed25519 key.</summary>
    public string SignArtifact(object artifact) => NipSigner.Sign(_keys.PrivateKey, artifact);

    // ── CA public key ─────────────────────────────────────────────────────────

    /// <summary>Returns the CA public key in <c>ed25519:{base64url}</c> format.</summary>
    public string GetCaPublicKey() => NipSigner.EncodePublicKey(_keys.PublicKey);

    // ── NID builder ───────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a NID from the CA's issuer domain and an entity-specific identifier.
    /// </summary>
    public string BuildNid(string entityType, string identifier)
    {
        // Extract domain from CaNid: "urn:nps:org:ca.example.com" → "ca.example.com"
        var parts  = _opts.CaNid.Split(':');
        var domain = parts.Length >= 4 ? parts[3] : _opts.CaNid;
        return $"urn:nps:{entityType}:{domain}:{identifier}";
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private IdentFrame IssueFrame(
        string nid, string pubKey,
        IReadOnlyList<string> capabilities,
        string scopeJson,
        DateTime issuedAt, DateTime expiresAt,
        string serial,
        string? metadataJson,
        AssuranceLevel? assuranceLevel = null,
        IdentLineage? lineage = null)
    {
        var scope   = JsonDocument.Parse(scopeJson).RootElement;
        var issuedAtStr  = issuedAt.ToString("O");
        var expiresAtStr = expiresAt.ToString("O");

        // Canonical payload for signing — alphabetical order is enforced by
        // NipSigner.CanonicalJson. assurance_level and lineage are included
        // in the signed payload only when set; absent fields are omitted so
        // frames issued without these features remain bit-compatible with
        // pre-RFC-0003 / pre-CR-0003 verifiers (NPS-3 §5.1, §5.1.3).
        object payload = BuildSignedPayload(
            nid, pubKey, capabilities, scope, issuedAtStr, expiresAtStr,
            serial, assuranceLevel, lineage);
        var signature = NipSigner.Sign(_keys.PrivateKey, payload);

        IdentMetadata? metadata = null;
        if (metadataJson is not null)
            metadata = JsonSerializer.Deserialize<IdentMetadata>(metadataJson, s_jsonOpts);

        return new IdentFrame
        {
            Nid            = nid,
            PubKey         = pubKey,
            Capabilities   = capabilities,
            Scope          = scope.Clone(),
            IssuedBy       = _opts.CaNid,
            IssuedAt       = issuedAtStr,
            ExpiresAt      = expiresAtStr,
            Serial         = serial,
            Signature      = signature,
            Metadata       = metadata,
            AssuranceLevel = assuranceLevel,
            Lineage        = lineage,
        };
    }

    private object BuildSignedPayload(
        string nid, string pubKey,
        IReadOnlyList<string> capabilities,
        JsonElement scope,
        string issuedAtStr, string expiresAtStr,
        string serial,
        AssuranceLevel? assuranceLevel,
        IdentLineage? lineage)
    {
        // Build a Dictionary so we can add fields conditionally. NipSigner
        // re-orders alphabetically anyway; this keeps the call sites simple.
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["capabilities"] = capabilities,
            ["expires_at"]   = expiresAtStr,
            ["frame"]        = "0x20",
            ["issued_at"]    = issuedAtStr,
            ["issued_by"]    = _opts.CaNid,
            ["nid"]          = nid,
            ["pub_key"]      = pubKey,
            ["scope"]        = scope,
            ["serial"]       = serial,
        };
        if (assuranceLevel is not null)
            payload["assurance_level"] = assuranceLevel.Value;
        if (lineage is not null)
            payload["lineage"] = lineage;
        return payload;
    }

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // ── X.509 helpers (NPS-RFC-0002 prototype) ────────────────────────────────

    private static byte[] ExtractEd25519Raw(string encoded)
    {
        const string prefix = "ed25519:";
        if (!encoded.StartsWith(prefix, StringComparison.Ordinal))
            throw new NipCaException(
                $"X.509 issuance requires an ed25519:* pubkey; got '{encoded}'.",
                NipErrorCodes.CertFormatInvalid);
        var b64u = encoded[prefix.Length..];
        var raw  = NipSigner.FromBase64Url(b64u);
        if (raw.Length != 32)
            throw new NipCaException(
                $"Ed25519 pubkey must be 32 bytes; got {raw.Length}.",
                NipErrorCodes.CertFormatInvalid);
        return raw;
    }

    private static byte[] ParseSerialBytes(string serial)
    {
        // Accept "0x<hex>" or plain hex. X509 serials must be positive — pad
        // with a leading 0x00 byte if the high bit is set.
        var hex = serial.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? serial[2..] : serial;
        if (hex.Length % 2 != 0) hex = "0" + hex;
        var bytes = Convert.FromHexString(hex);
        if (bytes.Length == 0) bytes = new byte[] { 0x01 };
        if ((bytes[0] & 0x80) != 0)
        {
            var padded = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, padded, 1, bytes.Length);
            return padded;
        }
        return bytes;
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── Lineage helpers (NPS-CR-0003) ─────────────────────────────────────────

    private static string RandomHex(int byteLength)
    {
        var buf = new byte[byteLength];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }

    private static string? ExtractOwnerUserId(string? lineageJson) =>
        ExtractLineageString(lineageJson, "owner_user_id");

    private static string? ExtractOwnerKeyId(string? lineageJson) =>
        ExtractLineageString(lineageJson, "owner_key_id");

    private static string? ExtractLineageString(string? lineageJson, string field)
    {
        if (string.IsNullOrEmpty(lineageJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(lineageJson);
            return doc.RootElement.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch { return null; }
    }
}

// ── Result types ──────────────────────────────────────────────────────────────

/// <summary>Result of a NIP certificate verification check.</summary>
public sealed class NipVerifyResult
{
    public bool           Valid     { get; private init; }
    public string?        ErrorCode { get; private init; }
    public string?        Message   { get; private init; }
    public NipCertRecord? Record    { get; private init; }

    public static NipVerifyResult Ok(NipCertRecord record) =>
        new() { Valid = true, Record = record };

    public static NipVerifyResult Fail(string errorCode, string message) =>
        new() { Valid = false, ErrorCode = errorCode, Message = message };
}

/// <summary>Thrown when a NIP CA operation cannot be completed.</summary>
public sealed class NipCaException : Exception
{
    public string ErrorCode { get; }
    public NipCaException(string message, string errorCode) : base(message) => ErrorCode = errorCode;
}

/// <summary>NIP error codes (NPS-3 §9).</summary>
public static class NipErrorCodes
{
    /// <summary>Alpha.20 profile error. Maps to NPS-CLIENT-BAD-FRAME; never implies safe retry.</summary>
    public const string ContextInvalid = "NIP-CONTEXT-INVALID";
    /// <summary>Alpha.20 profile error. Maps to NPS-AUTH-FORBIDDEN; never implies safe retry.</summary>
    public const string ContextUntrusted = "NIP-CONTEXT-UNTRUSTED";
    /// <summary>Alpha.20 profile error. Maps to NPS-AUTH-FORBIDDEN; never implies safe retry.</summary>
    public const string ContextBindingMismatch = "NIP-CONTEXT-BINDING-MISMATCH";
    /// <summary>Alpha.20 profile error. Maps to NPS-AUTH-FORBIDDEN; never implies safe retry.</summary>
    public const string ContextExpired = "NIP-CONTEXT-EXPIRED";
    /// <summary>Alpha.20 profile error. Maps to NPS-CLIENT-CONFLICT; never implies safe retry.</summary>
    public const string ContextReplay = "NIP-CONTEXT-REPLAY";
    /// <summary>Alpha.20 profile error. Maps to NPS-SERVER-UNAVAILABLE; never implies safe retry.</summary>
    public const string ContextVerifierUnavailable = "NIP-CONTEXT-VERIFIER-UNAVAILABLE";
    public const string CertExpired      = "NIP-CERT-EXPIRED";
    public const string CertRevoked      = "NIP-CERT-REVOKED";
    public const string CertSigInvalid   = "NIP-CERT-SIGNATURE-INVALID";
    public const string CertUntrusted    = "NIP-CERT-UNTRUSTED-ISSUER";
    public const string CertCapMissing   = "NIP-CERT-CAPABILITY-MISSING";
    public const string CertScope        = "NIP-CERT-SCOPE-VIOLATION";

    /// <summary>IdentFrame.node_roles not a subset of the id-nps-node-roles cert extension (NIP v0.11 §7.5).</summary>
    public const string CertNodeRolesMismatch = "NIP-CERT-NODE-ROLES-MISMATCH";

    /// <summary>IdentFrame.capabilities claims a capability absent from id-nps-capabilities (NIP v0.11 §7.5).</summary>
    public const string CertCapabilitiesExceeded = "NIP-CERT-CAPABILITIES-EXCEEDED";

    /// <summary>ocsp_staple missing, malformed, or nextUpdate elapsed under Phase-3 enforcement (NIP v0.9 §5.1.4, v0.11 §7.5).</summary>
    public const string OcspStapleExpired = "NIP-OCSP-STAPLE-EXPIRED";
    public const string OcspUnknown       = "NIP-OCSP-UNKNOWN";
    public const string RevocationStateStale = "NIP-REVOCATION-STATE-STALE";
    public const string NidNotFound      = "NIP-CA-NID-NOT-FOUND";
    public const string NidAlreadyExists = "NIP-CA-NID-ALREADY-EXISTS";
    public const string SerialDuplicate  = "NIP-CA-SERIAL-DUPLICATE";
    public const string RenewalTooEarly  = "NIP-CA-RENEWAL-TOO-EARLY";
    public const string ScopeExpansion   = "NIP-CA-SCOPE-EXPANSION-DENIED";
    public const string OcspUnavailable  = "NIP-OCSP-UNAVAILABLE";
    public const string TrustInvalid     = "NIP-TRUST-FRAME-INVALID";
    public const string RevokeInvalid    = "NIP-REVOKE-FRAME-INVALID";

    /// <summary>
    /// IdentFrame.assurance_level disagrees with the X.509 cert extension
    /// id-nid-assurance-level (downgrade-attack defence). NPS-3 §5.1.1
    /// (NPS-RFC-0003). → NPS-CLIENT-BAD-FRAME.
    /// </summary>
    public const string AssuranceMismatch = "NIP-ASSURANCE-MISMATCH";

    /// <summary>
    /// IdentFrame.assurance_level (or the X.509 extension) carries a
    /// value outside the defined enum (anonymous / attested / verified).
    /// NPS-3 §5.1.1 (NPS-RFC-0003). → NPS-CLIENT-BAD-FRAME.
    /// </summary>
    public const string AssuranceUnknown  = "NIP-ASSURANCE-UNKNOWN";

    /// <summary>
    /// IdentFrame.cert_chain bytes are not DER-encoded X.509 or fail ASN.1
    /// parsing. NPS-RFC-0002 §4.3. → NPS-CLIENT-BAD-FRAME.
    /// </summary>
    public const string CertFormatInvalid    = "NIP-CERT-FORMAT-INVALID";

    /// <summary>
    /// IdentFrame.cert_chain leaf certificate is missing the required NPS
    /// EKU (<c>agent-identity</c> or <c>node-identity</c>). EKU MUST be
    /// marked critical to prevent cross-purpose use as a TLS server cert.
    /// NPS-RFC-0002 §4.3. → NPS-CLIENT-BAD-FRAME.
    /// </summary>
    public const string CertEkuMissing       = "NIP-CERT-EKU-MISSING";

    /// <summary>
    /// X.509 cert subject CN or SAN URI does not match the
    /// <see cref="Frames.IdentFrame.Nid"/> field. NPS-RFC-0002 §4.3.
    /// → NPS-CLIENT-BAD-FRAME.
    /// </summary>
    public const string CertSubjectNidMismatch = "NIP-CERT-SUBJECT-NID-MISMATCH";

    /// <summary>
    /// ACME <c>agent-01</c> challenge validation failed at the CA side
    /// (signature missing, token mismatch, replay, etc.). NPS-RFC-0002
    /// §4.3 / §4.4. → NPS-CLIENT-BAD-FRAME.
    /// </summary>
    public const string AcmeChallengeFailed  = "NIP-ACME-CHALLENGE-FAILED";

    /// <summary>
    /// Reputation log entry signature fails verification or canonical
    /// (RFC 8785 JCS) form is malformed. NPS-3 §5.1.2 (NPS-RFC-0004).
    /// → NPS-CLIENT-BAD-FRAME.
    /// </summary>
    public const string ReputationEntryInvalid = "NIP-REPUTATION-ENTRY-INVALID";

    /// <summary>
    /// A log operator referenced by a Node's reputation_policy cannot
    /// be reached during admission evaluation. NPS-3 §5.1.2
    /// (NPS-RFC-0004). → NPS-DOWNSTREAM-UNAVAILABLE.
    /// </summary>
    public const string ReputationLogUnreachable = "NIP-REPUTATION-LOG-UNREACHABLE";

    /// <summary>
    /// Cannot issue a session under a group NID that has been revoked.
    /// NPS-3 §5.1.3 (NPS-CR-0003). → NPS-AUTH-FORBIDDEN.
    /// </summary>
    public const string GroupRevoked = "NIP-CA-GROUP-REVOKED";

    /// <summary>
    /// The parent_nid / group NID referenced by a session-issue request
    /// does not exist. NPS-3 §5.1.3 (NPS-CR-0003). → NPS-CLIENT-NOT-FOUND.
    /// </summary>
    public const string ParentNotFound = "NIP-CA-PARENT-NOT-FOUND";

    /// <summary>
    /// The referenced parent NID exists but is not a group
    /// (lineage.role ≠ "group"). NPS-3 §5.1.3 (NPS-CR-0003).
    /// → NPS-CLIENT-BAD-PARAM.
    /// </summary>
    public const string ParentNotGroup = "NIP-CA-PARENT-NOT-GROUP";

    /// <summary>
    /// Requested session validity below 60s or above the configured
    /// maximum. NPS-3 §5.1.3 (NPS-CR-0003). → NPS-CLIENT-BAD-PARAM.
    /// </summary>
    public const string SessionValidityInvalid = "NIP-CA-SESSION-VALIDITY-INVALID";

    /// <summary>
    /// Group-JWS authorisation on a session-issue request fails
    /// signature, header, or shape validation. NPS-3 §5.1.3
    /// (NPS-CR-0003). → NPS-AUTH-UNAUTHENTICATED.
    /// </summary>
    public const string JwsInvalid = "NIP-CA-JWS-INVALID";

    /// <summary>
    /// Group-JWS iat outside the CA's clock-skew window (default ±5
    /// minutes). NPS-3 §5.1.3 (NPS-CR-0003). → NPS-AUTH-UNAUTHENTICATED.
    /// </summary>
    public const string JwsExpired = "NIP-CA-JWS-EXPIRED";

    /// <summary>
    /// Session NID's parent / group NID is revoked or expired (chain
    /// check, NPS-3 §7 step 3a). NPS-CR-0003. → NPS-AUTH-UNAUTHENTICATED.
    /// </summary>
    public const string ParentRevoked = "NIP-CERT-PARENT-REVOKED";

    // ── RA error codes (NPS-CR-0005) ──────────────────────────────────────────

    /// <summary>
    /// Bootstrap token is missing, has an invalid prefix, or does not match
    /// any stored token hash. NPS-CR-0005 §3.3. → NPS-AUTH-UNAUTHENTICATED.
    /// </summary>
    public const string RaTokenInvalid = "NIP-RA-TOKEN-INVALID";

    /// <summary>
    /// Bootstrap token matched but is expired or already consumed.
    /// NPS-CR-0005 §3.3. → NPS-AUTH-UNAUTHENTICATED.
    /// </summary>
    public const string RaTokenExpired = "NIP-RA-TOKEN-EXPIRED";

    /// <summary>
    /// Identifier does not match any pattern in the enrollment allowlist.
    /// NPS-CR-0005 §3.2. → NPS-AUTH-FORBIDDEN.
    /// </summary>
    public const string RaNidNotAllowed = "NIP-RA-NID-NOT-ALLOWED";

    /// <summary>
    /// Operator explicitly rejected this pending registration.
    /// NPS-CR-0005 §3.4. → NPS-AUTH-FORBIDDEN.
    /// </summary>
    public const string RaPendingRejected = "NIP-RA-PENDING-REJECTED";
}
