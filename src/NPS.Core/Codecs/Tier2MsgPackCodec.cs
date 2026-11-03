// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using MessagePack;
using MessagePack.Resolvers;
using NPS.Core.Exceptions;
using NPS.Core.Frames;
using NPS.Core.Registry;

namespace NPS.Core.Codecs;

/// <summary>
/// Tier-2 codec: MessagePack binary serialisation via MessagePack-CSharp.
/// Payload size depends on data and framing; compare equivalent compressed baselines.
/// Registered frames use source-generated formatters. The single-argument
/// <see cref="Encode(IFrame)"/> overload retains contractless runtime generation
/// only as a compatibility fallback for unregistered third-party frame types.
/// </summary>
public sealed class Tier2MsgPackCodec : IFrameCodec
{
    [RequiresDynamicCode("Use Encode(IFrame, FrameRegistry) with source-generated frame metadata for NativeAOT.")]
    [RequiresUnreferencedCode("Use Encode(IFrame, FrameRegistry) with source-generated frame metadata when trimming.")]
    public byte[] Encode(IFrame frame)
    {
        try
        {
            return MessagePackSerializer.Serialize(frame.GetType(), frame, DynamicFallback.Options);
        }
        catch (Exception ex)
        {
            throw new NpsCodecException($"Tier-2 MsgPack encode failed for {frame.FrameType}.", ex);
        }
    }

    private static class DynamicFallback
    {
        internal static MessagePackSerializerOptions Options { get; } =
            MessagePackSerializerOptions.Standard
                .WithResolver(ContractlessStandardResolver.Instance)
                .WithCompression(MessagePackCompression.None);
    }

    public byte[] Encode(IFrame frame, FrameRegistry registry)
    {
        try
        {
            return registry.ResolveRegistration(frame).MessagePackEncoder(frame);
        }
        catch (Exception ex)
        {
            throw new NpsCodecException($"Tier-2 MsgPack encode failed for {frame.FrameType}.", ex);
        }
    }

    public IFrame Decode(FrameType type, ReadOnlySpan<byte> payload, FrameRegistry registry)
    {
        var registration = registry.ResolveRegistration(type);
        try
        {
            return registration.MessagePackDecoder(payload);
        }
        catch (Exception ex)
        {
            throw new NpsCodecException($"Tier-2 MsgPack decode failed for {type}.", ex);
        }
    }
}
