namespace ZeroFall.Base.Commands;

public static class CommandIds
{
    public static class Workspace
    {
        public const string OpenFolder = "workspace.openFolder";
        public const string CloseWorkspace = "workspace.closeWorkspace";
    }

    public static class Sidebar
    {
        public const string RefreshTree = "sidebar.refreshTree";
        public const string IndexFile = "sidebar.indexFile";
    }

    public static class SqlEditor
    {
        public const string NewQuery = "sqlEditor.newQuery";
        public const string ExecuteQuery = "sqlEditor.executeQuery";
        public const string ListTables = "sqlEditor.listTables";
    }

    public static class AiPanel
    {
        public const string SendMessage = "aiPanel.sendMessage";
        public const string TogglePanel = "aiPanel.togglePanel";
    }

    public static class AssetRecon
    {
        public const string StartRecon = "assetRecon.startRecon";
    }

    public static class Terminal
    {
        public const string ExecuteCommand = "terminal.executeCommand";
        public const string TogglePanel = "terminal.togglePanel";
    }
}
