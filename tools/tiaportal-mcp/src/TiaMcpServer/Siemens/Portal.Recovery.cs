using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Crash recovery. When the TIA Portal process this server is bound to has died (V21 can take
    /// itself down, e.g. issue #36), every later call failed against disposed handles until someone
    /// reconnected by hand and reopened the project. Now the tool wrapper checks the bound PID
    /// before each TIA call (a local process lookup, no Openness call), and on a dead process
    /// reconnects (attach to a running TIA, else start a new one) and reopens or re-attaches the
    /// project that was open, reporting it in that call's response meta ("recovery").
    /// </summary>
    public partial class Portal
    {
        private int? _portalPid;
        private object? _pidPortal;
        private object? _notedProject;
        private string? _lastProjectPath;
        private string? _lastProjectName;
        private JsonObject? _lastRecovery;

        public string? LastProjectPath => _lastProjectPath;
        public JsonObject? LastRecovery => _lastRecovery;

        /// <summary>
        /// After every TIA tool call: remember the bound TIA process id and the open project's path,
        /// whichever way they were bound (Connect, OpenProject, AttachToOpenProject, self-heal).
        /// Only touches Openness when the bound portal/project object changed.
        /// </summary>
        public void NoteSessionState()
        {
            if (_portal == null)
            {
                _portalPid = null;
                _pidPortal = null;
            }
            else if (!ReferenceEquals(_portal, _pidPortal))
            {
                try
                {
                    _portalPid = _portal.GetCurrentProcess().Id;
                    _pidPortal = _portal;
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Could not read the TIA Portal process id");
                }
            }

            if (_project != null && !ReferenceEquals(_project, _notedProject))
            {
                try
                {
                    var path = _project.Path?.FullName;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        _lastProjectPath = path;
                        _lastProjectName = _project.Name;
                    }
                    _notedProject = _project;
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Could not read the open project's path");
                }
            }
        }

        /// <summary>
        /// Before a TIA tool call: null when the bound TIA process is alive (or nothing is bound);
        /// otherwise recovers and returns what happened.
        /// </summary>
        public JsonObject? EnsureAliveOrRecover()
        {
            if (_portal == null || _portalPid is not int pid) return null;
            if (IsProcessAlive(pid)) return null;

            var sw = Stopwatch.StartNew();
            var lastPath = _lastProjectPath;
            var lastName = _lastProjectName;
            var info = new JsonObject
            {
                ["crashDetected"] = true,
                ["deadPid"] = pid,
                ["lastProject"] = lastPath
            };
            _logger?.LogWarning($"TIA Portal process {pid} is gone; recovering (last project: {lastPath ?? "-"}).");

            _project = null;
            _session = null;
            _projectOpenedByUs = false;
            try { _portal.Dispose(); } catch { }
            _portal = null;
            _portalPid = null;
            _pidPortal = null;
            _notedProject = null;

            string action;
            string? error = null;
            try
            {
                ConnectPortal();
                NoteSessionState();
                info["newPid"] = _portalPid;

                string? boundName = null;
                try { boundName = _project?.Name; } catch { }

                if (boundName != null && lastName != null && string.Equals(boundName, lastName, StringComparison.OrdinalIgnoreCase))
                {
                    action = "reattached";
                }
                else if (!string.IsNullOrWhiteSpace(lastPath) && File.Exists(lastPath))
                {
                    if (boundName != null)
                    {
                        action = "failed";
                        error = $"The TIA instance now attached has another project ('{boundName}') open; it was not closed. Open '{lastPath}' yourself if that is intended.";
                    }
                    else if (OpenProject(lastPath!))
                    {
                        action = "reopened";
                    }
                    else
                    {
                        action = "failed";
                        error = LastConnectError ?? "OpenProject returned false.";
                    }
                }
                else
                {
                    action = boundName == null ? "connected-no-project" : "attached-other-project";
                    if (lastPath != null) error = $"Last project file not found: {lastPath}";
                }
                NoteSessionState();
            }
            catch (Exception ex)
            {
                action = "failed";
                error = ex.Message;
            }

            info["action"] = action;
            try { info["project"] = _project?.Name; } catch { }
            if (error != null) info["error"] = error;
            info["elapsedMs"] = sw.ElapsedMilliseconds;
            info["note"] = "Unsaved changes made before the crash are lost; check the last edits.";
            _lastRecovery = info;
            _logger?.LogWarning($"TIA recovery: {info.ToJsonString()}");
            return info;
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    return !p.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return false; // no process with that id
            }
            catch
            {
                return true; // access problems: do not guess "dead"
            }
        }
    }
}
