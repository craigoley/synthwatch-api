namespace SynthWatch.Api.Dtos;

/// <summary>
/// Response of POST /api/checks/parse-intent — the chat-to-prefill suggestion. ★ A SUGGESTION, never a create:
/// the dashboard prefills the create modal with <see cref="Fields"/> + shows <see cref="FieldErrors"/> inline; the
/// human reviews + clicks Create (the unchanged POST /checks revalidates). <see cref="Redirect"/> is set for a
/// browser/multistep ask (no prefill). <see cref="Configured"/>/<see cref="Note"/> mirror the AOAI-endpoint states.
/// </summary>
public sealed record ParseIntentDto(
    bool Configured,
    string? Note,
    bool Retryable,
    string? Redirect,
    string? Reason,
    bool Valid,
    CreateCheckRequest? Fields,
    IReadOnlyDictionary<string, string> FieldErrors,
    string? Notes)
{
    private static readonly IReadOnlyDictionary<string, string> NoErrors = new Dictionary<string, string>();

    public static ParseIntentDto NotConfigured() =>
        new(false, "AI monitor-prefill is not configured for this environment yet.", false, null, null, false, null, NoErrors, null);

    public static ParseIntentDto Unavailable(string note, bool retryable) =>
        new(true, note, retryable, null, null, false, null, NoErrors, null);

    public static ParseIntentDto RedirectTo(string redirect, string? reason) =>
        new(true, null, false, redirect, reason, false, null, NoErrors, null);

    /// <summary>The parsed prefill — <paramref name="fields"/> always returned (so the form prefills what parsed);
    /// <paramref name="fieldErrors"/> are the field-keyed validator errors the form already renders.</summary>
    public static ParseIntentDto Parsed(CreateCheckRequest fields, bool valid, IReadOnlyDictionary<string, string> fieldErrors, string? notes) =>
        new(true, null, false, null, null, valid, fields, fieldErrors, notes);
}
