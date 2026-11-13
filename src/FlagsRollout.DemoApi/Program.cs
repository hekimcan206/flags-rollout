using FlagsRollout.DemoApi;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddDemoApi(builder.Configuration);
builder.Services.AddDemoObservability(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.MapOpenApi();
app.MapScalarApiReference();
app.MapQuoteEndpoints();
app.MapAdminEndpoints();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready");

app.Run();

/// <summary>Entry point; public so integration tests can host the service with WebApplicationFactory.</summary>
public partial class Program;
