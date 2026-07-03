using System;
using System.Threading;

namespace ZeroFall.Platform.Services;

public sealed class AiChatRunContext : IAiChatRunContext
{
    private readonly AsyncLocal<ScopeState?> _state = new();

    public CancellationToken CancellationToken => _state.Value?.Token ?? CancellationToken.None;

    public bool EnableThinking => _state.Value?.EnableThinking ?? false;

    public string? ModelOverride => _state.Value?.ModelOverride;

    public IDisposable BeginScope(CancellationToken cancellationToken, bool enableThinking = false, string? modelOverride = null)
    {
        var previous = _state.Value;
        _state.Value = new ScopeState(cancellationToken, enableThinking, modelOverride);
        return new Scope(this, previous);
    }

    private sealed record ScopeState(CancellationToken Token, bool EnableThinking, string? ModelOverride);

    private sealed class Scope : IDisposable
    {
        private readonly AiChatRunContext _owner;
        private readonly ScopeState? _previous;
        private bool _disposed;

        public Scope(AiChatRunContext owner, ScopeState? previous)
        {
            _owner = owner;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _owner._state.Value = _previous;
        }
    }
}
