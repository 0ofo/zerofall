using System;
using System.Threading;

namespace ZeroFall.Platform.Services;

public sealed class AiChatRunContext : IAiChatRunContext
{
    private CancellationToken _token = CancellationToken.None;
    private bool _enableThinking;
    private string? _modelOverride;

    public CancellationToken CancellationToken => _token;

    public bool EnableThinking => _enableThinking;

    public string? ModelOverride => _modelOverride;

    public IDisposable BeginScope(CancellationToken cancellationToken, bool enableThinking = false, string? modelOverride = null)
    {
        var previous = new ScopeState(_token, _enableThinking, _modelOverride);
        _token = cancellationToken;
        _enableThinking = enableThinking;
        _modelOverride = modelOverride;
        return new Scope(this, previous);
    }

    private readonly record struct ScopeState(CancellationToken Token, bool EnableThinking, string? ModelOverride);

    private sealed class Scope : IDisposable
    {
        private readonly AiChatRunContext _owner;
        private readonly ScopeState _previous;
        private bool _disposed;

        public Scope(AiChatRunContext owner, ScopeState previous)
        {
            _owner = owner;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _owner._token = _previous.Token;
            _owner._enableThinking = _previous.EnableThinking;
            _owner._modelOverride = _previous.ModelOverride;
        }
    }
}
