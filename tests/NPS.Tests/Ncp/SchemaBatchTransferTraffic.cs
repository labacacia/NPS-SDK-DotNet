// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Sockets;

namespace NPS.Tests.Ncp;

public sealed partial class NcpSchemaBatchSessionTests
{
    // Client-side counters BELOW TLS, both directions, no payload logging. A byte
    // crossing this endpoint is counted once; TCP/IP packet overhead is excluded.
    private sealed class TransferTraffic
    {
        private long _read, _written;
        public long Total => Interlocked.Read(ref _read) + Interlocked.Read(ref _written);
        public Stream Wrap(Stream stream) => new CountedStream(stream, this);
        public void Configure(SocketsHttpHandler handler) => handler.ConnectCallback = async (context, ct) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, ct);
                return Wrap(new NetworkStream(socket, ownsSocket: true));
            }
            catch { socket.Dispose(); throw; }
        };

        private sealed class CountedStream(Stream inner, TransferTraffic traffic) : Stream
        {
            public override bool CanRead => inner.CanRead;
            public override bool CanWrite => inner.CanWrite;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => inner.Flush();
            public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
            public override int Read(byte[] bytes, int offset, int count)
            {
                var read = inner.Read(bytes, offset, count);
                Interlocked.Add(ref traffic._read, read);
                return read;
            }
            public override async ValueTask<int> ReadAsync(Memory<byte> bytes, CancellationToken ct = default)
            {
                var read = await inner.ReadAsync(bytes, ct);
                Interlocked.Add(ref traffic._read, read);
                return read;
            }
            public override void Write(byte[] bytes, int offset, int count)
            {
                inner.Write(bytes, offset, count);
                Interlocked.Add(ref traffic._written, count);
            }
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
            {
                await inner.WriteAsync(bytes, ct);
                Interlocked.Add(ref traffic._written, bytes.Length);
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
            public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); GC.SuppressFinalize(this); }
        }
    }
}
