using System;
using System.Net.Http;

namespace Datafinder.Platform.Services;

public interface IOutboundHttpClientFactory
{
    HttpClient CreateClient(string purpose, TimeSpan timeout);
}
