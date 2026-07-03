using System;

namespace ZeroFall.Platform.Models;

public enum InvestigationRunStatus
{
    Draft,
    Running,
    Completed,
    Failed,
    Cancelled
}

public enum InvestigationStepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped
}

public sealed class InvestigationRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Goal { get; set; } = string.Empty;
    public InvestigationRunStatus Status { get; set; } = InvestigationRunStatus.Draft;
    public string CreatedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public string UpdatedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public string CurrentStage { get; set; } = string.Empty;
}

public sealed class InvestigationStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RunId { get; set; } = string.Empty;
    public int Seq { get; set; }
    public string StepType { get; set; } = string.Empty;
    public InvestigationStepStatus Status { get; set; } = InvestigationStepStatus.Pending;
    public string InputJson { get; set; } = "{}";
    public string OutputJson { get; set; } = "{}";
    public string Error { get; set; } = string.Empty;
    public string CreatedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public string UpdatedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");
}

public sealed class InvestigationArtifact
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RunId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public string ArtifactType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string CreatedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");
}
