using Cronos;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace sharpclaw.Core;

public class EasyTimingWheelManager
{
    private TimingWheel tw_s;
    private TimingWheel tw_m;
    private TimingWheel tw_h;
    private TimingWheel tw_D;
    private TimingWheel tw_M;
    private TimingWheel tw_Y;

    private ConcurrentDictionary<string, ITimingWheelTask> _tasks;

    public EasyTimingWheelManager()
    {
        _tasks = new ConcurrentDictionary<string, ITimingWheelTask>();
        var now = DateTime.Now;
        tw_s = new TimingWheel(60);
        tw_m = new TimingWheel(60);
        tw_h = new TimingWheel(24);
        tw_D = new TimingWheel(DateTime.DaysInMonth(now.Year, now.Month), 31)
        {
            OnReset = tw =>
            {
                var nextDate = DateTime.Now.AddDays(1);
                tw.Total = DateTime.DaysInMonth(nextDate.Year, nextDate.Month);
            }
        };
        tw_M = new TimingWheel(12);
        tw_Y = new TimingWheel(10000);
        tw_s.AddSubClock(tw_m);
        tw_m.AddSubClock(tw_h);
        tw_h.AddSubClock(tw_D);
        tw_D.AddSubClock(tw_M);
        tw_M.AddSubClock(tw_Y);
        TimeSync();
    }

    public void TimeSync()
    {
        var now = DateTime.Now;
        tw_h.Point = now.Hour;
        tw_m.Point = now.Minute;
        tw_s.Point = now.Second;
        tw_Y.Point = now.Year;
        tw_M.Point = now.Month;
        tw_D.Point = now.Day;
        tw_D.Total = DateTime.DaysInMonth(now.Year, now.Month);
    }

    public DateTime GetClockDateTime()
    {
        return new DateTime(tw_Y.Point, tw_M.Point, tw_D.Point, tw_h.Point, tw_m.Point, tw_s.Point,
            DateTimeKind.Local);
    }

    public ITimingWheel[] GetClockChain()
    {
        return new TimingWheel[] { tw_Y, tw_M, tw_D, tw_h, tw_m, tw_s };
    }

    public async Task StartLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            await Task.Delay(1000, cancellationToken);
            var nDate = DateTimeOffset.Now;
            var cdateTime = GetClockDateTime();
            var c = (int)(nDate - cdateTime).TotalSeconds;
            if (c <= 0)
                tw_s.Forward();
            else
                for (int i = 0; i < c; i++)
                    tw_s.Forward();
        }
    }

    public List<ITimingWheelTask> GetTasks()
    {
        return _tasks.Values.ToList();
    }

    public void AddTask(ITimingWheelTask task)
    {
        _tasks.TryAdd(task.ID, task);
        tw_Y.AddTask(task);
    }

    public ITimingWheelTask? GetTask(string id)
    {
        _tasks.TryGetValue(id, out var task);
        return task;
    }

    public void RemoveTask(string id)
    {
        _tasks.TryRemove(id, out var task);
        task?.Cancel();
    }
}

public class TimingWheel : ITimingWheel
{
    public int Total { get; set; }
    private int _point = 0;
    private object _pointLock = new object();

    public Action<ITimingWheel>? OnReset { get; set; }

    public int Point
    {
        get
        {
            lock (_pointLock)
            {
                return _point;
            }
        }
        set
        {
            lock (_pointLock)
            {
                _point = value;
            }
        }
    }

    public int DefaultPoint { get; set; } = 0;
    public int Interval { get; set; } = 1;
    public ITimingWheel? ParentClock { get; set; } = null;
    protected ConcurrentBag<ITimingWheel> SubClock { get; set; } = new ConcurrentBag<ITimingWheel>();
    protected ConcurrentQueue<ITimingWheelTask>[] TaskSlots { get; set; }

    protected ConcurrentQueue<ITimingWheelTask> WaitAddTaskSlot { get; set; } =
        new ConcurrentQueue<ITimingWheelTask>();

    public TimingWheel(int total = 60, int? slotMax = null)
    {
        Total = total;
        TaskSlots = new ConcurrentQueue<ITimingWheelTask>[slotMax ?? total];
        for (int i = 0; i < TaskSlots.Length; i++)
        {
            TaskSlots[i] = new ConcurrentQueue<ITimingWheelTask>();
        }
    }

    public virtual void GetParentClockChain(List<ITimingWheel> ret)
    {
        ret.Insert(0, this);
        if (ParentClock != null)
        {
            ParentClock.GetParentClockChain(ret);
        }
    }

    public virtual ITimingWheel AddSubClock(ITimingWheel clock)
    {
        clock.ParentClock = this;
        SubClock.Add(clock);
        return clock;
    }

    public virtual void Forward()
    {
        if (Point + Interval >= Total)
        {
            OnReset?.Invoke(this);
        }

        CallAddTask();
        Set(Point + Interval);
        CallTaskEvent();
    }

    protected virtual void CallTaskEvent()
    {
        var tasklist = TaskSlots[Point];
        while (tasklist.TryDequeue(out var task))
        {
            if (task.IsCancel())
                continue;
            if (ParentClock != null && task.SlotPointIndex - 1 >= 0 && task.SlotPointList != null &&
                task.SlotPointList.Count > 0 && task.SlotPointList[task.SlotPointIndex - 1] != 0)
            {
                task.SlotPointIndex--;
                ParentClock.AddWaitTask(task);
            }
            else
            {
                var currentTask = task;
                Task.Run(() =>
                {
                    try
                    {
                        currentTask.TaskCallback?.Invoke(currentTask, currentTask.TaskValue);
                    }
                    catch (Exception e)
                    {
                        currentTask.TaskExceptionCallback?.Invoke(e);
                    }
                    finally
                    {
                        if (currentTask is TimingWheelCronTask cronTask)
                        {
                            cronTask.UpdateNextInterval();
                        }

                        if (currentTask.InitAddClock != null)
                        {
                            currentTask.SlotPointList = null;
                            currentTask.SlotPointIndex = 0;
                            currentTask.InitAddClock.AddTask(currentTask);
                        }
                    }
                });
            }
        }
    }

    protected virtual void CallAddTask()
    {
        var tpoint = Point;
        while (WaitAddTaskSlot.TryDequeue(out var task))
        {
            var new_point = task.StepInterval;
            if (task.SlotPointList == null)
            {
                if (new_point + tpoint >= Total)
                {
                    new_point = new_point + tpoint - Total;
                }
                else
                {
                    new_point += tpoint;
                }
            }
            else
            {
                new_point = task.SlotPointList[task.SlotPointIndex];
            }

            TaskSlots[new_point].Enqueue(task);
        }
    }

    public virtual void AddTask(ITimingWheelTask task)
    {
        var clockChain = new List<ITimingWheel>();
        GetParentClockChain(clockChain);
        var totals = 1L;
        foreach (var clock in clockChain)
        {
            totals *= clock.Total;
        }

        if (totals < task.StepInterval)
        {
            throw new Exception("total delay is too large");
        }

        ITimingWheel? lastClock = null;
        if (task.SlotPointList == null)
        {
            task.SlotPointList = new List<int>();
            var sdelay = task.StepInterval;
            foreach (var item in clockChain)
            {
                var tpoint = item.Point;
                if (sdelay <= item.Total)
                {
                    if (sdelay + tpoint >= item.Total)
                    {
                        sdelay = sdelay + tpoint - item.Total;
                    }
                    else
                    {
                        sdelay += tpoint;
                    }

                    task.SlotPointList.Add(sdelay);
                    lastClock = item;
                    break;
                }

                var npoint = sdelay % item.Total;
                sdelay = (sdelay - npoint) / item.Total;
                if (npoint + tpoint >= item.Total)
                {
                    npoint = npoint + tpoint - item.Total;
                    sdelay++;
                }
                else
                {
                    npoint += tpoint;
                }

                task.SlotPointList.Add(npoint);
            }

            task.SlotPointIndex = task.SlotPointList.Count - 1;
        }
        else
        {
            lastClock = clockChain[task.SlotPointIndex];
        }

        if (task.InitAddClock == null)
        {
            task.InitAddClock = lastClock;
        }

        if (lastClock == null)
        {
            throw new Exception();
        }

        lastClock.AddWaitTask(task);
    }

    public virtual void AddWaitTask(ITimingWheelTask task)
    {
        task.RunningClock = this;
        WaitAddTaskSlot.Enqueue(task);
    }

    private void Set(int value)
    {
        if (value >= Total)
        {
            Point = DefaultPoint;
            if (SubClock.Count > 0)
            {
                Parallel.ForEach(SubClock, clock => { clock.Forward(); });
            }
        }
        else
        {
            Point = value;
        }
    }
}

public class TimingWheelCronTask : TimingWheelTask
{
    public string Cron { get; set; }

    private int _initStepInterval;

    public override int StepInterval => _initStepInterval;

    public void UpdateNextInterval()
    {
        _initStepInterval = GetNextExecutionTimeTotalSeconds(Cron);
    }

    private ITimingWheel _YClock;
    private ITimingWheel _MClock;
    private ITimingWheel _DClock;
    private ITimingWheel _hClock;
    private ITimingWheel _mClock;
    private ITimingWheel _sClock;

    private int GetNextExecutionTimeTotalSeconds(string cron)
    {
        var cronExpression = CronExpression.Parse(cron, CronFormat.IncludeSeconds);
        var dateTime = new DateTime(_YClock.Point, _MClock.Point, _DClock.Point, _hClock.Point, _mClock.Point,
            _sClock.Point, DateTimeKind.Utc);
        var nextTime = cronExpression.GetNextOccurrence(dateTime)!.Value;
        var ret = (int)(nextTime - dateTime).TotalSeconds;
        return ret;
    }

    public TimingWheelCronTask(string name, string cron, ITimingWheel[] clockChain, TWTaskEvent callback,
        params object?[]? taskValue) :
        base(name, 1, callback, taskValue)
    {
        Cron = cron;
        TaskValue = taskValue;
        _YClock = clockChain[0];
        _MClock = clockChain[1];
        _DClock = clockChain[2];
        _hClock = clockChain[3];
        _mClock = clockChain[4];
        _sClock = clockChain[5];
        UpdateNextInterval();
    }
}

public class TimingWheelTask : ITimingWheelTask
{
    public virtual string ID { get; }

    public virtual string Name { get; set; } = "";

    private int _stepInterval = 1;

    public virtual int StepInterval
    {
        get { return _stepInterval; }
        set
        {
            if (value <= 0)
            {
                throw new Exception("the minimum step interval is 1");
            }

            _stepInterval = value;
        }
    }

    public virtual TWTaskEvent? TaskCallback { get; set; } = null;

    public Action<Exception>? TaskExceptionCallback { get; set; } = null;

    public virtual object?[]? TaskValue { get; set; } = null;

    public virtual ITimingWheel? InitAddClock { get; set; } = null;

    public ITimingWheel? RunningClock { get; set; } = null;

    public virtual List<int>? SlotPointList { get; set; } = null;

    public TWTaskEventN? OnCancel { get; set; }

    public virtual int SlotPointIndex { get; set; } = 0;

    private bool _isCancel = false;

    public TimingWheelTask(string name, int stepInterval, TWTaskEvent? callback = null, params object?[]? taskValue)
    {
        ID = Guid.NewGuid().ToString();
        Name = name;
        StepInterval = stepInterval;
        TaskCallback = callback;
        TaskValue = taskValue;
    }

    public virtual bool IsCancel()
    {
        return _isCancel;
    }

    public virtual void Cancel()
    {
        _isCancel = true;
        OnCancel?.Invoke(this, TaskValue);
    }
}

public interface ITimingWheelBase
{
    ITimingWheel AddSubClock(ITimingWheel clock);

    void Forward();

    void AddTask(ITimingWheelTask task);
}

public interface ITimingWheel : ITimingWheelBase
{
    int Point { get; set; }

    int Total { get; set; }

    int DefaultPoint { get; set; }

    ITimingWheel? ParentClock { get; set; }

    Action<ITimingWheel>? OnReset { get; set; }

    void GetParentClockChain(List<ITimingWheel> ret);

    void AddWaitTask(ITimingWheelTask task);
}

public interface ITimingWheelTask
{
    string ID { get; }

    string Name { get; set; }

    int StepInterval { get; set; }

    TWTaskEvent? TaskCallback { get; set; }

    Action<Exception>? TaskExceptionCallback { get; set; }

    object?[]? TaskValue { get; set; }

    ITimingWheel? InitAddClock { get; set; }

    ITimingWheel? RunningClock { get; set; }

    List<int>? SlotPointList { get; set; }

    TWTaskEventN? OnCancel { get; set; }

    int SlotPointIndex { get; set; }

    bool IsCancel();

    void Cancel();
}

public delegate void TWTaskEvent(ITimingWheelTask task, object?[]? param);

public delegate void TWTaskEventN(ITimingWheelTask task, object?[]? param);