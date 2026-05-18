using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Choreography.Dispatch
{
    public enum DispatchTaskStatus
    {
        Running,
        Completed,
        Failed,
        Stuck,
        Cancelled
    }

    public sealed class TaskInfo
    {
        public string TaskId { get; }
        public string TaskType { get; }
        public string StepName { get; }
        public string InstanceKey { get; }
        public string MessageId { get; }
        public DateTime ArrivalTime { get; }
        public DateTime StartTime { get; internal set; }
        public DispatchTaskStatus Status { get; internal set; }

        internal CancellationTokenSource Cts { get; }

        public TimeSpan Elapsed => Status == DispatchTaskStatus.Running
            ? DateTime.UtcNow - StartTime
            : completedAt - StartTime;

        private DateTime completedAt;

        internal TaskInfo(string taskId, string taskType, string stepName, string instanceKey,
            string messageId, CancellationTokenSource cts)
        {
            TaskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
            TaskType = taskType;
            StepName = stepName;
            InstanceKey = instanceKey;
            MessageId = messageId;
            ArrivalTime = DateTime.UtcNow;
            StartTime = DateTime.UtcNow;
            Status = DispatchTaskStatus.Running;
            Cts = cts;
        }

        internal void MarkCompleted()
        {
            completedAt = DateTime.UtcNow;
            Status = DispatchTaskStatus.Completed;
        }

        internal void MarkFailed()
        {
            completedAt = DateTime.UtcNow;
            Status = DispatchTaskStatus.Failed;
        }

        internal void MarkCancelled()
        {
            completedAt = DateTime.UtcNow;
            Status = DispatchTaskStatus.Cancelled;
        }

        internal void MarkStuck()
        {
            Status = DispatchTaskStatus.Stuck;
        }
    }

    public sealed class TaskMonitor : IDisposable
    {
        private readonly ConcurrentDictionary<string, TaskInfo> activeTasks = new();
        private readonly Timer stuckDetectionTimer;
        private TimeSpan stuckThreshold;

        public event Action<TaskInfo> OnTaskStarted;
        public event Action<TaskInfo> OnTaskCompleted;
        public event Action<TaskInfo, Exception> OnTaskFailed;
        public event Action<TaskInfo> OnTaskStuck;

        internal TaskMonitor(TimeSpan stuckThreshold)
        {
            this.stuckThreshold = stuckThreshold;
            stuckDetectionTimer = new Timer(_ => DetectStuck(), null,
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        public TimeSpan StuckThreshold
        {
            get => stuckThreshold;
            set
            {
                if (value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(value));
                stuckThreshold = value;
            }
        }

        public IReadOnlyList<TaskInfo> ActiveTasks => activeTasks.Values.ToList();

        public IReadOnlyList<TaskInfo> GetByType(string taskType)
        {
            ArgumentNullException.ThrowIfNull(taskType);
            return activeTasks.Values.Where(t => t.TaskType == taskType).ToList();
        }

        public bool TryCancel(string taskId)
        {
            ArgumentNullException.ThrowIfNull(taskId);

            if (!activeTasks.TryGetValue(taskId, out var task))
                return false;

            if (task.Status != DispatchTaskStatus.Running && task.Status != DispatchTaskStatus.Stuck)
                return false;

            task.Cts.Cancel();
            task.MarkCancelled();
            return true;
        }

        internal TaskInfo Register(string taskType, string stepName, string instanceKey,
            string messageId, CancellationTokenSource cts)
        {
            var taskId = Guid.NewGuid().ToString("N");
            var info = new TaskInfo(taskId, taskType, stepName, instanceKey, messageId, cts);
            activeTasks.TryAdd(taskId, info);
            OnTaskStarted?.Invoke(info);
            return info;
        }

        internal void Complete(TaskInfo info)
        {
            info.MarkCompleted();
            activeTasks.TryRemove(info.TaskId, out _);
            OnTaskCompleted?.Invoke(info);
        }

        internal void Fail(TaskInfo info, Exception ex)
        {
            info.MarkFailed();
            activeTasks.TryRemove(info.TaskId, out _);
            OnTaskFailed?.Invoke(info, ex);
        }

        private void DetectStuck()
        {
            foreach (var task in activeTasks.Values)
            {
                if (task.Status == DispatchTaskStatus.Running && task.Elapsed > stuckThreshold)
                {
                    task.MarkStuck();
                    OnTaskStuck?.Invoke(task);
                }
            }
        }

        public void Dispose()
        {
            stuckDetectionTimer.Dispose();

            foreach (var task in activeTasks.Values)
            {
                if (task.Status == DispatchTaskStatus.Running || task.Status == DispatchTaskStatus.Stuck)
                {
                    task.Cts.Cancel();
                    task.MarkCancelled();
                }
            }
            activeTasks.Clear();
        }
    }
}
