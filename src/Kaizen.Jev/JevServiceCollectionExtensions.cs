using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kaizen.Jev;

/// <summary>DI registration for <see cref="IJevClient"/>.</summary>
public static class JevServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IJevClient"/> as a typed HttpClient. Returns the <see cref="IHttpClientBuilder"/>
    /// so you can add resilience handlers, logging, etc.
    /// </summary>
    public static IHttpClientBuilder AddJevClient(this IServiceCollection services, Action<JevClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<JevClientOptions>()
            .Configure(configure)
            .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "Jev ApiKey is required.")
            .Validate(o => o.BaseAddress is { IsAbsoluteUri: true }, "Jev BaseAddress must be an absolute URI.");
        return services.AddHttpClient<IJevClient, JevClient>((sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<JevClientOptions>>().Value;
            var b = o.BaseAddress.ToString();
            http.BaseAddress = new Uri(b.EndsWith('/') ? b : b + "/");
        });
    }
}
