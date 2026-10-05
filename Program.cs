using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using SchoolAssistant;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddSingleton<TelegramApi>();
builder.Services.AddSingleton<JujuCalendar>();
builder.Services.AddSingleton<SheetSync>();

// LLM adapters: pick one with the LLM_PROVIDER setting ("openai" or "anthropic")
builder.Services.AddSingleton<AnthropicLlmClient>();
builder.Services.AddSingleton<OpenAiLlmClient>();
builder.Services.AddSingleton<ILlmClient>(sp =>
    Environment.GetEnvironmentVariable("LLM_PROVIDER")?.Trim().ToLowerInvariant() switch
    {
        null or "" or "openai" => sp.GetRequiredService<OpenAiLlmClient>(),
        "anthropic" => sp.GetRequiredService<AnthropicLlmClient>(),
        var other => throw new InvalidOperationException($"Unknown LLM_PROVIDER '{other}'. Use 'openai' or 'anthropic'.")
    });

if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
{
    builder.Services.AddOpenTelemetry()
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter();
}

builder.Build().Run();