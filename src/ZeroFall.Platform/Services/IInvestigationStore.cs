using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZeroFall.Platform.Models;

namespace ZeroFall.Platform.Services;

public interface IInvestigationStore
{
    Task<InvestigationRun> CreateRunAsync(string goal, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InvestigationRun>> ListRunsAsync(CancellationToken cancellationToken = default);
    Task<InvestigationRun?> LoadRunAsync(string runId, CancellationToken cancellationToken = default);
    Task UpdateRunStatusAsync(string runId, InvestigationRunStatus status, string currentStage = "", CancellationToken cancellationToken = default);
    Task<InvestigationStep> AddStepAsync(string runId, string stepType, string inputJson = "{}", CancellationToken cancellationToken = default);
    Task UpdateStepAsync(InvestigationStep step, CancellationToken cancellationToken = default);
    Task<InvestigationArtifact> AddArtifactAsync(string runId, string stepId, string artifactType, string title, string payloadJson, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InvestigationStep>> ListStepsAsync(string runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InvestigationArtifact>> ListArtifactsAsync(string runId, CancellationToken cancellationToken = default);
}
