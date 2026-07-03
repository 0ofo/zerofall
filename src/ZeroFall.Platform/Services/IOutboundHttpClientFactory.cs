using System;
using System.Net.Http;

namespace ZeroFall.Platform.Services;

public interface IOutboundHttpClientFactory
{
    HttpClient CreateClient(string purpose, TimeSpan timeout, bool acceptAnyServerCertificate = false);
}
