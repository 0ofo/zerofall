using System;

namespace Datafinder.Platform.Services.RelationalDb;

public static class RelationalSqlHelper
{
    public static bool IsReadOnlyQuery(string sql)
    {
        var trimmed = sql.TrimStart();
        return trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("SHOW", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("DESCRIBE", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("DESC", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
    }
}
