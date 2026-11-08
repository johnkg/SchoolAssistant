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
builder.Services.AddSingleton<ISheetTabLister, GoogleSheetTabLister>();
builder.Services.AddSingleton<SheetSync>();
builder.Services.AddSingleton<AnnouncementProcessor>();
builder.Services.AddSingleton<ButtonHandler>();

// LLM adapters: pick one with the LLM_PROVIDER setting ("openai" or "anthropic")
builder.Services.AddSingleton<AnthropicLlmClient>();
builder.Services.AddSingleton<OpenAiLlmClient>();
builder.Services.AddSingleton<ILlmClient>(sp =>
    Env.GetOr(Settings.LlmProvider, LlmProviders.OpenAi).Trim().ToLowerInvariant() switch
    {
        LlmProviders.OpenAi => sp.GetRequiredService<OpenAiLlmClient>(),
        LlmProviders.Anthropic => sp.GetRequiredService<AnthropicLlmClient>(),
        var other => throw new InvalidOperationException(
            $"Unknown {Settings.LlmProvider} '{other}'. Use '{LlmProviders.OpenAi}' or '{LlmProviders.Anthropic}'.")
    });

if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Settings.AppInsightsConnectionString)))
{
    builder.Services.AddOpenTelemetry()
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter();
}

builder.Build().Run();