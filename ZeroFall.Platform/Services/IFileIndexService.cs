using System.Threading.Tasks;

namespace ZeroFall.Platform.Services;

public interface IFileIndexService
{
    Task<IndexResult> IndexCsvAsync(string csvFilePath, string projectDatabasePath);
}

public class IndexResult
{
    public bool IsSuccess { get; init; }
    public string? Error { get; init; }
    public string? TableName { get; init; }
    public int RowCount { get; init; }
    public string? SourceId { get; init; }

    public static IndexResult Success(string tableName, int rowCount, string? sourceId = null) => new()
    {
        IsSuccess = true,
        TableName = tableName,
        RowCount = rowCount,
        SourceId = sourceId
    };

    public static IndexResult Failure(string error) => new()
    {
        IsSuccess = false,
        Error = error
    };
}
