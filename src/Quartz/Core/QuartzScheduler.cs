#region License

/* 
 * All content copyright Terracotta, Inc., unless otherwise indicated. All rights reserved. 
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not 
 * use this file except in compliance with the License. You may obtain a copy 
 * of the License at 
 * 
 *   http://www.apache.org/licenses/LICENSE-2.0 
 *   
 * Unless required by applicable law or agreed to in writing, software 
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT 
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the 
 * License for the specific language governing permissions and limitations 
 * under the License.
 * 
 */

#endregion

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
#if REMOTING
using System.Runtime.Remoting;
#endif // REMOTING
using System.Security;
using System.Text;
using System.Threading.Tasks;

using Quartz.Impl;
using Quartz.Impl.Matchers;
using Quartz.Impl.Triggers;
using Quartz.Logging;
using Quartz.Simpl;
using Quartz.Spi;
using Quartz.Util;

namespace Quartz.Core
{
    /// <summary>
    /// This is the heart of Quartz, an indirect implementation of the <see cref="IScheduler" />
    /// interface, containing methods to schedule <see cref="IJob" />s,
    /// register <see cref="IJobListener" /> instances, etc.
    /// </summary>
    /// <seealso cref="IScheduler" />
    /// <seealso cref="QuartzSchedulerThread" />
    /// <seealso cref="IJobStore" />
    /// <seealso cref="IThreadPool" />
    /// <author>James House</author>
    /// <author>Marko Lahma (.NET)</author>
    public class QuartzScheduler : 
#if REMOTING
        MarshalByRefObject,
#endif // REMOTING
        IRemotableQuartzScheduler
    {
        private readonly ILog log;
        private static readonly Version version;

        private readonly QuartzSchedulerResources resources;

        private readonly QuartzSchedulerThread schedThread;

        private readonly ConcurrentDictionary<string, IJobListener> internalJobListeners = new ConcurrentDictionary<string, IJobListener>();
        private readonly ConcurrentDictionary<string, ITriggerListener> internalTriggerListeners = new ConcurrentDictionary<string, ITriggerListener>();
        private readonly List<ISchedulerListener> internalSchedulerListeners = new List<ISchedulerListener>(10);

        private IJobFactory jobFactory = new PropertySettingJobFactory();
        private readonly ExecutingJobsManager jobMgr;
        private readonly Random random = new Random();
        private readonly List<object> holdToPreventGc = new List<object>(5);
        private volatile bool closed;
        private volatile bool shuttingDown;
        private DateTimeOffset? initialStart;
        private bool boundRemotely;

        /// <summary>
        /// Initializes the <see cref="QuartzScheduler"/> class.
        /// </summary>
        static QuartzScheduler()
        {
            var asm = Assembly.GetAssembly(typeof (QuartzScheduler));

            if (asm != null)
            {
                version = asm.GetName().Version;
            }
        }

        /// <summary>
        /// Gets the version of the Quartz Scheduler.
        /// </summary>
        /// <value>The version.</value>
        public string Version => version.ToString();

        /// <summary>
        /// Gets the version major.
        /// </summary>
        /// <value>The version major.</value>
        public static string VersionMajor => version.Major.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Gets the version minor.
        /// </summary>
        /// <value>The version minor.</value>
        public static string VersionMinor => version.Minor.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Gets the version iteration.
        /// </summary>
        /// <value>The version iteration.</value>
        public static string VersionIteration => version.Build.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Gets the scheduler signaler.
        /// </summary>
        /// <value>The scheduler signaler.</value>
        public virtual ISchedulerSignaler SchedulerSignaler { get; }

        /// <summary>
        /// Returns the name of the <see cref="QuartzScheduler" />.
        /// </summary>
        public virtual string SchedulerName => resources.Name;

        /// <summary> 
        /// Returns the instance Id of the <see cref="QuartzScheduler" />.
        /// </summary>
        public virtual string SchedulerInstanceId => resources.InstanceId;

        /// <summary>
        /// Returns the <see cref="SchedulerContext" /> of the <see cref="IScheduler" />.
        /// </summary>
        public virtual SchedulerContext SchedulerContext { get; } = new SchedulerContext();

        /// <summary>
        /// Gets or sets a value indicating whether to signal on scheduling change.
        /// </summary>
        /// <value>
        /// 	<c>true</c> if scheduler should signal on scheduling change; otherwise, <c>false</c>.
        /// </value>
        public virtual bool SignalOnSchedulingChange { get; set; } = true;

        /// <summary>
        /// Reports whether the <see cref="IScheduler" /> is paused.
        /// </summary>
        public virtual bool InStandbyMode => schedThread.Paused;

        /// <summary>
        /// Gets the job store class.
        /// </summary>
        /// <value>The job store class.</value>
        public virtual Type JobStoreClass => resources.JobStore.GetType();

        /// <summary>
        /// Gets the thread pool class.
        /// </summary>
        /// <value>The thread pool class.</value>
        public virtual Type ThreadPoolClass => resources.ThreadPool.GetType();

        /// <summary>
        /// Gets the size of the thread pool.
        /// </summary>
        /// <value>The size of the thread pool.</value>
        public virtual int ThreadPoolSize => resources.ThreadPool.PoolSize;

        /// <summary>
        /// Reports whether the <see cref="IScheduler" /> has been Shutdown.
        /// </summary>
        public virtual bool IsShutdown => closed;

        public virtual bool IsShuttingDown => shuttingDown;

        public virtual bool IsStarted => !shuttingDown && !closed && !InStandbyMode && initialStart != null;

        /// <summary>
        /// Return a list of <see cref="ICancellableJobExecutionContext" /> objects that
        /// represent all currently executing Jobs in this Scheduler instance.
        /// <para>
        /// This method is not cluster aware.  That is, it will only return Jobs
        /// currently executing in this Scheduler instance, not across the entire
        /// cluster.
        /// </para>
        /// <para>
        /// Note that the list returned is an 'instantaneous' snap-shot, and that as
        /// soon as it's returned, the true list of executing jobs may be different.
        /// </para>
        /// </summary>
        public virtual IReadOnlyList<IJobExecutionContext> CurrentlyExecutingJobs => jobMgr.ExecutingJobs;

        /// <summary>
        /// Register the given <see cref="ISchedulerListener" /> with the
        /// <see cref="IScheduler" />'s list of internal listeners.
        /// </summary>
        /// <param name="schedulerListener"></param>
        public void AddInternalSchedulerListener(ISchedulerListener schedulerListener)
        {
            lock (internalSchedulerListeners)
            {
                internalSchedulerListeners.Add(schedulerListener);
            }
        }

        /// <summary>
        /// Remove the given <see cref="ISchedulerListener" /> from the
        /// <see cref="IScheduler" />'s list of internal listeners.
        /// </summary>
        /// <param name="schedulerListener"></param>
        /// <returns>true if the identified listener was found in the list, andremoved.</returns>
        public bool RemoveInternalSchedulerListener(ISchedulerListener schedulerListener)
        {
            lock (internalSchedulerListeners)
            {
                return internalSchedulerListeners.Remove(schedulerListener);
            }
        }

        /// <summary>
        /// Get a List containing all of the <i>internal</i> <see cref="ISchedulerListener" />s
        /// registered with the <see cref="IScheduler" />.
        /// </summary>
        public IReadOnlyList<ISchedulerListener> InternalSchedulerListeners
        {
            get
            {
                lock (internalSchedulerListeners)
                {
                    return new List<ISchedulerListener>(internalSchedulerListeners);
                }
            }
        }

        /// <summary>
        /// Gets or sets the job factory.
        /// </summary>
        /// <value>The job factory.</value>
        public virtual IJobFactory JobFactory
        {
            get { return jobFactory; }
            set
            {
                if (value == null)
                {
                    throw new ArgumentException("JobFactory cannot be set to null!");
                }

                log.Info("JobFactory set to: " + value);

                jobFactory = value;
            }
        }

        // ReSharper disable once MemberCanBePrivate.Global
        protected QuartzScheduler()
        {
            log = LogProvider.GetLogger(GetType());
        }

        /// <summary>
        /// Create a <see cref="QuartzScheduler" /> with the given configuration
        /// properties.
        /// </summary>
        /// <seealso cref="QuartzSchedulerResources" />
        public QuartzScheduler(QuartzSchedulerResources resources, TimeSpan idleWaitTime) : this()
        {
            this.resources = resources;

            if (resources.JobStore is IJobListener)
            {
                AddInternalJobListener((IJobListener) resources.JobStore);
            }

            schedThread = new QuartzSchedulerThread(this, resources);
            schedThread.Start();

            if (idleWaitTime > TimeSpan.Zero)
            {
                schedThread.IdleWaitTime = idleWaitTime;
            }

            jobMgr = new ExecutingJobsManager();
            AddInternalJobListener(jobMgr);
            var errLogger = new ErrorLogger();
            AddInternalSchedulerListener(errLogger);

            SchedulerSignaler = new SchedulerSignalerImpl(this, schedThread);

            log.InfoFormat("Quartz Scheduler v.{0} created.", Version);
        }

        public void Initialize()
        {
            try
            {
                Bind();
            }
            catch (Exception re)
            {
                throw new SchedulerException(
                    "Unable to bind scheduler to remoting.", re);
            }

            log.Info("Scheduler meta-data: " +
                     (new SchedulerMetaData(SchedulerName, SchedulerInstanceId, GetType(), boundRemotely, RunningSince != null,
                         InStandbyMode, IsShutdown, RunningSince,
                         NumJobsExecuted, JobStoreClass,
                         SupportsPersistence, Clustered, ThreadPoolClass,
                         ThreadPoolSize, Version)));
        }

        /// <summary>
        /// Bind the scheduler to remoting infrastructure.
        /// </summary>
        private void Bind()
        {
            if (resources.SchedulerExporter != null)
            {
                resources.SchedulerExporter.Bind(this);
                boundRemotely = true;
            }
        }

        /// <summary>
        /// Un-bind the scheduler from remoting infrastructure.
        /// </summary>
        private void UnBind()
        {
            resources.SchedulerExporter?.UnBind(this);
        }

        /// <summary>
        /// Adds an object that should be kept as reference to prevent
        /// it from being garbage collected.
        /// </summary>
        /// <param name="obj">The obj.</param>
        public virtual void AddNoGCObject(object obj)
        {
            holdToPreventGc.Add(obj);
        }

        /// <summary>
        /// Removes the object from garbage collection protected list.
        /// </summary>
        /// <param name="obj">The obj.</param>
        /// <returns></returns>
        public virtual bool RemoveNoGCObject(object obj)
        {
            return holdToPreventGc.Remove(obj);
        }

        /// <summary>
        /// Starts the <see cref="QuartzScheduler" />'s threads that fire <see cref="ITrigger" />s.
        /// <para>
        /// All <see cref="ITrigger" />s that have misfired will
        /// be passed to the appropriate TriggerListener(s).
        /// </para>
        /// </summary>
        public virtual async Task Start()
        {
            if (shuttingDown || closed)
            {
                throw new SchedulerException("The Scheduler cannot be restarted after Shutdown() has been called.");
            }

            await NotifySchedulerListenersStarting().ConfigureAwait(false);

            if (!initialStart.HasValue)
            {
                initialStart = SystemTime.UtcNow();
                await resources.JobStore.SchedulerStarted().ConfigureAwait(false);
                await StartPlugins().ConfigureAwait(false);
            }
            else
            {
                await resources.JobStore.SchedulerResumed().ConfigureAwait(false);
            }

            schedThread.TogglePause(false);

            log.Info($"Scheduler {resources.GetUniqueIdentifier()} started.");

            await NotifySchedulerListenersStarted().ConfigureAwait(false);
        }

        public virtual Task StartDelayed(TimeSpan delay)
        {
            if (shuttingDown || closed)
            {
                throw new SchedulerException(
                    "The Scheduler cannot be restarted after Shutdown() has been called.");
            }
            return Task.Run(async () =>
            {
                await Task.Delay(delay).ConfigureAwait(false);

                try
                {
                    await Start().ConfigureAwait(false);
                }
                catch (SchedulerException se)
                {
                    log.ErrorException("Unable to start scheduler after startup delay.", se);
                }
            });
        }

        /// <summary>
        /// Temporarily halts the <see cref="QuartzScheduler" />'s firing of <see cref="ITrigger" />s.
        /// <para>
        /// The scheduler is not destroyed, and can be re-started at any time.
        /// </para>
        /// </summary>
        public virtual async Task Standby()
        {
            await resources.JobStore.SchedulerPaused().ConfigureAwait(false);
            schedThread.TogglePause(true);
            log.Info($"Scheduler {resources.GetUniqueIdentifier()} paused.");
            await NotifySchedulerListenersInStandbyMode().ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the running since.
        /// </summary>
        /// <value>The running since.</value>
        public virtual DateTimeOffset? RunningSince => initialStart;

        /// <summary>
        /// Gets the number of jobs executed.
        /// </summary>
        /// <value>The number of jobs executed.</value>
        public virtual int NumJobsExecuted => jobMgr.NumJobsFired;

        /// <summary>
        /// Gets a value indicating whether this scheduler supports persistence.
        /// </summary>
        /// <value><c>true</c> if supports persistence; otherwise, <c>false</c>.</value>
        public virtual bool SupportsPersistence => resources.JobStore.SupportsPersistence;

        public virtual bool Clustered => resources.JobStore.Clustered;

        /// <summary>
        /// Halts the <see cref="QuartzScheduler" />'s firing of <see cref="ITrigger" />s,
        /// and cleans up all resources associated with the QuartzScheduler.
        /// Equivalent to <see cref="Shutdown(bool)" />.
        /// <para>
        /// The scheduler cannot be re-started.
        /// </para>
        /// </summary>
        public virtual Task Shutdown()
        {
            return Shutdown(false);
        }

        /// <summary>
        /// Halts the <see cref="QuartzScheduler" />'s firing of <see cref="ITrigger" />s,
        /// and cleans up all resources associated with the QuartzScheduler.
        /// <para>
        /// The scheduler cannot be re-started.
        /// </para>
        /// </summary>
        /// <param name="waitForJobsToComplete">
        /// if <see langword="true" /> the scheduler will not allow this method
        /// to return until all currently executing jobs have completed.
        /// </param>
        public virtual async Task Shutdown(bool waitForJobsToComplete)
        {
            if (shuttingDown || closed)
            {
                return;
            }

            shuttingDown = true;

            log.InfoFormat("Scheduler {0} shutting down.", resources.GetUniqueIdentifier());

            await Standby().ConfigureAwait(false);

            await schedThread.Halt(waitForJobsToComplete).ConfigureAwait(false);

            await NotifySchedulerListenersShuttingdown().ConfigureAwait(false);

            if ((resources.InterruptJobsOnShutdown && !waitForJobsToComplete) || (resources.InterruptJobsOnShutdownWithWait && waitForJobsToComplete))
            {
                var jobs = CurrentlyExecutingJobs;
                foreach (ICancellableJobExecutionContext job in jobs)
                {
                    try
                    {
                        job.Cancel();
                    }
                    catch (Exception ex)
                    {
                        // TODO: Is this still needed? Probably not.
                        // do nothing, this was just a courtesy effort
                        log.WarnFormat("Encountered error when interrupting job {0} during shutdown: {1}", job.JobDetail.Key, ex);
                    }
                }
            }

            resources.ThreadPool.Shutdown(waitForJobsToComplete);

            // Scheduler thread may have be waiting for the fire time of an acquired 
            // trigger and need time to release the trigger once halted, so make sure
            // the thread is dead before continuing to shutdown the job store.
            await schedThread.Shutdown().ConfigureAwait(false);

            closed = true;

            if (boundRemotely)
            {
                try
                {
                    UnBind();
                }
#if REMOTING
                catch (RemotingException)
#else // REMOTING
                catch (Exception) // TODO (NetCore Port): Determine the correct exception type
#endif // REMOTING
                {
                }
            }

            await ShutdownPlugins().ConfigureAwait(false);

            await resources.JobStore.Shutdown().ConfigureAwait(false);

            await NotifySchedulerListenersShutdown().ConfigureAwait(false);

            SchedulerRepository.Instance.Remove(resources.Name);

            holdToPreventGc.Clear();

            log.Info($"Scheduler {resources.GetUniqueIdentifier()} Shutdown complete.");
        }

        /// <summary>
        /// Validates the state.
        /// </summary>
        public virtual void ValidateState()
        {
            if (IsShutdown)
            {
                throw new SchedulerException("The Scheduler has been Shutdown.");
            }

            // other conditions to check (?)
        }

        /// <summary> 
        /// Add the <see cref="IJob" /> identified by the given
        /// <see cref="IJobDetail" /> to the Scheduler, and
        /// associate the given <see cref="ITrigger" /> with it.
        /// <para>
        /// If the given Trigger does not reference any <see cref="IJob" />, then it
        /// will be set to reference the Job passed with it into this method.
        /// </para>
        /// </summary>
        public virtual async Task<DateTimeOffset> ScheduleJob(IJobDetail jobDetail, ITrigger trigger)
        {
            ValidateState();

            if (jobDetail == null)
            {
                throw new SchedulerException("JobDetail cannot be null");
            }

            if (trigger == null)
            {
                throw new SchedulerException("Trigger cannot be null");
            }

            if (jobDetail.Key == null)
            {
                throw new SchedulerException("Job's key cannot be null");
            }

            if (jobDetail.JobType == null)
            {
                throw new SchedulerException("Job's class cannot be null");
            }

            IOperableTrigger trig = (IOperableTrigger) trigger;

            if (trigger.JobKey == null)
            {
                trig.JobKey = jobDetail.Key;
            }
            else if (!trigger.JobKey.Equals(jobDetail.Key))
            {
                throw new SchedulerException("Trigger does not reference given job!");
            }

            trig.Validate();

            ICalendar cal = null;
            if (trigger.CalendarName != null)
            {
                cal = await resources.JobStore.RetrieveCalendar(trigger.CalendarName).ConfigureAwait(false);
                if (cal == null)
                {
                    throw new SchedulerException($"Calendar not found: {trigger.CalendarName}");
                }
            }

            DateTimeOffset? ft = trig.ComputeFirstFireTimeUtc(cal);

            if (!ft.HasValue)
            {
                var message = $"Based on configured schedule, the given trigger '{trigger.Key}' will never fire.";
                throw new SchedulerException(message);
            }

            await resources.JobStore.StoreJobAndTrigger(jobDetail, trig).ConfigureAwait(false);
            await NotifySchedulerListenersJobAdded(jobDetail).ConfigureAwait(false);
            NotifySchedulerThread(trigger.GetNextFireTimeUtc());
            await NotifySchedulerListenersScheduled(trigger).ConfigureAwait(false);

            return ft.Value;
        }

        /// <summary>
        /// Schedule the given <see cref="ITrigger" /> with the
        /// <see cref="IJob" /> identified by the <see cref="ITrigger" />'s settings.
        /// </summary>
        public virtual async Task<DateTimeOffset> ScheduleJob(ITrigger trigger)
        {
            ValidateState();

            if (trigger == null)
            {
                throw new SchedulerException("Trigger cannot be null");
            }

            IOperableTrigger trig = (IOperableTrigger) trigger;
            trig.Validate();

            ICalendar cal = null;
            if (trigger.CalendarName != null)
            {
                cal = await resources.JobStore.RetrieveCalendar(trigger.CalendarName).ConfigureAwait(false);
                if (cal == null)
                {
                    throw new SchedulerException($"Calendar not found: {trigger.CalendarName}");
                }
            }

            DateTimeOffset? ft = trig.ComputeFirstFireTimeUtc(cal);

            if (!ft.HasValue)
            {
                var message = $"Based on configured schedule, the given trigger '{trigger.Key}' will never fire.";
                throw new SchedulerException(message);
            }

            await resources.JobStore.StoreTrigger(trig, false).ConfigureAwait(false);
            NotifySchedulerThread(trigger.GetNextFireTimeUtc());
            await NotifySchedulerListenersScheduled(trigger).ConfigureAwait(false);

            return ft.Value;
        }

        /// <summary>
        /// Add the given <see cref="IJob" /> to the Scheduler - with no associated
        /// <see cref="ITrigger" />. The <see cref="IJob" /> will be 'dormant' until
        /// it is scheduled with a <see cref="ITrigger" />, or <see cref="IScheduler.TriggerJob(Quartz.JobKey)" />
        /// is called for it.
        /// <para>
        /// The <see cref="IJob" /> must by definition be 'durable', if it is not,
        /// SchedulerException will be thrown.
        /// </para>
        /// </summary>
        public virtual Task AddJob(IJobDetail jobDetail, bool replace)
        {
            return AddJob(jobDetail, replace, false);
        }

        public virtual async Task AddJob(IJobDetail jobDetail, bool replace, bool storeNonDurableWhileAwaitingScheduling)
        {
            ValidateState();

            if (!storeNonDurableWhileAwaitingScheduling && !jobDetail.Durable)
            {
                throw new SchedulerException("Jobs added with no trigger must be durable.");
            }

            await resources.JobStore.StoreJob(jobDetail, replace).ConfigureAwait(false);
            NotifySchedulerThread(null);
            await NotifySchedulerListenersJobAdded(jobDetail).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete the identified <see cref="IJob" /> from the Scheduler - and any
        /// associated <see cref="ITrigger" />s.
        /// </summary>
        /// <returns> true if the Job was found and deleted.</returns>
        public virtual async Task<bool> DeleteJob(JobKey jobKey)
        {
            ValidateState();

            bool result = false;
            var triggers = await GetTriggersOfJob(jobKey).ConfigureAwait(false);
            foreach (ITrigger trigger in triggers)
            {
                if (!await UnscheduleJob(trigger.Key).ConfigureAwait(false))
                {
                    StringBuilder sb = new StringBuilder()
                        .Append("Unable to unschedule trigger [")
                        .Append(trigger.Key).Append("] while deleting job [")
                        .Append(jobKey).Append("]");
                    throw new SchedulerException(sb.ToString());
                }
                result = true;
            }

            result = await resources.JobStore.RemoveJob(jobKey).ConfigureAwait(false) || result;
            if (result)
            {
                NotifySchedulerThread(null);
                await NotifySchedulerListenersJobDeleted(jobKey).ConfigureAwait(false);
            }
            return result;
        }

        public virtual async Task<bool> DeleteJobs(IList<JobKey> jobKeys)
        {
            ValidateState();

            bool result = await resources.JobStore.RemoveJobs(jobKeys).ConfigureAwait(false);
            NotifySchedulerThread(null);
            foreach (JobKey key in jobKeys)
            {
                await NotifySchedulerListenersJobDeleted(key).ConfigureAwait(false);
            }
            return result;
        }

        public virtual async Task ScheduleJobs(IDictionary<IJobDetail, ISet<ITrigger>> triggersAndJobs, bool replace)
        {
            ValidateState();

            // make sure all triggers refer to their associated job
            foreach (IJobDetail job in triggersAndJobs.Keys)
            {
                if (job == null) // there can be one of these (for adding a bulk set of triggers for pre-existing jobs)
                {
                    continue;
                }
                ISet<ITrigger> triggers = triggersAndJobs[job];
                if (triggers == null) // this is possible because the job may be durable, and not yet be having triggers
                {
                    continue;
                }
                foreach (IOperableTrigger trigger in triggers)
                {
                    trigger.JobKey = job.Key;

                    trigger.Validate();

                    ICalendar cal = null;
                    if (trigger.CalendarName != null)
                    {
                        cal = await resources.JobStore.RetrieveCalendar(trigger.CalendarName).ConfigureAwait(false);
                        if (cal == null)
                        {
                            throw new SchedulerException(
                                "Calendar '" + trigger.CalendarName + "' not found for trigger: " + trigger.Key);
                        }
                    }

                    DateTimeOffset? ft = trigger.ComputeFirstFireTimeUtc(cal);

                    if (ft == null)
                    {
                        var message = $"Based on configured schedule, the given trigger '{trigger.Key}' will never fire.";
                        throw new SchedulerException(message);
                    }
                }
            }

            await resources.JobStore.StoreJobsAndTriggers(triggersAndJobs, replace).ConfigureAwait(false);
            NotifySchedulerThread(null);
            await Task.WhenAll(triggersAndJobs.Keys.Select(NotifySchedulerListenersJobAdded)).ConfigureAwait(false);
        }

        public virtual Task ScheduleJob(IJobDetail jobDetail, ISet<ITrigger> triggersForJob, bool replace)
        {
            var triggersAndJobs = new Dictionary<IJobDetail, ISet<ITrigger>>();
            triggersAndJobs.Add(jobDetail, triggersForJob);
            return ScheduleJobs(triggersAndJobs, replace);
        }

        public virtual async Task<bool> UnscheduleJobs(IList<TriggerKey> triggerKeys)
        {
            ValidateState();

            bool result = await resources.JobStore.RemoveTriggers(triggerKeys).ConfigureAwait(false);
            NotifySchedulerThread(null);
            await Task.WhenAll(triggerKeys.Select(NotifySchedulerListenersUnscheduled)).ConfigureAwait(false);
            return result;
        }

        /// <summary>
        /// Remove the indicated <see cref="ITrigger" /> from the
        /// scheduler.
        /// </summary>
        public virtual async Task<bool> UnscheduleJob(TriggerKey triggerKey)
        {
            ValidateState();

            if (await resources.JobStore.RemoveTrigger(triggerKey).ConfigureAwait(false))
            {
                NotifySchedulerThread(null);
                await NotifySchedulerListenersUnscheduled(triggerKey).ConfigureAwait(false);
            }
            else
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Remove (delete) the <see cref="ITrigger" /> with the
        /// given name, and store the new given one - which must be associated
        /// with the same job.
        /// </summary>
        /// <param name="triggerKey">the key of the trigger</param>
        /// <param name="newTrigger">The new <see cref="ITrigger" /> to be stored.</param>
        /// <returns>
        /// 	<see langword="null" /> if a <see cref="ITrigger" /> with the given
        /// name and group was not found and removed from the store, otherwise
        /// the first fire time of the newly scheduled trigger.
        /// </returns>
        public virtual async Task<DateTimeOffset?> RescheduleJob(TriggerKey triggerKey, ITrigger newTrigger)
        {
            ValidateState();

            if (triggerKey == null)
            {
                throw new ArgumentException("triggerKey cannot be null");
            }
            if (newTrigger == null)
            {
                throw new ArgumentException("newTrigger cannot be null");
            }

            var trigger = (IOperableTrigger) newTrigger;
            ITrigger oldTrigger = await GetTrigger(triggerKey).ConfigureAwait(false);
            if (oldTrigger == null)
            {
                return null;
            }

            trigger.JobKey = oldTrigger.JobKey;
            trigger.Validate();

            ICalendar cal = null;
            if (newTrigger.CalendarName != null)
            {
                cal = await resources.JobStore.RetrieveCalendar(newTrigger.CalendarName).ConfigureAwait(false);
            }

            DateTimeOffset? ft;
            if (trigger.GetNextFireTimeUtc() != null)
            {
                // use a cloned trigger so that we don't lose possible forcefully set next fire time
                var clonedTrigger = (IOperableTrigger) trigger.Clone();
                ft = clonedTrigger.ComputeFirstFireTimeUtc(cal);
            }
            else
            {
                ft = trigger.ComputeFirstFireTimeUtc(cal);
            }

            if (!ft.HasValue)
            {
                var message = $"Based on configured schedule, the given trigger '{trigger.Key}' will never fire.";
                throw new SchedulerException(message);
            }

            if (await resources.JobStore.ReplaceTrigger(triggerKey, trigger).ConfigureAwait(false))
            {
                NotifySchedulerThread(newTrigger.GetNextFireTimeUtc());
                await NotifySchedulerListenersUnscheduled(triggerKey).ConfigureAwait(false);
                await NotifySchedulerListenersScheduled(newTrigger).ConfigureAwait(false);
            }
            else
            {
                return null;
            }

            return ft;
        }

        private string NewTriggerId()
        {
            long r = NextLong(random);
            if (r < 0)
            {
                r = -r;
            }
            return "MT_" + Convert.ToString(r, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Creates a new positive random number 
        /// </summary>
        /// <param name="random">The last random obtained</param>
        /// <returns>Returns a new positive random number</returns>
        public static long NextLong(Random random)
        {
            long temporaryLong = random.Next();
            temporaryLong = (temporaryLong << 32) + random.Next();
            if (random.Next(-1, 1) < 0)
            {
                return -temporaryLong;
            }

            return temporaryLong;
        }

        /// <summary>
        /// Trigger the identified <see cref="IJob" /> (Execute it now) - with a non-volatile trigger.
        /// </summary>
        public virtual async Task TriggerJob(JobKey jobKey, JobDataMap data)
        {
            ValidateState();

            // TODO: use builder
            IOperableTrigger trig = new SimpleTriggerImpl(
                NewTriggerId(), SchedulerConstants.DefaultGroup, jobKey.Name, jobKey.Group, SystemTime.UtcNow(), null, 0, TimeSpan.Zero);

            trig.ComputeFirstFireTimeUtc(null);
            if (data != null)
            {
                trig.JobDataMap = data;
            }

            bool collision = true;
            while (collision)
            {
                try
                {
                    await resources.JobStore.StoreTrigger(trig, false).ConfigureAwait(false);
                    collision = false;
                }
                catch (ObjectAlreadyExistsException)
                {
                    trig.Key = new TriggerKey(NewTriggerId(), SchedulerConstants.DefaultGroup);
                }
            }

            NotifySchedulerThread(trig.GetNextFireTimeUtc());
            await NotifySchedulerListenersScheduled(trig).ConfigureAwait(false);
        }

        /// <summary>
        /// Store and schedule the identified <see cref="IOperableTrigger"/>
        /// </summary>
        /// <param name="trig"></param>
        public virtual async Task TriggerJob(IOperableTrigger trig)
        {
            ValidateState();

            trig.ComputeFirstFireTimeUtc(null);

            bool collision = true;
            while (collision)
            {
                try
                {
                    await resources.JobStore.StoreTrigger(trig, false).ConfigureAwait(false);
                    collision = false;
                }
                catch (ObjectAlreadyExistsException)
                {
                    trig.Key = new TriggerKey(NewTriggerId(), SchedulerConstants.DefaultGroup);
                }
            }

            NotifySchedulerThread(trig.GetNextFireTimeUtc());
            await NotifySchedulerListenersScheduled(trig).ConfigureAwait(false);
        }

        /// <summary>
        /// Pause the <see cref="ITrigger" /> with the given name.
        /// </summary>
        public virtual async Task PauseTrigger(TriggerKey triggerKey)
        {
            ValidateState();

            await resources.JobStore.PauseTrigger(triggerKey).ConfigureAwait(false);
            NotifySchedulerThread(null);
            await NotifySchedulerListenersPausedTrigger(triggerKey).ConfigureAwait(false);
        }

        /// <summary>
        /// Pause all of the <see cref="ITrigger" />s in the given group.
        /// </summary>
        public virtual async Task PauseTriggers(GroupMatcher<TriggerKey> matcher)
        {
            ValidateState();

            if (matcher == null)
            {
                matcher = GroupMatcher<TriggerKey>.GroupEquals(SchedulerConstants.DefaultGroup);
            }

            ISet<string> pausedGroups = await resources.JobStore.PauseTriggers(matcher).ConfigureAwait(false);
            NotifySchedulerThread(null);
            await Task.WhenAll(pausedGroups.Select(NotifySchedulerListenersPausedTriggers)).ConfigureAwait(false);
        }

        /// <summary> 
        /// Pause the <see cref="IJobDetail" /> with the given
        /// name - by pausing all of its current <see cref="ITrigger" />s.
        /// </summary>
        public virtual async Task PauseJob(JobKey jobKey)
        {
            ValidateState();

            await resources.JobStore.PauseJob(jobKey).ConfigureAwait(false);
            NotifySchedulerThread(null);
            NotifySchedulerListenersPausedJob(jobKey);
        }

        /// <summary>
        /// Pause all of the <see cref="IJobDetail" />s in the
        /// given group - by pausing all of their <see cref="ITrigger" />s.
        /// </summary>
        public virtual async Task PauseJobs(GroupMatcher<JobKey> groupMatcher)
        {
            ValidateState();

            if (groupMatcher == null)
            {
                groupMatcher = GroupMatcher<JobKey>.GroupEquals(SchedulerConstants.DefaultGroup);
            }

            var pausedGroups = await resources.JobStore.PauseJobs(groupMatcher).ConfigureAwait(false);
            NotifySchedulerThread(null);
            await Task.WhenAll(pausedGroups.Select(NotifySchedulerListenersPausedJobs)).ConfigureAwait(false);
        }

        /// <summary>
        /// Resume (un-pause) the <see cref="ITrigger" /> with the given
        /// name.
        /// <para>
        /// If the <see cref="ITrigger" /> missed one or more fire-times, then the
        /// <see cref="ITrigger" />'s misfire instruction will be applied.
        /// </para>
        /// </summary>
        public virtual async Task ResumeTrigger(TriggerKey triggerKey)
        {
            ValidateState();

            await resources.JobStore.ResumeTrigger(triggerKey).ConfigureAwait(false);
            NotifySchedulerThread(null);
            await NotifySchedulerListenersResumedTrigger(triggerKey).ConfigureAwait(false);
        }

        /// <summary>
        /// Resume (un-pause) all of the <see cref="ITrigger" />s in the
        /// matching groups.
        /// <para>
        /// If any <see cref="ITrigger" /> missed one or more fire-times, then the
        /// <see cref="ITrigger" />'s misfire instruction will be applied.
        /// </para>
        /// </summary>
        public virtual async Task ResumeTriggers(GroupMatcher<TriggerKey> matcher)
        {
            ValidateState();

            if (matcher == null)
            {
                matcher = GroupMatcher<TriggerKey>.GroupEquals(SchedulerConstants.DefaultGroup);
            }

            var pausedGroups = await resources.JobStore.ResumeTriggers(matcher).ConfigureAwait(false);
            NotifySchedulerThread(null);
            await Task.WhenAll(pausedGroups.Select(NotifySchedulerListenersResumedTriggers)).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the paused trigger groups.
        /// </summary>
        /// <returns></returns>
        public virtual Task<ISet<string>> GetPausedTriggerGroups()
        {
            return resources.JobStore.GetPausedTriggerGroups();
        }

        /// <summary>
        /// Resume (un-pause) the <see cref="IJobDetail" /> with
        /// the given name.
        /// <para>
        /// If any of the <see cref="IJob" />'s<see cref="ITrigger" /> s missed one
        /// or more fire-times, then the <see cref="ITrigger" />'s misfire
        /// instruction will be applied.
        /// </para>
        /// </summary>
        public virtual async Task ResumeJob(JobKey jobKey)
        {
            ValidateState();

            await resources.JobStore.ResumeJob(jobKey).ConfigureAwait(false);
            NotifySchedulerThread(null);
            await NotifySchedulerListenersResumedJob(jobKey).ConfigureAwait(false);
        }

        /// <summary>
        /// Resume (un-pause) all of the <see cref="IJobDetail" />s
        /// in the matching groups.
        /// <para>
        /// If any of the <see cref="IJob" /> s had <see cref="ITrigger" /> s that
        /// missed one or more fire-times, then the <see cref="ITrigger" />'s
        /// misfire instruction will be applied.
        /// </para>
        /// </summary>
        public virtual async Task ResumeJobs(GroupMatcher<JobKey> matcher)
        {
            ValidateState();

            if (matcher == null)
            {
                matcher = GroupMatcher<JobKey>.GroupEquals(SchedulerConstants.DefaultGroup);
            }

            ISet<string> resumedGroups = await resources.JobStore.ResumeJobs(matcher).ConfigureAwait(false);
            NotifySchedulerThread(null);
            await Task.WhenAll(resumedGroups.Select(NotifySchedulerListenersResumedJobs)).ConfigureAwait(false);
        }

        /// <summary>
        /// Pause all triggers - equivalent of calling <see cref="PauseTriggers" />
        /// with a matcher matching all known groups.
        /// <para>
        /// When <see cref="ResumeAll" /> is called (to un-pause), trigger misfire
        /// instructions WILL be applied.
        /// </para>
        /// </summary>
        /// <seealso cref="ResumeAll()" />
        /// <seealso cref="PauseJob" />
        public virtual async Task PauseAll()
        {
            ValidateState();

            await resources.JobStore.PauseAll().ConfigureAwait(false);
            NotifySchedulerThread(null);
            await NotifySchedulerListenersPausedTriggers(null).ConfigureAwait(false);
        }

        /// <summary>
        /// Resume (un-pause) all triggers - equivalent of calling <see cref="ResumeTriggers" />
        /// on every group.
        /// <para>
        /// If any <see cref="ITrigger" /> missed one or more fire-times, then the
        /// <see cref="ITrigger" />'s misfire instruction will be applied.
        /// </para>
        /// </summary>
        /// <seealso cref="PauseAll()" />
        public virtual async Task ResumeAll()
        {
            ValidateState();

            await resources.JobStore.ResumeAll().ConfigureAwait(false);
            NotifySchedulerThread(null);
            await NotifySchedulerListenersResumedTriggers(null).ConfigureAwait(false);
        }

        /// <summary>
        /// Get the names of all known <see cref="IJob" /> groups.
        /// </summary>
        public virtual async Task<IReadOnlyList<string>> GetJobGroupNames()
        {
            ValidateState();

            return await resources.JobStore.GetJobGroupNames().ConfigureAwait(false);
        }

        /// <summary>
        /// Get the names of all the <see cref="IJob" />s in the
        /// given group.
        /// </summary>
        public virtual Task<ISet<JobKey>> GetJobKeys(GroupMatcher<JobKey> matcher)
        {
            ValidateState();

            if (matcher == null)
            {
                matcher = GroupMatcher<JobKey>.GroupEquals(SchedulerConstants.DefaultGroup);
            }

            return resources.JobStore.GetJobKeys(matcher);
        }

        /// <summary> 
        /// Get all <see cref="ITrigger" /> s that are associated with the
        /// identified <see cref="IJobDetail" />.
        /// </summary>
        public virtual async Task<IReadOnlyList<ITrigger>> GetTriggersOfJob(JobKey jobKey)
        {
            ValidateState();

            var triggersForJob = await resources.JobStore.GetTriggersForJob(jobKey).ConfigureAwait(false);

            var retValue = new List<ITrigger>(triggersForJob.Count);
            foreach (var trigger in triggersForJob)
            {
                retValue.Add(trigger);
            }
            return retValue;
        }

        /// <summary>
        /// Get the names of all known <see cref="ITrigger" />
        /// groups.
        /// </summary>
        public virtual async Task<IReadOnlyList<string>> GetTriggerGroupNames()
        {
            ValidateState();
            return await resources.JobStore.GetTriggerGroupNames().ConfigureAwait(false);
        }

        /// <summary>
        /// Get the names of all the <see cref="ITrigger" />s in
        /// the matching groups.
        /// </summary>
        public virtual Task<ISet<TriggerKey>> GetTriggerKeys(GroupMatcher<TriggerKey> matcher)
        {
            ValidateState();

            if (matcher == null)
            {
                matcher = GroupMatcher<TriggerKey>.GroupEquals(SchedulerConstants.DefaultGroup);
            }

            return resources.JobStore.GetTriggerKeys(matcher);
        }

        /// <summary> 
        /// Get the <see cref="IJobDetail" /> for the <see cref="IJob" />
        /// instance with the given name and group.
        /// </summary>
        public virtual Task<IJobDetail> GetJobDetail(JobKey jobKey)
        {
            ValidateState();

            return resources.JobStore.RetrieveJob(jobKey);
        }

        /// <summary>
        /// Get the <see cref="ITrigger" /> instance with the given name and
        /// group.
        /// </summary>
        public virtual async Task<ITrigger> GetTrigger(TriggerKey triggerKey)
        {
            ValidateState();

            return await resources.JobStore.RetrieveTrigger(triggerKey).ConfigureAwait(false);
        }

        /// <summary>
        /// Determine whether a <see cref="IJob"/> with the given identifier already
        /// exists within the scheduler.
        /// </summary>
        /// <remarks>
        /// </remarks>
        /// <param name="jobKey">the identifier to check for</param>
        /// <returns>true if a Job exists with the given identifier</returns>
        public virtual Task<bool> CheckExists(JobKey jobKey)
        {
            ValidateState();

            return resources.JobStore.CheckExists(jobKey);
        }

        /// <summary>
        /// Determine whether a <see cref="ITrigger" /> with the given identifier already
        /// exists within the scheduler.
        /// </summary>
        /// <remarks>
        /// </remarks>
        /// <param name="triggerKey">the identifier to check for</param>
        /// <returns>true if a Trigger exists with the given identifier</returns>
        public virtual async Task<bool> CheckExists(TriggerKey triggerKey)
        {
            ValidateState();

            return await resources.JobStore.CheckExists(triggerKey).ConfigureAwait(false);
        }

        /// <summary>
        /// Clears (deletes!) all scheduling data - all <see cref="IJob"/>s, <see cref="ITrigger" />s
        /// <see cref="ICalendar" />s.
        /// </summary>
        public virtual async Task Clear()
        {
            ValidateState();

            await resources.JobStore.ClearAllSchedulingData().ConfigureAwait(false);
            await NotifySchedulerListenersUnscheduled(null).ConfigureAwait(false);
        }

        /// <summary>
        /// Get the current state of the identified <see cref="ITrigger" />.  
        /// </summary>
        /// <seealso cref="TriggerState" />
        public virtual Task<TriggerState> GetTriggerState(TriggerKey triggerKey)
        {
            ValidateState();

            return resources.JobStore.GetTriggerState(triggerKey);
        }

        /// <summary>
        /// Add (register) the given <see cref="ICalendar" /> to the Scheduler.
        /// </summary>
        public virtual Task AddCalendar(string calName, ICalendar calendar, bool replace, bool updateTriggers)
        {
            ValidateState();
            return resources.JobStore.StoreCalendar(calName, calendar, replace, updateTriggers);
        }

        /// <summary>
        /// Delete the identified <see cref="ICalendar" /> from the Scheduler.
        /// </summary>
        /// <returns> true if the Calendar was found and deleted.</returns>
        public virtual Task<bool> DeleteCalendar(string calName)
        {
            ValidateState();
            return resources.JobStore.RemoveCalendar(calName);
        }

        /// <summary> 
        /// Get the <see cref="ICalendar" /> instance with the given name.
        /// </summary>
        public virtual Task<ICalendar> GetCalendar(string calName)
        {
            ValidateState();
            return resources.JobStore.RetrieveCalendar(calName);
        }

        /// <summary>
        /// Get the names of all registered <see cref="ICalendar" />s.
        /// </summary>
        public virtual Task<IReadOnlyList<string>> GetCalendarNames()
        {
            ValidateState();
            return resources.JobStore.GetCalendarNames();
        }

        public IListenerManager ListenerManager { get; } = new ListenerManagerImpl();

        /// <summary>
        /// Add the given <see cref="IJobListener" /> to the
        /// <see cref="IScheduler" />'s <i>internal</i> list.
        /// </summary>
        /// <param name="jobListener"></param>
        public void AddInternalJobListener(IJobListener jobListener)
        {
            if (jobListener.Name.IsNullOrWhiteSpace())
            {
                throw new ArgumentException("JobListener name cannot be empty.", "jobListener");
            }
            internalJobListeners[jobListener.Name] = jobListener;
        }

        /// <summary>
        /// Remove the identified <see cref="IJobListener" /> from the <see cref="IScheduler" />'s
        /// list of <i>internal</i> listeners.
        /// </summary>
        /// <param name="name"></param>
        /// <returns>true if the identified listener was found in the list, and removed.</returns>
        public bool RemoveInternalJobListener(string name)
        {
            IJobListener temp;
            return internalJobListeners.TryRemove(name, out temp);
        }

        /// <summary>
        /// Get a List containing all of the <see cref="IJobListener" />s
        /// in the <see cref="IScheduler" />'s <i>internal</i> list.
        /// </summary>
        /// <returns></returns>
        public IReadOnlyList<IJobListener> InternalJobListeners => new List<IJobListener>(internalJobListeners.Values);

        /// <summary>
        /// Get the <i>internal</i> <see cref="IJobListener" />
        /// that has the given name.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public IJobListener GetInternalJobListener(string name)
        {
            IJobListener listener;
            internalJobListeners.TryGetValue(name, out listener);
            return listener;
        }

        /// <summary>
        /// Add the given <see cref="ITriggerListener" /> to the
        /// <see cref="IScheduler" />'s <i>internal</i> list.
        /// </summary>
        /// <param name="triggerListener"></param>
        public void AddInternalTriggerListener(ITriggerListener triggerListener)
        {
            if (triggerListener.Name.IsNullOrWhiteSpace())
            {
                throw new ArgumentException("TriggerListener name cannot be empty.", "triggerListener");
            }
            internalTriggerListeners[triggerListener.Name] = triggerListener;
        }

        /// <summary>
        /// Remove the identified <see cref="ITriggerListener" /> from the <see cref="IScheduler" />'s
        /// list of <i>internal</i> listeners.
        /// </summary>
        /// <param name="name"></param>
        /// <returns>true if the identified listener was found in the list, and removed.</returns>
        public bool RemoveinternalTriggerListener(string name)
        {
            ITriggerListener temp;
            return internalTriggerListeners.TryRemove(name, out temp);
        }

        /// <summary>
        /// Get a list containing all of the <see cref="ITriggerListener" />s
        /// in the <see cref="IScheduler" />'s <i>internal</i> list.
        /// </summary>
        public IReadOnlyList<ITriggerListener> InternalTriggerListeners
        {
            get { return new List<ITriggerListener>(internalTriggerListeners.Values); }
        }

        /// <summary>
        /// Get the <i>internal</i> <see cref="ITriggerListener" /> that
        /// has the given name.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public ITriggerListener GetInternalTriggerListener(string name)
        {
            ITriggerListener triggerListener;
            internalTriggerListeners.TryGetValue(name, out triggerListener);
            return triggerListener;
        }

        public virtual Task NotifyJobStoreJobVetoed(IOperableTrigger trigger, IJobDetail detail, SchedulerInstruction instCode)
        {
            return resources.JobStore.TriggeredJobComplete(trigger, detail, instCode);
        }

        /// <summary>
        /// Notifies the job store job complete.
        /// </summary>
        /// <param name="trigger">The trigger.</param>
        /// <param name="detail">The detail.</param>
        /// <param name="instCode">The instruction code.</param>
        public virtual Task NotifyJobStoreJobComplete(IOperableTrigger trigger, IJobDetail detail, SchedulerInstruction instCode)
        {
            return resources.JobStore.TriggeredJobComplete(trigger, detail, instCode);
        }

        /// <summary>
        /// Notifies the scheduler thread.
        /// </summary>
        protected virtual void NotifySchedulerThread(DateTimeOffset? candidateNewNextFireTimeUtc)
        {
            if (SignalOnSchedulingChange)
            {
                schedThread.SignalSchedulingChange(candidateNewNextFireTimeUtc);
            }
        }

        private IEnumerable<ITriggerListener> BuildTriggerListenerList()
        {
            List<ITriggerListener> listeners = new List<ITriggerListener>();
            listeners.AddRange(ListenerManager.GetTriggerListeners());
            listeners.AddRange(InternalTriggerListeners);
            return listeners;
        }

        private IEnumerable<IJobListener> BuildJobListenerList()
        {
            List<IJobListener> listeners = new List<IJobListener>();
            listeners.AddRange(ListenerManager.GetJobListeners());
            listeners.AddRange(InternalJobListeners);
            return listeners;
        }

        private IList<ISchedulerListener> BuildSchedulerListenerList()
        {
            List<ISchedulerListener> allListeners = new List<ISchedulerListener>();
            allListeners.AddRange(ListenerManager.GetSchedulerListeners());
            allListeners.AddRange(InternalSchedulerListeners);
            return allListeners;
        }

        private bool MatchJobListener(IJobListener listener, JobKey key)
        {
            IList<IMatcher<JobKey>> matchers = ListenerManager.GetJobListenerMatchers(listener.Name);
            if (matchers == null)
            {
                return true;
            }
            foreach (IMatcher<JobKey> matcher in matchers)
            {
                if (matcher.IsMatch(key))
                {
                    return true;
                }
            }
            return false;
        }

        private bool MatchTriggerListener(ITriggerListener listener, TriggerKey key)
        {
            IList<IMatcher<TriggerKey>> matchers = ListenerManager.GetTriggerListenerMatchers(listener.Name);
            if (matchers == null)
            {
                return true;
            }
            return matchers.Any(matcher => matcher.IsMatch(key));
        }

        /// <summary>
        /// Notifies the trigger listeners about fired trigger.
        /// </summary>
        /// <param name="jec">The job execution context.</param>
        /// <returns></returns>
        public virtual async Task<bool> NotifyTriggerListenersFired(IJobExecutionContext jec)
        {
            bool vetoedExecution = false;

            // build a list of all trigger listeners that are to be notified...
            IEnumerable<ITriggerListener> listeners = BuildTriggerListenerList();

            // notify all trigger listeners in the list
            foreach (ITriggerListener tl in listeners)
            {
                if (!MatchTriggerListener(tl, jec.Trigger.Key))
                {
                    continue;
                }
                try
                {
                    await tl.TriggerFired(jec.Trigger, jec).ConfigureAwait(false);

                    if (await tl.VetoJobExecution(jec.Trigger, jec).ConfigureAwait(false))
                    {
                        vetoedExecution = true;
                    }
                }
                catch (Exception e)
                {
                    SchedulerException se = new SchedulerException($"TriggerListener '{tl.Name}' threw exception: {e.Message}", e);
                    throw se;
                }
            }

            return vetoedExecution;
        }

        /// <summary>
        /// Notifies the trigger listeners about misfired trigger.
        /// </summary>
        /// <param name="trigger">The trigger.</param>
        public virtual async Task NotifyTriggerListenersMisfired(ITrigger trigger)
        {
            // build a list of all trigger listeners that are to be notified...
            var listeners = BuildTriggerListenerList();

            // notify all trigger listeners in the list
            foreach (ITriggerListener tl in listeners)
            {
                if (!MatchTriggerListener(tl, trigger.Key))
                {
                    continue;
                }
                try
                {
                    await tl.TriggerMisfired(trigger).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    SchedulerException se = new SchedulerException($"TriggerListener '{tl.Name}' threw exception: {e.Message}", e);
                    throw se;
                }
            }
        }

        /// <summary>
        /// Notifies the trigger listeners of completion.
        /// </summary>
        /// <param name="jec">The job execution context.</param>
        /// <param name="instCode">The instruction code to report to triggers.</param>
        public virtual async Task NotifyTriggerListenersComplete(IJobExecutionContext jec, SchedulerInstruction instCode)
        {
            // build a list of all trigger listeners that are to be notified...
            var listeners = BuildTriggerListenerList();

            // notify all trigger listeners in the list
            foreach (ITriggerListener tl in listeners)
            {
                if (!MatchTriggerListener(tl, jec.Trigger.Key))
                {
                    continue;
                }
                try
                {
                    await tl.TriggerComplete(jec.Trigger, jec, instCode).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    SchedulerException se = new SchedulerException($"TriggerListener '{tl.Name}' threw exception: {e.Message}", e);
                    throw se;
                }
            }
        }

        /// <summary>
        /// Notifies the job listeners about job to be executed.
        /// </summary>
        /// <param name="jec">The jec.</param>
        public virtual async Task NotifyJobListenersToBeExecuted(IJobExecutionContext jec)
        {
            // build a list of all job listeners that are to be notified...
            IEnumerable<IJobListener> listeners = BuildJobListenerList();

            // notify all job listeners
            foreach (IJobListener jl in listeners)
            {
                if (!MatchJobListener(jl, jec.JobDetail.Key))
                {
                    continue;
                }
                try
                {
                    await jl.JobToBeExecuted(jec).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    SchedulerException se = new SchedulerException($"JobListener '{jl.Name}' threw exception: {e.Message}", e);
                    throw se;
                }
            }
        }

        /// <summary>
        /// Notifies the job listeners that job execution was vetoed.
        /// </summary>
        /// <param name="jec">The job execution context.</param>
        public virtual async Task NotifyJobListenersWasVetoed(IJobExecutionContext jec)
        {
            // build a list of all job listeners that are to be notified...
            IEnumerable<IJobListener> listeners = BuildJobListenerList();

            // notify all job listeners
            foreach (IJobListener jl in listeners)
            {
                if (!MatchJobListener(jl, jec.JobDetail.Key))
                {
                    continue;
                }
                try
                {
                    await jl.JobExecutionVetoed(jec).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    SchedulerException se = new SchedulerException($"JobListener '{jl.Name}' threw exception: {e.Message}", e);
                    throw se;
                }
            }
        }

        /// <summary>
        /// Notifies the job listeners that job was executed.
        /// </summary>
        /// <param name="jec">The jec.</param>
        /// <param name="je">The je.</param>
        public virtual async Task NotifyJobListenersWasExecuted(IJobExecutionContext jec, JobExecutionException je)
        {
            // build a list of all job listeners that are to be notified...
            IEnumerable<IJobListener> listeners = BuildJobListenerList();

            // notify all job listeners
            foreach (IJobListener jl in listeners)
            {
                if (!MatchJobListener(jl, jec.JobDetail.Key))
                {
                    continue;
                }
                try
                {
                    await jl.JobWasExecuted(jec, je).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    SchedulerException se = new SchedulerException($"JobListener '{jl.Name}' threw exception: {e.Message}", e);
                    throw se;
                }
            }
        }

        /// <summary>
        /// Notifies the scheduler listeners about scheduler error.
        /// </summary>
        /// <param name="msg">The MSG.</param>
        /// <param name="se">The se.</param>
        public virtual async Task NotifySchedulerListenersError(string msg, SchedulerException se)
        {
            // build a list of all scheduler listeners that are to be notified...
            IList<ISchedulerListener> schedListeners = BuildSchedulerListenerList();

            // notify all scheduler listeners
            foreach (ISchedulerListener sl in schedListeners)
            {
                try
                {
                    await sl.SchedulerError(msg, se).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    log.ErrorException("Error while notifying SchedulerListener of error: ", e);
                    log.ErrorException("  Original error (for notification) was: " + msg, se);
                }
            }
        }

        /// <summary>
        /// Notifies the scheduler listeners about job that was scheduled.
        /// </summary>
        /// <param name="trigger">The trigger.</param>
        public virtual Task NotifySchedulerListenersScheduled(ITrigger trigger)
        {
            return NotifySchedulerListeners(l => l.JobScheduled(trigger), $"scheduled job. Trigger={trigger.Key}");
        }

        /// <summary>
        /// Notifies the scheduler listeners about job that was unscheduled.
        /// </summary>
        public virtual async Task NotifySchedulerListenersUnscheduled(TriggerKey triggerKey)
        {
            // build a list of all scheduler listeners that are to be notified...
            IList<ISchedulerListener> schedListeners = BuildSchedulerListenerList();

            // notify all scheduler listeners
            foreach (ISchedulerListener sl in schedListeners)
            {
                try
                {
                    if (triggerKey == null)
                    {
                        await sl.SchedulingDataCleared().ConfigureAwait(false);
                    }
                    else
                    {
                        await sl.JobUnscheduled(triggerKey).ConfigureAwait(false);
                    }
                }
                catch (Exception e)
                {
                    log.ErrorFormat(
                        "Error while notifying SchedulerListener of unscheduled job. Trigger={0}",
                        e,
                        (triggerKey == null ? "ALL DATA" : triggerKey.ToString()));
                }
            }
        }

        /// <summary>
        /// Notifies the scheduler listeners about finalized trigger.
        /// </summary>
        /// <param name="trigger">The trigger.</param>
        public virtual Task NotifySchedulerListenersFinalized(ITrigger trigger)
        {
            return NotifySchedulerListeners(l => l.TriggerFinalized(trigger), $"finalized trigger. Trigger={trigger.Key}");
        }

        /// <summary>
        /// Notifies the scheduler listeners about paused trigger.
        /// </summary>
        /// <param name="group">The group.</param>
        public virtual async Task NotifySchedulerListenersPausedTriggers(string group)
        {
            // build a list of all job listeners that are to be notified...
            IList<ISchedulerListener> schedListeners = BuildSchedulerListenerList();

            // notify all scheduler listeners
            foreach (ISchedulerListener sl in schedListeners)
            {
                try
                {
                    await sl.TriggersPaused(group).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    log.ErrorException($"Error while notifying SchedulerListener of paused group: {@group}", e);
                }
            }
        }

        /// <summary>
        /// Notifies the scheduler listeners about paused trigger.
        /// </summary>
        public virtual async Task NotifySchedulerListenersPausedTrigger(TriggerKey triggerKey)
        {
            // build a list of all job listeners that are to be notified...
            IList<ISchedulerListener> schedListeners = BuildSchedulerListenerList();

            // notify all scheduler listeners
            foreach (ISchedulerListener sl in schedListeners)
            {
                try
                {
                    await sl.TriggerPaused(triggerKey).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    log.ErrorException($"Error while notifying SchedulerListener of paused trigger. Trigger={triggerKey}", e);
                }
            }
        }

        /// <summary>
        /// Notifies the scheduler listeners resumed trigger.
        /// </summary>
        /// <param name="group">The group.</param>
        public virtual Task NotifySchedulerListenersResumedTriggers(string group)
        {
            return NotifySchedulerListeners(l => l.TriggersResumed(group), $"resumed group: {@group}");
        }

        /// <summary>
        /// Notifies the scheduler listeners resumed trigger.
        /// </summary>
        public virtual async Task NotifySchedulerListenersResumedTrigger(TriggerKey triggerKey)
        {
            // build a list of all job listeners that are to be notified...
            IList<ISchedulerListener> schedListeners = BuildSchedulerListenerList();

            // notify all scheduler listeners
            foreach (ISchedulerListener sl in schedListeners)
            {
                try
                {
                    await sl.TriggerResumed(triggerKey).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    log.ErrorException($"Error while notifying SchedulerListener of resumed trigger. Trigger={triggerKey}", e);
                }
            }
        }

        /// <summary>
        /// Notifies the scheduler listeners about paused job.
        /// </summary>
        public virtual void NotifySchedulerListenersPausedJob(JobKey jobKey)
        {
            // build a list of all job listeners that are to be notified...
            IList<ISchedulerListener> schedListeners = BuildSchedulerListenerList();

            // notify all scheduler listeners
            foreach (ISchedulerListener sl in schedListeners)
            {
                try
                {
                    sl.JobPaused(jobKey);
                }
                catch (Exception e)
                {
                    log.ErrorException($"Error while notifying SchedulerListener of paused job. Job={jobKey}", e);
                }
            }
        }

        /// <summary>
        /// Notifies the scheduler listeners about paused job.
        /// </summary>
        /// <param name="group">The group.</param>
        public virtual async Task NotifySchedulerListenersPausedJobs(string group)
        {
            // build a list of all job listeners that are to be notified...
            IList<ISchedulerListener> schedListeners = BuildSchedulerListenerList();

            // notify all scheduler listeners
            foreach (ISchedulerListener sl in schedListeners)
            {
                try
                {
                    await sl.JobsPaused(@group).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    log.ErrorException($"Error while notifying SchedulerListener of paused group: {@group}", e);
                }
            }
        }

        /// <summary>
        /// Notifies the scheduler listeners about resumed job.
        /// </summary>
        public virtual async Task NotifySchedulerListenersResumedJob(JobKey jobKey)
        {
            // build a list of all job listeners that are to be notified...
            IList<ISchedulerListener> schedListeners = BuildSchedulerListenerList();

            // notify all scheduler listeners
            foreach (ISchedulerListener sl in schedListeners)
            {
                try
                {
                    await sl.JobResumed(jobKey).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    log.ErrorException($"Error while notifying SchedulerListener of resumed job: {jobKey}", e);
                }
            }
        }

        /// <summary>
        /// Notifies the scheduler listeners about resumed job.
        /// </summary>
        /// <param name="group">The group.</param>
        public virtual async Task NotifySchedulerListenersResumedJobs(string group)
        {
            // build a list of all job listeners that are to be notified...
            IList<ISchedulerListener> schedListeners = BuildSchedulerListenerList();

            // notify all scheduler listeners
            foreach (ISchedulerListener sl in schedListeners)
            {
                try
                {
                    await sl.JobsResumed(group).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    log.ErrorException($"Error while notifying SchedulerListener of resumed group: {@group}", e);
                }
            }
        }

        public virtual Task NotifySchedulerListenersInStandbyMode()
        {
            return NotifySchedulerListeners(l => l.SchedulerInStandbyMode(), "inStandByMode");
        }

        public virtual Task NotifySchedulerListenersStarted()
        {
            return NotifySchedulerListeners(l => l.SchedulerStarted(), "startup");
        }

        public virtual Task NotifySchedulerListenersStarting()
        {
            return NotifySchedulerListeners(l => l.SchedulerStarting(), "scheduler starting");
        }

        /// <summary>
        /// Notifies the scheduler listeners about scheduler shutdown.
        /// </summary>
        public virtual Task NotifySchedulerListenersShutdown()
        {
            return NotifySchedulerListeners(l => l.SchedulerShutdown(), "shutdown");
        }

        public virtual Task NotifySchedulerListenersShuttingdown()
        {
            return NotifySchedulerListeners(l => l.SchedulerShuttingdown(), "shutting down");
        }

        public virtual Task NotifySchedulerListenersJobAdded(IJobDetail jobDetail)
        {
            return NotifySchedulerListeners(l => l.JobAdded(jobDetail), "job addition");
        }

        public virtual Task NotifySchedulerListenersJobDeleted(JobKey jobKey)
        {
            return NotifySchedulerListeners(l => l.JobDeleted(jobKey), "job deletion");
        }

        protected virtual async Task NotifySchedulerListeners(Func<ISchedulerListener, Task> notifier, string action)
        {
            // notify all scheduler listeners
            var listeners = BuildSchedulerListenerList();
            foreach (var listener in listeners)
            {
                try
                {
                    await notifier(listener).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    log.ErrorException("Error while notifying SchedulerListener of " + action + ".", e);
                }
            }
        }

        /// <summary>
        /// Interrupt all instances of the identified InterruptableJob.
        /// </summary>
        public virtual Task<bool> Interrupt(JobKey jobKey)
        {
            var cancellableJobs = CurrentlyExecutingJobs.OfType<ICancellableJobExecutionContext>();

            bool interrupted = false;

            foreach (var cancellableJobExecutionContext in cancellableJobs)
            {
                var jobDetail = cancellableJobExecutionContext.JobDetail;
                if (jobKey.Equals(jobDetail.Key))
                {
                    cancellableJobExecutionContext.Cancel();
                    interrupted = true;
                    break;
                }
            }

            return Task.FromResult(interrupted);
        }

        /// <summary>
        /// Interrupt all instances of the identified InterruptableJob executing in this Scheduler instance.
        /// </summary>
        /// <remarks>
        /// This method is not cluster aware.  That is, it will only interrupt 
        /// instances of the identified InterruptableJob currently executing in this 
        /// Scheduler instance, not across the entire cluster.
        /// </remarks>
        /// <seealso cref="IRemotableQuartzScheduler.Interrupt(JobKey)" />
        /// <param name="fireInstanceId"></param>
        /// <returns></returns>
        public Task<bool> Interrupt(string fireInstanceId)
        {
            var cancellableJobs = CurrentlyExecutingJobs.OfType<ICancellableJobExecutionContext>();

            bool interrupted = false;

            foreach (var cancellableJobExecutionContext in cancellableJobs)
            {
                if (cancellableJobExecutionContext.FireInstanceId.Equals(fireInstanceId))
                {
                    cancellableJobExecutionContext.Cancel();
                    interrupted = true;
                    break;
                }
            }

            return Task.FromResult(interrupted);
        }

        private async Task ShutdownPlugins()
        {
            foreach (ISchedulerPlugin plugin in resources.SchedulerPlugins)
            {
                await plugin.Shutdown().ConfigureAwait(false);
            }
        }

        private async Task StartPlugins()
        {
            foreach (ISchedulerPlugin plugin in resources.SchedulerPlugins)
            {
                await plugin.Start().ConfigureAwait(false);
            }
        }

        public virtual Task<bool> IsJobGroupPaused(string groupName)
        {
            return resources.JobStore.IsJobGroupPaused(groupName);
        }

        public virtual Task<bool> IsTriggerGroupPaused(string groupName)
        {
            return resources.JobStore.IsTriggerGroupPaused(groupName);
        }

        ///<summary>
        ///Obtains a lifetime service object to control the lifetime policy for this instance.
        ///</summary>
        [SecurityCritical]
        public override object InitializeLifetimeService()
        {
            // overridden to initialize null life time service,
            // this basically means that remoting object will live as long
            // as the application lives
            return null;
        }
    }
}