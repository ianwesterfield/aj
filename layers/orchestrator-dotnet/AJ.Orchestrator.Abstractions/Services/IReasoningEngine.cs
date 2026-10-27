using AJ.Orchestrator.Abstractions.Models.Classification;
using AJ.Orchestrator.Abstractions.Models.Tasks;
using AJ.Orchestrator.Abstractions.Models.Workspace;

namespace AJ.Orchestrator.Abstractions.Services;

/// <summary>
/// Reasoning engine that coordinates LLM calls for step generation.
/// </summary>
public interface IReasoningEngine
{
    /// <summary>
    /// Classify user intent as 'task' or 'casual'.
    /// </summary>
    Task<ClassifyResponse> ClassifyIntentAsync(string text, string? context = null);

    /// <summary>
    /// Extract memory-worthy facts from user text (replaces the pragmatics service).
    /// </summary>
    Task<ExtractFactsResponse> ExtractFactsAsync(string text);

    /// <summary>
    /// Generate the next step based on task and history.
    /// </summary>
    Task<NextStepResponse> GenerateNextStepAsync(
        string task,
        List<StepResult> history,
        WorkspaceContext? workspace = null,
        List<Dictionary<string, object?>>? memoryContext = null);

    /// <summary>
    /// Run a complete task with streaming updates.
    /// </summary>
    IAsyncEnumerable<TaskEvent> RunTaskStreamAsync(
        RunTaskRequest request,
        CancellationToken ct = default);
}
