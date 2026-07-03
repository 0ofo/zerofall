namespace ZeroFall.Platform.Models;

public enum DataSourceType
{
    MySql,
    Sqlite,
    Csv,
    Json,
    Excel,
    Other
}

public enum TreeNodeType
{
    Root,
    Folder,
    DataSource,
    Database,
    Table,
    File
}
