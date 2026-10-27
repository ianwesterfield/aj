using System.Text.Json.Serialization;

namespace AJ.Orchestrator.Abstractions.Models.Classification;

/// <summary>
/// Request to classify user intent.
/// </summary>
public record ClassifyRequest
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("context")]
    public string? Context { get; init; }
}

/// <summary>
/// Classification result with intent and confidence.
/// </summary>
public record ClassifyResponse(
    [property: JsonPropertyName("intent")] string Intent,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("reason")] string Reason = ""
);

/// <summary>
/// Request to extract memory-worthy facts from user text.
/// </summary>
public record ExtractFactsRequest
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

/// <summary>
/// A single extracted fact (type + value), matching the legacy pragmatics shape.
/// </summary>
public record ExtractedFact(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("value")] string Value
);

/// <summary>
/// Fact-extraction result. Facts is empty when nothing is worth remembering.
/// </summary>
public record ExtractFactsResponse(
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("facts")] List<ExtractedFact> Facts
);
