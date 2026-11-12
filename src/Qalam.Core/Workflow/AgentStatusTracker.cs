using System.Collections.Concurrent;
using Qalam.Core.Models;

namespace Qalam.Core.Workflow;

public enum AgentWorkState { Idle, Working, Failed }

public sealed class AgentLiveStatus
{
    public AgentId Agent { get; init; }
    public AgentWorkState State { get; set; } = AgentWorkState.Idle;
    public string? CurrentTask { get; set; }
    public string? ProjectId { get; set; }
    public DateTime? Since { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Live agent status, driven only by real API calls starting, finishing or failing.</summary>
public sealed class AgentStatusTracker
{
    private readonly ConcurrentDictionary<AgentId, AgentLiveStatus> _status = new();

    public event EventHandler<AgentId>? Changed;

    public AgentStatusTracker()
    {
        foreach (var id in Enum.GetValues<AgentId>()) _status[id] = new AgentLiveStatus { Agent = id };
    }

    public AgentLiveStatus Get(AgentId id) => _status[id];

    public void Started(AgentId id, string task, string? projectId)
    {
        var s = _status[id];
        lock (s)
        {
            s.State = AgentWorkState.Working;
            s.CurrentTask = task;
            s.ProjectId = projectId;
            s.Since = DateTime.UtcNow;
            s.LastError = null;
        }
        Changed?.Invoke(this, id);
    }

    public void Finished(AgentId id)
    {
        var s = _status[id];
        lock (s)
        {
            s.State = AgentWorkState.Idle;
            s.CurrentTask = null;
            s.ProjectId = null;
            s.Since = DateTime.UtcNow;
        }
        Changed?.Invoke(this, id);
    }

    public void Failed(AgentId id, string error)
    {
        var s = _status[id];
        lock (s)
        {
            s.State = AgentWorkState.Failed;
            s.LastError = error;
            s.Since = DateTime.UtcNow;
        }
        Changed?.Invoke(this, id);
    }
}
