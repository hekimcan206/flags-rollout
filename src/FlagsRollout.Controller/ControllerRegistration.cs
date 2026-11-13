using Microsoft.Extensions.Options;

namespace FlagsRollout.Controller;

public static class ControllerRegistration
{
    public static IServiceCollection AddRolloutController(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ControllerOptions>()
            .Bind(configuration.GetSection(ControllerOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ControllerOptions>>().Value);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new FlagFileStore(sp.GetRequiredService<ControllerOptions>().FlagFile));
        services.AddHttpClient<ICounterSource, PrometheusCounterSource>((sp, http) =>
        {
            var options = sp.GetRequiredService<ControllerOptions>();
            http.BaseAddress = new Uri(options.PrometheusUrl.TrimEnd('/') + "/");
            // A query slower than a tick is a failed read; the loop must not stack up behind a sick Prometheus.
            http.Timeout = options.TickInterval < TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : options.TickInterval;
        });
        services.AddSingleton<RolloutController>();
        services.AddHostedService<RolloutWorker>();
        services.AddHealthChecks().AddCheck<ControllerHealthCheck>("rollout");
        return services;
    }
}
