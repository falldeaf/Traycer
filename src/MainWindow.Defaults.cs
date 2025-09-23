using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace Traycer
{
    public partial class MainWindow
    {
        private const string DEFAULTS_FILE = "traycer.defaults.json";

        private static string GetLocalDefaultsDirectory()
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "Traycer");
        }

        private static string GetLocalDefaultsPath()
        {
            return Path.Combine(GetLocalDefaultsDirectory(), DEFAULTS_FILE);
        }

        private bool TryEnsureDefaultsFile(out string? path)
        {
            path = GetLocalDefaultsPath();

            try
            {
                if (File.Exists(path))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Traycer could not verify its configuration path: {ex.Message}", "Traycer", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                path = null;
                return false;
            }

            var prompt = $"Traycer configuration file was not found at:{Environment.NewLine}{path}{Environment.NewLine}{Environment.NewLine}Would you like to create a template now?";
            var result = System.Windows.MessageBox.Show(prompt, "Traycer", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (result != System.Windows.MessageBoxResult.Yes)
            {
                System.Windows.MessageBox.Show("Traycer cannot start without a configuration file.", "Traycer", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                path = null;
                return false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var templateSource = Path.Combine(AppContext.BaseDirectory, DEFAULTS_FILE);
                if (!File.Exists(templateSource))
                {
                    throw new FileNotFoundException("Template defaults file not found beside the application executable.", templateSource);
                }

                File.Copy(templateSource, path, overwrite: false);
                System.Windows.MessageBox.Show($"Traycer created a configuration template at:{Environment.NewLine}{path}", "Traycer", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return true;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Traycer could not create a configuration template: {ex.Message}", "Traycer", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                path = null;
                return false;
            }
        }

        private bool TryLoadDefaults(string path, out string? error)
        {
            error = null;
            try
            {
                if (!File.Exists(path))
                {
                    error = $"Traycer defaults file not found at {path}.";
                    return false;
                }

                var json = File.ReadAllText(path, Encoding.UTF8);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                List<TaskConfig>? tasks = null;

                if (root.TryGetProperty("placement", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    if (p.TryGetProperty("height", out var hEl)) _heightOverride = hEl.GetDouble();
                    if (p.TryGetProperty("bottomOffset", out var bEl)) _bottomOffset = bEl.GetDouble();
                    if (p.TryGetProperty("padding", out var padEl)) _padding = padEl.GetDouble();
                    if (p.TryGetProperty("cornerRadius", out var crEl)) _cornerRadius = crEl.GetDouble();
                }

                if (root.TryGetProperty("wells", out var wellsEl) && wellsEl.ValueKind == JsonValueKind.Array)
                {
                    var tmp = new List<(int? index, WellConfig cfg)>();
                    int pos = 0;
                    foreach (var w in wellsEl.EnumerateArray())
                    {
                        string id = w.GetProperty("id").GetString() ?? $"well{pos}";
                        double width = w.TryGetProperty("width", out var widEl) ? widEl.GetDouble() : 200.0;
                        int? index = w.TryGetProperty("index", out var iEl) ? iEl.GetInt32() : (int?)null;
                        tmp.Add((index, new WellConfig(id, width)));
                        pos++;
                    }

                    IEnumerable<WellConfig> ordered = tmp.Any(t => t.index.HasValue)
                        ? tmp.OrderBy(t => t.index ?? int.MaxValue).Select(t => t.cfg)
                        : tmp.Select(t => t.cfg);

                    ApplyConfig(ordered);
                }

                if (root.TryGetProperty("updates", out var updatesEl) && updatesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var u in updatesEl.EnumerateArray())
                    {
                        string id = u.GetProperty("well").GetString() ?? string.Empty;
                        string? text = u.TryGetProperty("text", out var tEl) ? tEl.GetString() : null;
                        string? fg = u.TryGetProperty("fg", out var fgEl) ? fgEl.GetString() : null;
                        string? bg = u.TryGetProperty("bg", out var bgEl) ? bgEl.GetString() : null;
                        bool? blink = u.TryGetProperty("blink", out var bEl) ? bEl.GetBoolean() : null;
                        string? action = u.TryGetProperty("action", out var aEl) ? aEl.GetString() : null;
                        SetWell(id, text, fg, bg, blink, action);
                    }
                }

                if (root.TryGetProperty("tasks", out var tasksEl) && tasksEl.ValueKind == JsonValueKind.Array)
                {
                    tasks = ParseTaskConfigs(tasksEl);
                }

                if (root.TryGetProperty("actions", out var actsEl) && actsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in actsEl.EnumerateObject())
                    {
                        _actions[prop.Name] = prop.Value.GetString() ?? string.Empty;
                    }
                }

                ApplyTasks(tasks ?? Enumerable.Empty<TaskConfig>());
                ApplyChrome();
                RefreshTrayMenu();
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to load defaults: {ex.Message}";
                return false;
            }
        }
    }
}
