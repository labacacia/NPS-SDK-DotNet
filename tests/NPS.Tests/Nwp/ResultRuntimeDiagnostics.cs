// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NPS.Tests.Nwp;

public sealed partial class ResultInvocationTlsTests
{
    private readonly ConcurrentQueue<string> _runtimeExceptionTypes = new();

    // Fixture-only bounded diagnosis. Never render state, exception messages/stacks,
    // scopes or request data, even when an unexpected server error needs investigation.
    private sealed class RuntimeExceptionTypeLogger(ConcurrentQueue<string> types) : ILoggerProvider, ILogger
    {
        private int _count;
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
        public void Dispose() { }
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            for (var depth = 0; exception is not null && depth < 4; depth++, exception = exception.InnerException)
            {
                if (Interlocked.Increment(ref _count) > 64) return;
                var name = exception.GetType().FullName;
                types.Enqueue(name is { Length: <= 256 } ? name : nameof(Exception));
            }
        }
    }

    [Fact]
    public void RuntimeDiagnosticLogRetainsBoundedTypesWithoutRenderingProtectedState()
    {
        var types = new ConcurrentQueue<string>();
        using var logger = new RuntimeExceptionTypeLogger(types);
        for (var index = 0; index < 100; index++)
            logger.Log(LogLevel.Error, new EventId(0), "protected request state",
                new InvalidOperationException("protected exception message"),
                (_, _) => throw new InvalidOperationException("A diagnostic must never render state or exceptions."));
        Assert.Equal(64, types.Count);
        Assert.All(types, item => Assert.Equal("System.InvalidOperationException", item));
    }
}
