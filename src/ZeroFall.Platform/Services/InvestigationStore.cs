using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ZeroFall.Base.Events;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Models;

namespace ZeroFall.Platform.Services;

public sealed class InvestigationStore : IInvestigationStore, IDisposable
{
    private const string RunsTable = "investigation_runs";
    private const string StepsTable = "investigation_steps";
    private const string ArtifactsTable = "investigation_artifacts";

    private readonly IEventBus _eventBus;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<ProjectOpenedEvent> _projectOpenedHandler;

    private string _databasePath = string.Empty;
    private bool _schemaReady;

    public InvestigationStore(IEventBus eventBus)
    {
        _eventBus = eventBus;
        _projectOpenedHandler = OnProjectOpened;
        _eventBus.Subscribe(_projectOpenedHandler);
    }

    private void OnProjectOpened(ProjectOpenedEvent e)
    {
        _databasePath = e.DatabasePath ?? string.Empty;
        _schemaReady = false;
        _ = EnsureSchemaAsync();
    }

    public async Task<InvestigationRun> CreateRunAsync(string goal, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow.ToString("O");
        var run = new InvestigationRun
        {
            Goal = goal,
            Status = InvestigationRunStatus.Draft,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO {RunsTable} (id, goal, status, created_at_utc, updated_at_utc, current_stage)
                VALUES ($id, $goal, $status, $created, $updated, $stage)
                """;
            cmd.Parameters.AddWithValue("$id", run.Id);
            cmd.Parameters.AddWithValue("$goal", run.Goal);
            cmd.Parameters.AddWithValue("$status", run.Status.ToString());
            cmd.Parameters.AddWithValue("$created", run.CreatedAtUtc);
            cmd.Parameters.AddWithValue("$updated", run.UpdatedAtUtc);
            cmd.Parameters.AddWithValue("$stage", run.CurrentStage);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        return run;
    }

    public async Task<IReadOnlyList<InvestigationRun>> ListRunsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT id, goal, status, created_at_utc, updated_at_utc, current_stage FROM {RunsTable} ORDER BY created_at_utc DESC";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var list = new List<InvestigationRun>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                list.Add(ReadRun(reader));
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InvestigationRun?> LoadRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT id, goal, status, created_at_utc, updated_at_utc, current_stage FROM {RunsTable} WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", runId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRun(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateRunStatusAsync(string runId, InvestigationRunStatus status, string currentStage = "", CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"UPDATE {RunsTable} SET status = $status, current_stage = $stage, updated_at_utc = $updated WHERE id = $id";
            cmd.Parameters.AddWithValue("$status", status.ToString());
            cmd.Parameters.AddWithValue("$stage", currentStage);
            cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$id", runId);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InvestigationStep> AddStepAsync(string runId, string stepType, string inputJson = "{}", CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var seq = await NextStepSeqAsync(connection, runId, cancellationToken).ConfigureAwait(false);
            var now = DateTime.UtcNow.ToString("O");
            var step = new InvestigationStep
            {
                RunId = runId,
                Seq = seq,
                StepType = stepType,
                InputJson = string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO {StepsTable} (id, run_id, seq, step_type, status, input_json, output_json, error, created_at_utc, updated_at_utc)
                VALUES ($id, $run, $seq, $type, $status, $input, $output, $error, $created, $updated)
                """;
            BindStep(cmd, step);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return step;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateStepAsync(InvestigationStep step, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        step.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                UPDATE {StepsTable}
                SET status = $status, output_json = $output, error = $error, updated_at_utc = $updated
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$status", step.Status.ToString());
            cmd.Parameters.AddWithValue("$output", step.OutputJson);
            cmd.Parameters.AddWithValue("$error", step.Error);
            cmd.Parameters.AddWithValue("$updated", step.UpdatedAtUtc);
            cmd.Parameters.AddWithValue("$id", step.Id);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InvestigationArtifact> AddArtifactAsync(string runId, string stepId, string artifactType, string title, string payloadJson, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var artifact = new InvestigationArtifact
        {
            RunId = runId,
            StepId = stepId,
            ArtifactType = artifactType,
            Title = title,
            PayloadJson = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            CreatedAtUtc = DateTime.UtcNow.ToString("O")
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO {ArtifactsTable} (id, run_id, step_id, artifact_type, title, payload_json, created_at_utc)
                VALUES ($id, $run, $step, $type, $title, $payload, $created)
                """;
            cmd.Parameters.AddWithValue("$id", artifact.Id);
            cmd.Parameters.AddWithValue("$run", artifact.RunId);
            cmd.Parameters.AddWithValue("$step", artifact.StepId);
            cmd.Parameters.AddWithValue("$type", artifact.ArtifactType);
            cmd.Parameters.AddWithValue("$title", artifact.Title);
            cmd.Parameters.AddWithValue("$payload", artifact.PayloadJson);
            cmd.Parameters.AddWithValue("$created", artifact.CreatedAtUtc);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return artifact;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<InvestigationStep>> ListStepsAsync(string runId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT id, run_id, seq, step_type, status, input_json, output_json, error, created_at_utc, updated_at_utc FROM {StepsTable} WHERE run_id = $run ORDER BY seq ASC";
            cmd.Parameters.AddWithValue("$run", runId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var list = new List<InvestigationStep>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                list.Add(ReadStep(reader));
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<InvestigationArtifact>> ListArtifactsAsync(string runId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT id, run_id, step_id, artifact_type, title, payload_json, created_at_utc FROM {ArtifactsTable} WHERE run_id = $run ORDER BY created_at_utc ASC";
            cmd.Parameters.AddWithValue("$run", runId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var list = new List<InvestigationArtifact>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                list.Add(ReadArtifact(reader));
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_databasePath) || _schemaReady)
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_schemaReady)
                return;

            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE IF NOT EXISTS {RunsTable} (
                    id TEXT PRIMARY KEY,
                    goal TEXT NOT NULL,
                    status TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    current_stage TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE IF NOT EXISTS {StepsTable} (
                    id TEXT PRIMARY KEY,
                    run_id TEXT NOT NULL,
                    seq INTEGER NOT NULL,
                    step_type TEXT NOT NULL,
                    status TEXT NOT NULL,
                    input_json TEXT NOT NULL,
                    output_json TEXT NOT NULL,
                    error TEXT NOT NULL DEFAULT '',
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS {ArtifactsTable} (
                    id TEXT PRIMARY KEY,
                    run_id TEXT NOT NULL,
                    step_id TEXT NOT NULL,
                    artifact_type TEXT NOT NULL,
                    title TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_investigation_steps_run_seq ON {StepsTable}(run_id, seq);
                CREATE INDEX IF NOT EXISTS idx_investigation_artifacts_run ON {ArtifactsTable}(run_id);
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _schemaReady = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<int> NextStepSeqAsync(SqliteConnection connection, string runId, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(MAX(seq), -1) + 1 FROM {StepsTable} WHERE run_id = $run";
        cmd.Parameters.AddWithValue("$run", runId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }

    private static void BindStep(SqliteCommand cmd, InvestigationStep step)
    {
        cmd.Parameters.AddWithValue("$id", step.Id);
        cmd.Parameters.AddWithValue("$run", step.RunId);
        cmd.Parameters.AddWithValue("$seq", step.Seq);
        cmd.Parameters.AddWithValue("$type", step.StepType);
        cmd.Parameters.AddWithValue("$status", step.Status.ToString());
        cmd.Parameters.AddWithValue("$input", step.InputJson);
        cmd.Parameters.AddWithValue("$output", step.OutputJson);
        cmd.Parameters.AddWithValue("$error", step.Error);
        cmd.Parameters.AddWithValue("$created", step.CreatedAtUtc);
        cmd.Parameters.AddWithValue("$updated", step.UpdatedAtUtc);
    }

    private static InvestigationRun ReadRun(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Goal = reader.GetString(1),
        Status = Enum.TryParse<InvestigationRunStatus>(reader.GetString(2), out var status) ? status : InvestigationRunStatus.Draft,
        CreatedAtUtc = reader.GetString(3),
        UpdatedAtUtc = reader.GetString(4),
        CurrentStage = reader.GetString(5)
    };

    private static InvestigationStep ReadStep(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        RunId = reader.GetString(1),
        Seq = reader.GetInt32(2),
        StepType = reader.GetString(3),
        Status = Enum.TryParse<InvestigationStepStatus>(reader.GetString(4), out var status) ? status : InvestigationStepStatus.Pending,
        InputJson = reader.GetString(5),
        OutputJson = reader.GetString(6),
        Error = reader.GetString(7),
        CreatedAtUtc = reader.GetString(8),
        UpdatedAtUtc = reader.GetString(9)
    };

    private static InvestigationArtifact ReadArtifact(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        RunId = reader.GetString(1),
        StepId = reader.GetString(2),
        ArtifactType = reader.GetString(3),
        Title = reader.GetString(4),
        PayloadJson = reader.GetString(5),
        CreatedAtUtc = reader.GetString(6)
    };

    public void Dispose()
    {
        _eventBus.Unsubscribe(_projectOpenedHandler);
        _gate.Dispose();
    }
}
