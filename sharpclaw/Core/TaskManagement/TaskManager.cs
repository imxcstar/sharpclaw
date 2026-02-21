using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace sharpclaw.Core.TaskManagement;

/// <summary>
/// Manages all background tasks (both process and native).
/// </summary>
public class TaskManager
{
    private long _nextTaskId = 1000;
    private readonly ConcurrentDictionary<string, ITask> _tasks = new();

    public string GenerateTaskId()
    {
        return System.Threading.Interlocked.Increment(ref _nextTaskId).ToString();
    }

    public void AddTask(ITask task)
    {
        _tasks[task.TaskId] = task;
    }

    public bool TryGetTask(string taskId, out ITask? task)
    {
        return _tasks.TryGetValue(taskId, out task);
    }

    public bool RemoveTask(string taskId)
    {
        if (_tasks.TryRemove(taskId, out var task))
        {
            task.Dispose();
            return true;
        }
        return false;
    }

    public List<ITask> GetAllTasks()
    {
        return _tasks.Values.ToList();
    }

    public int TaskCount => _tasks.Count;

    public void Dispose()
    {
        foreach (var task in _tasks.Values)
        {
            try { task.Dispose(); } catch { }
        }
        _tasks.Clear();
    }
}
