namespace SynthWatch.Api.Dtos;

/// <summary>POST /api/checks/{id}/run response (202 Accepted). The API enqueues a run_request + kicks the
/// runner; the run itself appears in the check's run history. requestId echoes the (possibly coalesced)
/// pending request.</summary>
public record RunNowAcceptedDto(long RequestId);
