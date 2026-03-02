using System;
using System.Collections.Generic;
using System.Text;

namespace sharpclaw.Core;

public interface IAgentContext
{
    string GetWorkspaceDirPath();
    void SetWorkspaceDirPath(string path);
    string GetSessionDirPath();
    void SetSessionDirPath(string path);
    string GetSessionWorkingMemoryFilePath();
    string GetSessionPrimaryMemoryFilePath();
    string GetSessionRecentMemoryFilePath();
    string GetSessionHistoryDirPath();
    string GetSkillsDirPath();
}

public class AgentContext : IAgentContext
{
    private string _sessionPath = string.Empty;
    private string _workspacePath = string.Empty;

    public void SetWorkspaceDirPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Workspace path cannot be null or empty", nameof(path));
        _workspacePath = path;
    }

    public string GetWorkspaceDirPath()
    {
        if (string.IsNullOrWhiteSpace(_workspacePath))
            throw new InvalidOperationException("Workspace path is null");
        return _workspacePath;
    }

    public string GetSessionDirPath()
    {
        if (string.IsNullOrWhiteSpace(_sessionPath))
            throw new InvalidOperationException("Session path is null");
        return _sessionPath;
    }

    public void SetSessionDirPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Session path cannot be null or empty", nameof(path));
        _sessionPath = path;
    }

    public string GetSessionWorkingMemoryFilePath()
    {
        return Path.Combine(_sessionPath, "working_memory.md");
    }

    public string GetSessionPrimaryMemoryFilePath()
    {
        return Path.Combine(_sessionPath, "primary_memory.md");
    }

    public string GetSessionRecentMemoryFilePath()
    {
        return Path.Combine(_sessionPath, "recent_memory.md");
    }

    public string GetSessionHistoryDirPath()
    {
        return Path.Combine(_sessionPath, "history");
    }

    public string GetSkillsDirPath()
    {
        return Path.Combine(SharpclawConfig.ConfigPath, "skills");
    }
}
