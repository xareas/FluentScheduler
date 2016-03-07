﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using FluentScheduler.Model;

namespace FluentScheduler
{
    /// <summary>
    /// Controls the timer logic to execute all configured tasks.
    /// </summary>
    public static class TaskManager
    {
        private static ITaskFactory _taskFactory;

        public static ITaskFactory TaskFactory
        {
            get
            {
                return (_taskFactory = _taskFactory ?? new TaskFactory());
            }
            set
            {
                _taskFactory = value;
            }
        }

        [SuppressMessage("Microsoft.Design", "CA1009:DeclareEventHandlersCorrectly",
            Justification = "Using strong-typed GenericEventHandler<TSender, TEventArgs> event handler pattern.")]
        public static event GenericEventHandler<TaskExceptionInformation, FluentScheduler.UnhandledExceptionEventArgs> UnobservedTaskException;

        [SuppressMessage("Microsoft.Design", "CA1009:DeclareEventHandlersCorrectly",
            Justification = "Using strong-typed GenericEventHandler<TSender, TEventArgs> event handler pattern.")]
        public static event GenericEventHandler<TaskStartScheduleInformation, EventArgs> TaskStart;

        [SuppressMessage("Microsoft.Design", "CA1009:DeclareEventHandlersCorrectly",
            Justification = "Using strong-typed GenericEventHandler<TSender, TEventArgs> event handler pattern.")]
        public static event GenericEventHandler<TaskEndScheduleInformation, EventArgs> TaskEnd;

        private static ScheduleCollection _schedules = new ScheduleCollection();

        private static System.Threading.Timer _timer;

        private static readonly ConcurrentDictionary<List<Action>, bool> RunningNonReentrantTasks = new ConcurrentDictionary<List<Action>, bool>();

        private static readonly ConcurrentDictionary<Guid, Schedule> _runningSchedules = new ConcurrentDictionary<Guid, Schedule>();

        /// <summary>
        /// Gets a list of currently schedules currently executing.
        /// </summary>
        public static IEnumerable<Schedule> RunningSchedules
        {
            get
            {
                return _runningSchedules.Values.ToList();
            }
        }

        /// <summary>
        /// The list of all schedules, whether or not they are currently running.
        /// Use <see cref="GetSchedule"/> to get concrete schedule by name.
        /// </summary>
        public static IEnumerable<Schedule> AllSchedules
        {
            get
            {
                return _schedules.All();
            }
        }
        /// <summary>
        /// Get schedule by name.
        /// </summary>
        /// <param name="name">Schedule name</param>
        /// <returns>Schedule instance or null if the schedule does not exist</returns>
        public static Schedule GetSchedule(string name)
        {
            return _schedules.Get(name);
        }

        /// <summary>
        /// Initializes the task manager with all schedules configured in the specified registry
        /// </summary>
        /// <param name="registry">Registry containing task schedules</param>
        public static void Initialize(Registry registry)
        {
            if (registry == null)
                throw new ArgumentNullException("registry");

            var immediateTasks = new List<Schedule>();
            AddSchedules(registry.Schedules, immediateTasks, DateTime.Now);
            RunAndInitializeSchedule(immediateTasks);
        }

        private static void RaiseUnobservedTaskException(Schedule schedule, Task t)
        {
            var handler = UnobservedTaskException;
            if (handler != null && t.Exception != null)
            {
                var info = new TaskExceptionInformation
                {
                    Name = schedule.Name,
                    Task = t
                };
                handler(info, new FluentScheduler.UnhandledExceptionEventArgs(t.Exception.InnerException, true));
            }
        }
        private static void RaiseTaskStart(Schedule schedule, DateTime startTime)
        {
            var handler = TaskStart;
            if (handler != null)
            {
                var info = new TaskStartScheduleInformation
                {
                    Name = schedule.Name,
                    StartTime = startTime
                };
                handler(info, new EventArgs());
            }
        }
        private static void RaiseTaskEnd(Schedule schedule, DateTime startTime, TimeSpan duration)
        {
            var handler = TaskEnd;
            if (handler != null)
            {
                var info = new TaskEndScheduleInformation
                {
                    Name = schedule.Name,
                    StartTime = startTime,
                    Duration = duration
                };
                if (schedule.NextRun != default(DateTime))
                    info.NextRun = schedule.NextRun;

                handler(info, new EventArgs());
            }
        }

        private static void AddSchedules(IEnumerable<Schedule> schedules, ICollection<Schedule> immediatelyInvokedSchedules, DateTime now)
        {
            foreach (var schedule in schedules)
            {
                if (schedule.CalculateNextRun == null)
                {
                    if (schedule.DelayRunFor > TimeSpan.Zero)
                    {
                        // delayed task
                        schedule.NextRun = DateTime.Now.Add(schedule.DelayRunFor);
                        _schedules.Add(schedule);
                    }
                    else
                    {
                        // only non-delayed tasks are started right away
                        immediatelyInvokedSchedules.Add(schedule);
                    }
                    var hasAdded = false;
                    foreach (var child in schedule.AdditionalSchedules.Where(x => x.CalculateNextRun != null))
                    {
                        var nextRun = child.CalculateNextRun(now.Add(child.DelayRunFor).AddMilliseconds(1));
                        if (!hasAdded || schedule.NextRun > nextRun)
                        {
                            schedule.NextRun = nextRun;
                            hasAdded = true;
                        }
                    }
                }
                else
                {
                    schedule.NextRun = schedule.CalculateNextRun(now.Add(schedule.DelayRunFor));
                    _schedules.Add(schedule);
                }

                foreach (var childSchedule in schedule.AdditionalSchedules)
                {
                    if (childSchedule.CalculateNextRun == null)
                    {
                        if (childSchedule.DelayRunFor > TimeSpan.Zero)
                        {
                            // delayed task
                            childSchedule.NextRun = DateTime.Now.Add(childSchedule.DelayRunFor);
                            _schedules.Add(childSchedule);
                        }
                        else
                        {
                            // run immediately
                            immediatelyInvokedSchedules.Add(childSchedule);
                            continue;
                        }
                    }
                    else
                    {
                        childSchedule.NextRun = childSchedule.CalculateNextRun(now.Add(schedule.DelayRunFor));
                        _schedules.Add(childSchedule);
                    }
                }
            }
        }

        private static void RunAndInitializeSchedule(IEnumerable<Schedule> immediatelyInvokedSchedules)
        {
            foreach (var task in immediatelyInvokedSchedules)
            {
                StartTask(task);
            }

            if (!_schedules.Any())
                return;

            if (_timer == null)
            {
                _timer = new System.Threading.Timer(Timer_Callback, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            }
            _schedules.Sort();
            Schedule();
        }

        internal static void StartTask(Schedule schedule)
        {
            if (schedule.Disabled)
                return;

            if (!schedule.Reentrant)
            {
                if (!RunningNonReentrantTasks.TryAdd(schedule.Tasks, true))
                    return;
            }

            var id = Guid.NewGuid();
            _runningSchedules.TryAdd(id, schedule);

            var start = DateTime.Now;
            RaiseTaskStart(schedule, start);
            var mainTask = Task.Factory.StartNew(() =>
            {
                var stopwatch = new Stopwatch();
                try
                {
                    stopwatch.Start();
                    foreach (var action in schedule.Tasks)
                    {
                        var subTask = Task.Factory.StartNew(action);
                        subTask.Wait();
                    }
                }
                finally
                {
                    stopwatch.Stop();
                    bool notUsed;
                    RunningNonReentrantTasks.TryRemove(schedule.Tasks, out notUsed);
                    Schedule notUsedSchedule;
                    _runningSchedules.TryRemove(id, out notUsedSchedule);
                    RaiseTaskEnd(schedule, start, stopwatch.Elapsed);
                }
            }, TaskCreationOptions.PreferFairness);
            mainTask.ContinueWith(task => RaiseUnobservedTaskException(schedule, task), TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>
        /// Adds a task to the task manager
        /// </summary>
        /// <typeparam name="T">Task to schedule</typeparam>
        /// <param name="taskSchedule">Schedule for the task</param>
        [SuppressMessage("Microsoft.Design", "CA1004:GenericMethodsShouldProvideTypeParameter",
            Justification = "The 'T' requirement is on purpose.")]
        public static void AddTask<T>(Action<Schedule> taskSchedule) where T : ITask
        {
            if (taskSchedule == null)
                throw new ArgumentNullException("taskSchedule", "Please specify the task schedule to add to the task manager.");

            var schedule = new Schedule(TaskFactory.GetTaskInstance<T>())
            {
                Name = typeof(T).Name
            };
            AddTask(taskSchedule, schedule);
        }

        /// <summary>
        /// Adds a task to the task manager
        /// </summary>
        /// <param name="taskAction">Task to schedule</param>
        /// <param name="taskSchedule">Schedule for the task</param>
        public static void AddTask(Action taskAction, Action<Schedule> taskSchedule)
        {
            if (taskSchedule == null)
                throw new ArgumentNullException("taskSchedule", "Please specify the task schedule to add to the task manager.");

            var schedule = new Schedule(taskAction);
            AddTask(taskSchedule, schedule);
        }

        private static void AddTask(Action<Schedule> taskSchedule, Schedule schedule)
        {
            taskSchedule(schedule);

            var immediateTasks = new List<Schedule>();
            AddSchedules(new List<Schedule> { schedule }, immediateTasks, DateTime.Now);
            RunAndInitializeSchedule(immediateTasks);
        }

        public static void RemoveTask(string name)
        {
            _schedules.Remove(name);
        }

        /// <summary>
        /// Stops the task manager from executing tasks.
        /// </summary>
        public static void Stop()
        {
            if (_timer != null)
                _timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }

        /// <summary>
        /// Restarts the task manager if it had previously been stopped
        /// </summary>
        public static void Start()
        {
            if (_timer != null)
                Schedule();
        }

        static void Timer_Callback(object state)
        {
            Schedule();
        }

        private static void Schedule()
        {
            _timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

            var firstTask = _schedules.First();
            if (firstTask == null)
            {
                return;
            }
            if (firstTask.NextRun <= DateTime.Now)
            {
                StartTask(firstTask);
                if (firstTask.CalculateNextRun == null)
                {
                    // probably a ToRunNow().DelayFor() task, there's no CalculateNextRun
                }
                else
                {
                    firstTask.NextRun = firstTask.CalculateNextRun(DateTime.Now.AddMilliseconds(1));
                }
                if (firstTask.TaskExecutions > 0)
                {
                    firstTask.TaskExecutions--;
                }
                if (firstTask.NextRun <= DateTime.Now || firstTask.TaskExecutions == 0)
                {
                    _schedules.Remove(firstTask);
                }
                _schedules.Sort();
                Schedule();
                return;
            }

            var timerInterval = firstTask.NextRun - DateTime.Now;
            if (timerInterval <= TimeSpan.Zero)
            {
                Schedule();
                return;
            }

            _timer.Change(timerInterval, timerInterval);
        }
    }
}
