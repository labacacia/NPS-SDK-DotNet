// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NPS.Core.Alpha20;

namespace NPS.NIP.Invocation;

/// <summary>Minimized admission audit; failed/unverified assertions never supply a subject.</summary>
public sealed record NipInvocationAudit(string? Subject, string? Operation, string Outcome);

/// <summary>
/// Connects the experimental integer-only result ActionFrame binding to actual HTTP
/// requests. It depends on a mandatory trusted host factory for authenticated channel
/// identity, route/subject policy, current keys and durable replay. It never trusts
/// X-NWP-Agent, forwarded certificate headers or caller-selected tenant/issuer policy.
/// No endpoint or feature advertisement is installed by constructing this class.
/// </summary>
public sealed class NipResultHttpBinding
{
    /// <summary>Exact requirements for this projected HTTP leg; both dependencies are explicit.</summary>
    public const string RequiredFeatures = "nps.data_results=1,nps.invocation_context=1";
    private readonly object _cacheKey = new();
    private readonly Func<HttpContext, CancellationToken, ValueTask<NipInvocationVerificationOptions>> _hostOptions;
    private readonly Action<NipInvocationAudit>? _audit;

    /// <summary>
    /// Create the binding with a deployment-owned factory. The factory MUST derive allowed
    /// issuer sets from independently authenticated transport (for example actual mTLS),
    /// never from the projection itself. It runs on every admission/revalidation for live policy.
    /// </summary>
    public NipResultHttpBinding(Func<HttpContext, CancellationToken, ValueTask<NipInvocationVerificationOptions>> hostOptions,
        Action<NipInvocationAudit>? audit = null)
    {
        ArgumentNullException.ThrowIfNull(hostOptions);
        _hostOptions = hostOptions;
        _audit = audit;
    }

    /// <summary>
    /// Admit once per HttpContext, then revalidate before execution without consuming the
    /// projection twice. OriginalRequest must preserve the original JSON fields/lexemes;
    /// never supply a reserialized DTO with inserted defaults. Returns verified gateway
    /// assertions; the serving host still derives tenant, resource policy and model egress.
    /// </summary>
    public async ValueTask<NipAdmittedInvocation> VerifyAsync(HttpContext context, JsonElement originalRequest,
        CancellationToken ct = default)
    {
        string? operation = null;
        try
        {
            var rawFeatures = context.Request.Headers["X-NPS-Required-Features"];
            if (rawFeatures.Count != 1 || rawFeatures[0] != RequiredFeatures)
                throw new NipInvocationException("NIP-CONTEXT-BINDING-MISMATCH");
            var features = FeatureWireParser.ParseRequiredHeader(rawFeatures.Select(value => value ?? "").ToArray());
            var options = await _hostOptions(context, ct).ConfigureAwait(false);
            if (!options.SelectedFeatures.SequenceEqual(features)) throw new NipInvocationException("NIP-CONTEXT-BINDING-MISMATCH");
            var request = NipInvocationJson.ResultActionRequest(originalRequest.GetRawText(), options.Audience, features);
            operation = request.GetProperty("operation").GetString();
            var carriers = context.Request.Headers["X-NPS-Invocation-Context"].Select(value => value ?? "").ToArray();
            if (context.Items.TryGetValue(_cacheKey, out var existing))
            {
                if (existing is not NipAdmittedInvocation admitted) throw new NipInvocationException("NIP-CONTEXT-VERIFIER-UNAVAILABLE");
                await NipInvocationVerifier.RevalidateAsync(admitted, carriers, request, context, options, ct).ConfigureAwait(false);
                EmitAudit(new(admitted.Claims.Subject, operation, "revalidated"));
                return admitted;
            }
            var result = await NipInvocationVerifier.AdmitAsync(carriers, request, context, options, ct).ConfigureAwait(false);
            context.Items.Add(_cacheKey, result);
            EmitAudit(new(result.Claims.Subject, operation, "admitted"));
            return result;
        }
        catch (NipInvocationException error)
        {
            EmitAudit(new(null, operation, error.Code));
            throw;
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new NipInvocationException("NIP-CONTEXT-VERIFIER-UNAVAILABLE"); }
    }

    private void EmitAudit(NipInvocationAudit audit)
    {
        try { _audit?.Invoke(audit); }
        catch { throw new NipInvocationException("NIP-CONTEXT-VERIFIER-UNAVAILABLE"); }
    }
}
