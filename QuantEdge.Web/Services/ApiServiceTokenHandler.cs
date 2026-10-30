using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using QuantEdge.Infrastructure.Security;

namespace QuantEdge.Web.Services;

/// <summary>Adds the Web server's own access token to its calls to the API (the "QuantEdgeApi" HttpClient).</summary>
public class ApiServiceTokenHandler : DelegatingHandler
{
    private readonly IConfiguration _configuration;

    public ApiServiceTokenHandler(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string token = ApiAccessToken.ForService(_configuration);
        if (token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return base.SendAsync(request, cancellationToken);
    }
}
