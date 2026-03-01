using System;
using System.Collections.Generic;
using System.Text;

namespace sharpclaw.Core;

public interface IAgentContext
{
    string GetWorkspacePath();
    void SetWorkspacePath(string path);
}

public class AgentContext : IAgentContext
{
    private string _workspacePath = string.Empty;

    public void SetWorkspacePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Workspace path cannot be null or empty", nameof(path));
        _workspacePath = path;
    }

    public string GetWorkspacePath()
    {
        if (string.IsNullOrWhiteSpace(_workspacePath))
            throw new InvalidOperationException("Workspace path is null");
        return _workspacePath;
    }
}
