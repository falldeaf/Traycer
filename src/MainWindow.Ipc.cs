using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Traycer
{
    public partial class MainWindow
    {
        private const string PIPE_NAME = "TraycerHud";

        /// <summary>
        /// Hosts the named-pipe server loop.
        /// </summary>
        /// <returns>Completion task.</returns>
        private async Task PipeLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PIPE_NAME, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token);

                    using var reader = new StreamReader(server, new UTF8Encoding(false));
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null && !_cts.IsCancellationRequested)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            HandleMessage(doc.RootElement);
                        }
                        catch
                        {
                        }
                    }
                }
                catch when (_cts.IsCancellationRequested)
                {
                }
                catch
                {
                    await Task.Delay(200);
                }
            }
        }

        /// <summary>
        /// Dispatches a parsed pipe message payload.
        /// </summary>
        /// <param name="msg">JSON message.</param>
        private void HandleMessage(JsonElement msg)
        {
            string op = msg.TryGetProperty("op", out var opEl) ? opEl.GetString() ?? string.Empty : string.Empty;

            if (op.Equals("config", StringComparison.OrdinalIgnoreCase))
            {
                List<WellConfig>? wells = null;
                if (msg.TryGetProperty("wells", out var wellsEl) && wellsEl.ValueKind == JsonValueKind.Array)
                {
                    wells = new List<WellConfig>();
                    foreach (var w in wellsEl.EnumerateArray())
                    {
                        var id = w.GetProperty("id").GetString() ?? string.Empty;
                        var width = w.TryGetProperty("width", out var widEl) ? widEl.GetDouble() : 200.0;
                        wells.Add(new WellConfig(id, width));
                    }
                }

                List<TaskConfig>? tasks = null;
                if (msg.TryGetProperty("tasks", out var tasksEl) && tasksEl.ValueKind == JsonValueKind.Array)
                {
                    tasks = ParseTaskConfigs(tasksEl);
                }

                if (wells != null || tasks != null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (wells != null)
                        {
                            ApplyConfig(wells);
                            ApplyChrome();
                        }

                        if (tasks != null)
                        {
                            ApplyTasks(tasks);
                        }
                    });
                }
            }
            else if (op.Equals("add", StringComparison.OrdinalIgnoreCase))
            {
                string id = msg.GetProperty("well").GetString() ?? string.Empty;
                double width = msg.TryGetProperty("width", out var wEl) ? wEl.GetDouble() : 200.0;
                int? index = msg.TryGetProperty("index", out var iEl) ? iEl.GetInt32() : (int?)null;
                Dispatcher.Invoke(() =>
                {
                    AddWell(id, width, index);
                    ReassertTopmost();
                });
            }
            else if (op.Equals("remove", StringComparison.OrdinalIgnoreCase))
            {
                string id = msg.GetProperty("well").GetString() ?? string.Empty;
                Dispatcher.Invoke(() =>
                {
                    RemoveWell(id);
                    ReassertTopmost();
                });
            }
            else if (op.Equals("resize", StringComparison.OrdinalIgnoreCase))
            {
                string id = msg.GetProperty("well").GetString() ?? string.Empty;
                double width = msg.GetProperty("width").GetDouble();
                Dispatcher.Invoke(() => ResizeWell(id, width));
            }
            else if (op.Equals("set", StringComparison.OrdinalIgnoreCase))
            {
                string id = msg.GetProperty("well").GetString() ?? string.Empty;
                string? text = msg.TryGetProperty("text", out var tEl) ? tEl.GetString() : null;
                string? fg = msg.TryGetProperty("fg", out var fgEl) ? fgEl.GetString() : null;
                string? bg = msg.TryGetProperty("bg", out var bgEl) ? bgEl.GetString() : null;
                bool? blink = msg.TryGetProperty("blink", out var bEl) ? bEl.GetBoolean() : null;
                string? action = msg.TryGetProperty("action", out var aEl) ? aEl.GetString() : null;
                Dispatcher.Invoke(() => SetWell(id, text, fg, bg, blink, action));
            }
            else if (op.Equals("bulk", StringComparison.OrdinalIgnoreCase))
            {
                if (msg.TryGetProperty("updates", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    Dispatcher.Invoke(() =>
                    {
                        foreach (var u in arr.EnumerateArray())
                        {
                            string id = u.GetProperty("well").GetString() ?? string.Empty;
                            string? text = u.TryGetProperty("text", out var tEl) ? tEl.GetString() : null;
                            string? fg = u.TryGetProperty("fg", out var fgEl) ? fgEl.GetString() : null;
                            string? bg = u.TryGetProperty("bg", out var bgEl) ? bgEl.GetString() : null;
                            bool? blink = u.TryGetProperty("blink", out var bEl) ? bEl.GetBoolean() : null;
                            string? action = u.TryGetProperty("action", out var aEl) ? aEl.GetString() : null;
                            SetWell(id, text, fg, bg, blink, action);
                        }
                    });
                }
            }
            else if (op.Equals("bind", StringComparison.OrdinalIgnoreCase))
            {
                string id = msg.GetProperty("well").GetString() ?? string.Empty;
                string action = msg.GetProperty("action").GetString() ?? string.Empty;
                Dispatcher.Invoke(() =>
                {
                    _actions[id] = action;
                    RefreshTrayMenu();
                });
            }
            else if (op.Equals("placement", StringComparison.OrdinalIgnoreCase))
            {
                if (msg.TryGetProperty("height", out var hEl))
                {
                    _heightOverride = hEl.GetDouble();
                }

                if (msg.TryGetProperty("bottomOffset", out var bEl))
                {
                    _bottomOffset = bEl.GetDouble();
                }

                if (msg.TryGetProperty("padding", out var pEl))
                {
                    _padding = pEl.GetDouble();
                }

                if (msg.TryGetProperty("cornerRadius", out var cEl))
                {
                    _cornerRadius = cEl.GetDouble();
                }

                Dispatcher.Invoke(() =>
                {
                    PositionOverTaskbarLeftThird();
                    ApplyChrome();
                    ReassertTopmost();
                });
            }
        }
    }
}
