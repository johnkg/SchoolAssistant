namespace SchoolAssistant;

// Provider-neutral input. The webhook builds these; each adapter translates them to its API.
public abstract record LlmPart;
public sealed record TextPart(string Text) : LlmPart;
public sealed record ImagePart(byte[] Data, string MimeType) : LlmPart;
public sealed record PdfPart(byte[] Data, string FileName = "document.pdf") : LlmPart;

/// <summary>The only LLM surface the app depends on. Add a new provider by implementing this.</summary>
public interface ILlmClient
{
    Task<string> AskAsync(string system, IReadOnlyList<LlmPart> parts, CancellationToken ct = default);
}