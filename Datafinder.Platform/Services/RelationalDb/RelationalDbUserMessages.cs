using System;
using System.Net.Sockets;
using MySqlConnector;

namespace Datafinder.Platform.Services.RelationalDb;

public static class RelationalDbUserMessages
{
    public static string FormatLoadTablesFailure(string displayName, Exception ex)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? "数据源" : displayName.Trim();
        var root = Unwrap(ex);

        if (root is MySqlException mysql)
        {
            if (mysql.ErrorCode == MySqlErrorCode.UnableToConnectToHost)
                return $"无法连接 MySQL「{name}」：服务器不可达，请检查主机、端口与网络。";

            if (mysql.ErrorCode == MySqlErrorCode.AccessDenied)
                return $"MySQL「{name}」认证失败：请检查用户名与密码。";

            return $"加载 MySQL「{name}」库表失败：{mysql.Message}";
        }

        if (root is SocketException)
            return $"无法连接 MySQL「{name}」：服务器未响应，请确认 MySQL 是否在线。";

        if (root is TimeoutException)
            return $"连接 MySQL「{name}」超时，请确认服务是否在线。";

        return $"加载「{name}」库表失败：{root.Message}";
    }

    public static string FormatTestConnectionFailure(string displayName, Exception ex)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? "数据源" : displayName.Trim();
        var root = Unwrap(ex);

        if (root is MySqlException mysql)
        {
            if (mysql.ErrorCode == MySqlErrorCode.UnableToConnectToHost)
                return $"无法连接 MySQL「{name}」：服务器不可达，请检查主机、端口与网络。";

            if (mysql.ErrorCode == MySqlErrorCode.AccessDenied)
                return $"MySQL「{name}」认证失败：请检查用户名与密码。";

            return $"连接 MySQL「{name}」失败：{mysql.Message}";
        }

        if (root is SocketException)
            return $"无法连接 MySQL「{name}」：服务器未响应，请确认 MySQL 是否在线。";

        if (root is TimeoutException)
            return $"连接 MySQL「{name}」超时，请确认服务是否在线。";

        return $"连接「{name}」失败：{root.Message}";
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex.InnerException is { } inner && ex.Message == inner.Message)
            ex = inner;
        return ex;
    }
}
