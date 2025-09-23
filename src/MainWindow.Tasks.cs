using Microsoft.Win32.TaskScheduler;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Traycer
{
    public partial class MainWindow
    {
        private const string TraycerTaskFolderName = "Traycer";
        private const string TraycerTaskFolderPath = @"\Traycer";

        /// <summary>
        /// Captures task launch metadata.
        /// </summary>
        private record TaskConfig(string Id, string Command, string? Arguments, string Mode, bool AutoStart, ScheduleConfig? Schedule, string? WorkingDirectory);

        /// <summary>
        /// Describes scheduler cadence.
        /// </summary>
        private record ScheduleConfig(string Frequency, int? Interval, string? Start);

        /// <summary>
        /// Tracks runtime state for a configured task.
        /// </summary>
        private class ManagedTask
        {
            /// <summary>
            /// Initializes with a task config.
            /// </summary>
            /// <param name="config">Task config.</param>
            public ManagedTask(TaskConfig config) => Config = config;

            public TaskConfig Config { get; private set; }

            /// <summary>
            /// Current live process if running.
            /// </summary>
            public Process? ActiveProcess { get; set; }

            /// <summary>
            /// Gets whether this task uses the scheduler.
            /// </summary>
            public bool IsScheduled => string.Equals(Config.Mode, "schedule", StringComparison.OrdinalIgnoreCase);

            /// <summary>
            /// Replaces the stored config.
            /// </summary>
            /// <param name="config">New config.</param>
            public void Update(TaskConfig config) => Config = config;
        }

        /// <summary>
        /// Starts a non-scheduled task process.
        /// </summary>
        /// <param name="task">Target task.</param>
        /// <param name="fromMenu">True when user requested.</param>
        private void StartTaskProcess(ManagedTask task, bool fromMenu)
        {
            if (task.ActiveProcess is { HasExited: false })
            {
                if (fromMenu)
                {
                    ShowTrayMessage($"Task '{task.Config.Id}' is already running.");
                }

                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = task.Config.Command,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                if (!string.IsNullOrWhiteSpace(task.Config.Arguments))
                {
                    psi.Arguments = task.Config.Arguments;
                }

                if (!string.IsNullOrWhiteSpace(task.Config.WorkingDirectory))
                {
                    psi.WorkingDirectory = task.Config.WorkingDirectory!;
                }

                var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (_, __) => Dispatcher.Invoke(() =>
                    {
                        task.ActiveProcess = null;
                        RefreshTrayMenu();
                    });
                    task.ActiveProcess = proc;
                }
            }
            catch (Exception ex)
            {
                ShowTrayMessage($"Task '{task.Config.Id}' failed: {ex.Message}");
            }
            finally
            {
                RefreshTrayMenu();
            }
        }

        /// <summary>
        /// Attempts to kill a running process.
        /// </summary>
        /// <param name="task">Target task.</param>
        private void KillTaskProcess(ManagedTask task)
        {
            if (task.ActiveProcess is { HasExited: false } proc)
            {
                try
                {
                    proc.Kill(true);
                }
                catch (Exception ex)
                {
                    ShowTrayMessage($"Unable to kill '{task.Config.Id}': {ex.Message}");
                }
            }

            task.ActiveProcess = null;
            RefreshTrayMenu();
        }

        /// <summary>
        /// Triggers a scheduled task immediately.
        /// </summary>
        /// <param name="config">Task config.</param>
        private void RunScheduledTask(TaskConfig config)
        {
            try
            {
                using var service = new TaskService();
                var folder = GetTraycerFolder(service, createIfMissing: false, out var error);
                if (folder is null)
                {
                    ShowTrayMessage($"Start '{config.Id}' failed: {error ?? "Task folder not found."}");
                    return;
                }

                var task = folder.Tasks.FirstOrDefault(t => string.Equals(t.Name, config.Id, StringComparison.OrdinalIgnoreCase));
                if (task is null)
                {
                    ShowTrayMessage($"Start '{config.Id}' failed: task not found.");
                    return;
                }

                task.Run();
            }
            catch (Exception ex)
            {
                ShowTrayMessage($"Start '{config.Id}' failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Requests stop for a scheduled task.
        /// </summary>
        /// <param name="config">Task config.</param>
        private void EndScheduledTask(TaskConfig config)
        {
            try
            {
                using var service = new TaskService();
                var folder = GetTraycerFolder(service, createIfMissing: false, out var error);
                if (folder is null)
                {
                    ShowTrayMessage($"Stop '{config.Id}' failed: {error ?? "Task folder not found."}");
                    return;
                }

                var task = folder.Tasks.FirstOrDefault(t => string.Equals(t.Name, config.Id, StringComparison.OrdinalIgnoreCase));
                if (task is null)
                {
                    ShowTrayMessage($"Stop '{config.Id}' failed: task not found.");
                    return;
                }

                task.Stop();
            }
            catch (Exception ex)
            {
                ShowTrayMessage($"Stop '{config.Id}' failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Ensures a Windows Task Scheduler entry exists.
        /// </summary>
        /// <param name="config">Task config.</param>
        private void EnsureScheduledTask(TaskConfig config)
        {
            if (config.Schedule is null)
            {
                ShowTrayMessage($"Task '{config.Id}' missing schedule configuration.");
                return;
            }

            if (!TryCreateTrigger(config, out var trigger, out var frequencyError))
            {
                ShowTrayMessage($"Task '{config.Id}' has unsupported schedule frequency '{frequencyError}'.");
                return;
            }

            try
            {
                using var service = new TaskService();
                var folder = GetTraycerFolder(service, createIfMissing: true, out var folderError);
                if (folder is null)
                {
                    ShowTrayMessage($"Scheduling '{config.Id}' failed: {folderError ?? "Unable to access Task Scheduler."}");
                    return;
                }

                var definition = service.NewTask();
                definition.RegistrationInfo.Description = $"Traycer scheduled task '{config.Id}'";
                definition.Settings.Enabled = true;
                definition.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
                definition.Settings.DisallowStartIfOnBatteries = false;
                definition.Settings.StopIfGoingOnBatteries = false;
                definition.Settings.StartWhenAvailable = true;
                definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;

                var action = new ExecAction(config.Command, config.Arguments, config.WorkingDirectory);
                definition.Actions.Add(action);
                definition.Triggers.Add(trigger);

                var user = GetCurrentUserPrincipal();
                definition.Principal.LogonType = TaskLogonType.InteractiveToken;
                definition.Principal.RunLevel = TaskRunLevel.LUA;
                if (!string.IsNullOrWhiteSpace(user))
                {
                    definition.Principal.UserId = user;
                }

                folder.RegisterTaskDefinition(config.Id, definition, TaskCreation.CreateOrUpdate, string.IsNullOrWhiteSpace(user) ? null : user, null, TaskLogonType.InteractiveToken);
            }
            catch (Exception ex)
            {
                ShowTrayMessage($"Scheduling '{config.Id}' failed: {ex.Message}");
                return;
            }

            if (config.AutoStart)
            {
                RunScheduledTask(config);
            }
        }

        /// <summary>
        /// Removes the scheduler entry for a task.
        /// </summary>
        /// <param name="config">Task config.</param>
        private void RemoveScheduledTask(TaskConfig config)
        {
            try
            {
                using var service = new TaskService();
                var folder = GetTraycerFolder(service, createIfMissing: false, out _);
                folder?.DeleteTask(config.Id, false);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Fetches the Traycer task folder.
        /// </summary>
        /// <param name="service">Scheduler service.</param>
        /// <param name="createIfMissing">Create flag.</param>
        /// <param name="error">Error message.</param>
        /// <returns>Task folder or null.</returns>
        private static TaskFolder? GetTraycerFolder(TaskService service, bool createIfMissing, out string? error)
        {
            error = null;
            TaskFolder? folder = null;

            try
            {
                folder = service.GetFolder(TraycerTaskFolderPath);
            }
            catch (Exception ex)
            {
                if (!IsMissingFolderException(ex))
                {
                    error = ex.Message;
                    return null;
                }
            }

            if (folder is not null)
            {
                return folder;
            }

            if (!createIfMissing)
            {
                return null;
            }

            try
            {
                return service.RootFolder.CreateFolder(TraycerTaskFolderName);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Detects scheduler missing-folder exceptions.
        /// </summary>
        /// <param name="ex">Exception instance.</param>
        /// <returns>True when folder missing.</returns>
        private static bool IsMissingFolderException(Exception ex)
        {
            return ex is FileNotFoundException
                || ex is DirectoryNotFoundException
                || ex is IOException ioEx && (ioEx.HResult == unchecked((int)0x80070002) || ioEx.HResult == unchecked((int)0x80070003))
                || ex is System.Runtime.InteropServices.COMException comEx && (comEx.ErrorCode == unchecked((int)0x80070002) || comEx.ErrorCode == unchecked((int)0x80070003));
        }

        /// <summary>
        /// Builds a scheduler trigger from config.
        /// </summary>
        /// <param name="config">Task config.</param>
        /// <param name="trigger">Output trigger.</param>
        /// <param name="error">Unsupported value.</param>
        /// <returns>True when trigger created.</returns>
        private static bool TryCreateTrigger(TaskConfig config, out Trigger? trigger, out string? error)
        {
            trigger = null;
            error = null;

            var schedule = config.Schedule!;
            var frequency = schedule.Frequency?.Trim().ToLowerInvariant();
            var startBoundary = ResolveStartBoundary(schedule);

            switch (frequency)
            {
                case "minute":
                case "minutes":
                {
                    var interval = Math.Max(1, schedule.Interval ?? 1);
                    var trig = new DailyTrigger { DaysInterval = 1, StartBoundary = startBoundary };
                    trig.Repetition.Interval = TimeSpan.FromMinutes(interval);
                    trig.Repetition.Duration = TimeSpan.FromDays(1);
                    trig.Repetition.StopAtDurationEnd = false;
                    trigger = trig;
                    return true;
                }

                case "hour":
                case "hours":
                case "hourly":
                {
                    var interval = Math.Max(1, schedule.Interval ?? 1);
                    var trig = new DailyTrigger { DaysInterval = 1, StartBoundary = startBoundary };
                    trig.Repetition.Interval = TimeSpan.FromHours(interval);
                    trig.Repetition.Duration = TimeSpan.FromDays(1);
                    trig.Repetition.StopAtDurationEnd = false;
                    trigger = trig;
                    return true;
                }

                case "day":
                case "days":
                case "daily":
                {
                    var interval = Math.Max(1, schedule.Interval ?? 1);
                    trigger = new DailyTrigger
                    {
                        DaysInterval = (short)Math.Min(interval, short.MaxValue),
                        StartBoundary = startBoundary
                    };
                    return true;
                }

                case "logon":
                case "onlogon":
                    trigger = new LogonTrigger();
                    return true;

                case "once":
                    trigger = new TimeTrigger { StartBoundary = startBoundary };
                    return true;

                default:
                    error = schedule.Frequency;
                    return false;
            }
        }

        /// <summary>
        /// Derives the next start boundary.
        /// </summary>
        /// <param name="schedule">Schedule payload.</param>
        /// <returns>Start time.</returns>
        private static DateTime ResolveStartBoundary(ScheduleConfig schedule)
        {
            if (!string.IsNullOrWhiteSpace(schedule.Start) && TimeSpan.TryParse(schedule.Start, CultureInfo.InvariantCulture, out var timeOfDay))
            {
                var today = DateTime.Today.Add(timeOfDay);
                return today > DateTime.Now ? today : today.AddDays(1);
            }

            return DateTime.Now.AddMinutes(1);
        }

        /// <summary>
        /// Syncs managed tasks with config set.
        /// </summary>
        /// <param name="configs">Configured tasks.</param>
        private void ApplyTasks(IEnumerable<TaskConfig> configs)
        {
            var incoming = configs.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);

            foreach (var existing in _tasks.Values.ToList())
            {
                if (!incoming.ContainsKey(existing.Config.Id))
                {
                    if (!existing.IsScheduled && existing.ActiveProcess is { HasExited: false })
                    {
                        try
                        {
                            existing.ActiveProcess.Kill(true);
                        }
                        catch
                        {
                        }
                    }

                    if (existing.IsScheduled)
                    {
                        RemoveScheduledTask(existing.Config);
                    }

                    _tasks.Remove(existing.Config.Id);
                }
            }

            foreach (var cfg in incoming.Values)
            {
                if (!_tasks.TryGetValue(cfg.Id, out var managed))
                {
                    managed = new ManagedTask(cfg);
                    _tasks[cfg.Id] = managed;
                }
                else if (!managed.Config.Equals(cfg))
                {
                    if (managed.IsScheduled)
                    {
                        RemoveScheduledTask(managed.Config);
                    }
                    else if (managed.ActiveProcess is { HasExited: false })
                    {
                        try
                        {
                            managed.ActiveProcess.Kill(true);
                        }
                        catch
                        {
                        }

                        managed.ActiveProcess = null;
                    }

                    managed.Update(cfg);
                }
            }

            foreach (var task in _tasks.Values)
            {
                if (task.IsScheduled)
                {
                    EnsureScheduledTask(task.Config);
                }
                else if (task.Config.AutoStart)
                {
                    StartTaskProcess(task, false);
                }
            }

            RefreshTrayMenu();
        }

        /// <summary>
        /// Parses task array from JSON.
        /// </summary>
        /// <param name="tasksEl">Tasks element.</param>
        /// <returns>Task configs.</returns>
        private List<TaskConfig> ParseTaskConfigs(JsonElement tasksEl)
        {
            var list = new List<TaskConfig>();
            foreach (var element in tasksEl.EnumerateArray())
            {
                var cfg = ParseTaskConfig(element);
                if (cfg != null)
                {
                    list.Add(cfg);
                }
            }

            return list;
        }

        /// <summary>
        /// Parses a single task object.
        /// </summary>
        /// <param name="element">JSON node.</param>
        /// <returns>Task config or null.</returns>
        private TaskConfig? ParseTaskConfig(JsonElement element)
        {
            if (!element.TryGetProperty("id", out var idEl))
            {
                return null;
            }

            var id = idEl.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            if (!element.TryGetProperty("command", out var cmdEl))
            {
                return null;
            }

            var command = cmdEl.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(command))
            {
                return null;
            }

            command = ResolveExecutablePath(command);

            string? arguments = element.TryGetProperty("args", out var argsEl) ? argsEl.GetString() : null;
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                arguments = Environment.ExpandEnvironmentVariables(arguments);
            }

            string mode = element.TryGetProperty("mode", out var modeEl) ? modeEl.GetString() ?? "once" : "once";
            bool autoStart = true;
            if (element.TryGetProperty("autoStart", out var autoEl))
            {
                if (autoEl.ValueKind == JsonValueKind.True)
                {
                    autoStart = true;
                }
                else if (autoEl.ValueKind == JsonValueKind.False)
                {
                    autoStart = false;
                }
                else if (autoEl.ValueKind == JsonValueKind.String && bool.TryParse(autoEl.GetString(), out var parsed))
                {
                    autoStart = parsed;
                }
            }

            string? workingDirectory = element.TryGetProperty("workingDirectory", out var wdEl) ? wdEl.GetString() : null;
            workingDirectory = ResolveWorkingDirectory(workingDirectory);

            if (string.Equals(mode, "schedule", StringComparison.OrdinalIgnoreCase))
            {
                var hidden = MaybeSwapPythonForPythonw(command);
                if (!string.IsNullOrWhiteSpace(hidden))
                {
                    command = hidden!;
                }
            }

            ScheduleConfig? schedule = null;
            if (string.Equals(mode, "schedule", StringComparison.OrdinalIgnoreCase) && element.TryGetProperty("schedule", out var schedEl) && schedEl.ValueKind == JsonValueKind.Object)
            {
                string frequency = schedEl.TryGetProperty("frequency", out var freqEl) ? freqEl.GetString() ?? string.Empty : string.Empty;
                int? interval = null;
                if (schedEl.TryGetProperty("interval", out var intEl))
                {
                    if (intEl.ValueKind == JsonValueKind.Number && intEl.TryGetInt32(out var val))
                    {
                        interval = val;
                    }
                    else if (intEl.ValueKind == JsonValueKind.String && int.TryParse(intEl.GetString(), out val))
                    {
                        interval = val;
                    }
                }

                string? start = schedEl.TryGetProperty("start", out var startEl) ? startEl.GetString() : null;
                schedule = new ScheduleConfig(frequency, interval, start);
            }

            return new TaskConfig(id, command, arguments, mode, autoStart, schedule, workingDirectory);
        }

        /// <summary>
        /// Resolves an executable path search.
        /// </summary>
        /// <param name="command">Command text.</param>
        /// <returns>Absolute path or original.</returns>
        private static string ResolveExecutablePath(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return command;
            }

            var expanded = Environment.ExpandEnvironmentVariables(command);
            if (Path.IsPathRooted(expanded))
            {
                return Path.GetFullPath(expanded);
            }

            var baseDirCandidate = Path.Combine(AppContext.BaseDirectory, expanded);
            if (File.Exists(baseDirCandidate))
            {
                return Path.GetFullPath(baseDirCandidate);
            }

            var search = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(search))
            {
                foreach (var fragment in search.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(fragment))
                    {
                        continue;
                    }

                    try
                    {
                        var candidate = Path.Combine(fragment.Trim(), expanded);
                        if (File.Exists(candidate))
                        {
                            return Path.GetFullPath(candidate);
                        }

                        if (!expanded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            var exeCandidate = candidate + ".exe";
                            if (File.Exists(exeCandidate))
                            {
                                return Path.GetFullPath(exeCandidate);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }

            if (!expanded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var exeCandidate = expanded + ".exe";
                var exeInBase = Path.Combine(AppContext.BaseDirectory, exeCandidate);
                if (File.Exists(exeInBase))
                {
                    return Path.GetFullPath(exeInBase);
                }
            }

            return expanded;
        }

        /// <summary>
        /// Resolves the working directory path.
        /// </summary>
        /// <param name="workingDirectory">Directory input.</param>
        /// <returns>Absolute directory or null.</returns>
        private static string? ResolveWorkingDirectory(string? workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                return null;
            }

            var expanded = Environment.ExpandEnvironmentVariables(workingDirectory);
            if (!Path.IsPathRooted(expanded))
            {
                expanded = Path.Combine(AppContext.BaseDirectory, expanded);
            }

            try
            {
                return Path.GetFullPath(expanded);
            }
            catch
            {
                return workingDirectory;
            }
        }

        /// <summary>
        /// Finds pythonw alongside python.exe.
        /// </summary>
        /// <param name="command">Original command.</param>
        /// <returns>Replacement path or null.</returns>
        private static string? MaybeSwapPythonForPythonw(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return null;
            }

            var file = Path.GetFileName(command);
            if (!string.Equals(file, "python.exe", StringComparison.OrdinalIgnoreCase) && !string.Equals(file, "python", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                var dir = Path.GetDirectoryName(command);
                if (string.IsNullOrWhiteSpace(dir))
                {
                    return null;
                }

                var candidate = Path.Combine(dir, "pythonw.exe");
                return File.Exists(candidate) ? candidate : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Retrieves the current user principal string.
        /// </summary>
        /// <returns>User or domain\user.</returns>
        private static string? GetCurrentUserPrincipal()
        {
            try
            {
                var user = Environment.UserName;
                if (string.IsNullOrWhiteSpace(user))
                {
                    return null;
                }

                var domain = Environment.UserDomainName;
                if (!string.IsNullOrWhiteSpace(domain) && !string.Equals(domain, user, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{domain}\\{user}";
                }

                return user;
            }
            catch
            {
                return null;
            }
        }
    }
}



