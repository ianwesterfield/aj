using AJ.Orchestrator.Abstractions.Services;
using AJ.Orchestrator.Domain.Services;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Services.AddOptions();

// LLM chat client (OpenAI-compatible llama.cpp llama-server endpoint).
// Env var names keep OLLAMA_* aliases for backwards compatibility.
var llmUrl = (builder.Configuration["Llm:Url"]
    ?? Environment.GetEnvironmentVariable("LLM_BASE_URL")
    ?? Environment.GetEnvironmentVariable("OLLAMA_BASE_URL")
    ?? "http://localhost:8081").TrimEnd('/');
var llmModel = builder.Configuration["Llm:Model"]
    ?? Environment.GetEnvironmentVariable("LLM_MODEL")
    ?? Environment.GetEnvironmentVariable("OLLAMA_MODEL")
    ?? "bonsai-2-27b";

builder.Services.AddSingleton<IChatClient>(_ =>
    new OpenAIClient(
        new ApiKeyCredential("sk-local-llamacpp"),
        new OpenAIClientOptions
        {
            Endpoint = new Uri($"{llmUrl}/v1"),
            NetworkTimeout = TimeSpan.FromMinutes(5)
        })
    .GetChatClient(llmModel)
    .AsIChatClient());

// HTTP clients for external services
builder.Services.AddHttpClient("funnel", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["FunnelCloud:Url"] ?? "http://localhost:41421");
});

// Gossip seed host for cross-subnet discovery
var seedHost = builder.Configuration["FunnelCloud:GossipSeedHost"];
if (!string.IsNullOrEmpty(seedHost))
{
    builder.Services.AddHttpClient("funnel-seed", c =>
    {
        c.BaseAddress = new Uri($"http://{seedHost}:41421");
    });
}

// Core services
builder.Services.AddSingleton<SessionStateManager>();
builder.Services.AddSingleton<IAgentDiscovery, AgentDiscoveryService>();
builder.Services.AddSingleton<IGrpcAgentClient, GrpcAgentClient>();
builder.Services.AddSingleton<ISkillDiscoveryService, SkillDiscoveryService>();
builder.Services.AddSingleton<ISkillExecutor, SkillExecutor>();
builder.Services.AddScoped<ITaskPlanner, TaskPlanner>();
builder.Services.AddScoped<IReasoningEngine, ReasoningEngine>();

// Controllers & API with JSON options for Python interop
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Accept both camelCase and snake_case from Python clients
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        // Output camelCase for consistency
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

// MCP facade: expose FunnelCloud agents as MCP tools over streamable HTTP at /mcp
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

// CORS for Open-WebUI integration
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

// Load skills at startup
var skillService = app.Services.GetRequiredService<ISkillDiscoveryService>();
await skillService.LoadSkillsAsync();

// Load executable YAML skills for deterministic execution
var skillExecutor = app.Services.GetRequiredService<ISkillExecutor>();
if (skillExecutor is SkillExecutor executor)
{
    var skillPaths = builder.Configuration.GetSection("Skills:Paths").Get<string[]>() ?? ["/skills"];
    await executor.LoadSkillsAsync(skillPaths);
}

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.MapControllers();
app.MapMcp("/mcp");

// Health endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "orchestrator-dotnet" }));

// Skills endpoints for inspection and management
app.MapGet("/skills", (ISkillDiscoveryService skills) =>
{
    var allSkills = skills.GetAllSkills();
    return Results.Ok(new
    {
        count = allSkills.Count,
        skills = allSkills.Select(s => new
        {
            name = s.Name,
            description = s.Description,
            tags = s.Tags,
            targetAgent = s.TargetAgent,
            sourcePath = s.SourcePath
        })
    });
});

app.MapGet("/skills/{name}", (string name, ISkillDiscoveryService skills) =>
{
    var skill = skills.GetSkill(name);
    return skill != null ? Results.Ok(skill) : Results.NotFound();
});

app.MapPost("/skills/reload", async (ISkillDiscoveryService skills) =>
{
    await skills.ReloadSkillsAsync();
    return Results.Ok(new { message = "Skills reloaded", count = skills.GetAllSkills().Count });
});

var port = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")?.Split(':').LastOrDefault() ?? "8004";
Console.WriteLine($"AJ Orchestrator (.NET) starting on port {port}...");
app.Run();
