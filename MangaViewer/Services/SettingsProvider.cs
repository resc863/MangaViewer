using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace MangaViewer.Services
{
    /// <summary>
    /// SettingsProvider
    /// Purpose: Lightweight JSON-backed key/value store under %LocalAppData%/MangaViewer/settings.json.
    /// Characteristics:
    ///  - Uses ReaderWriterLockSlim for concurrent access; write locks only on mutation or initial load.
    ///  - Stores primitive values as JsonElements internally; serializes a flat object when persisting.
    ///  - Avoids WinRT ApplicationData dependency for broader host flexibility (e.g., desktop scenarios).
    ///  - Swallows IO/parse errors; corrupted file replaced silently on next successful write.
    /// Extension Ideas:
    ///  - Add typed structs or nested objects; currently supports primitives via simple serialization.
    ///  - Introduce change notifications (events) for reactive UI binding.
    ///  - Provide async APIs if heavy writes become performance concern.
    /// </summary>
    public static class SettingsProvider
    {
        private static readonly string s_folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MangaViewer");
        private static readonly string s_file = Path.Combine(s_folder, "settings.json");
        private static readonly ReaderWriterLockSlim s_lock = new();
        private static Dictionary<string, JsonElement> s_cache = new(StringComparer.OrdinalIgnoreCase);
        private static bool s_loaded;

        private static void EnsureLoaded()
        {
            if (s_loaded) return;
            s_lock.EnterWriteLock();
            try
            {
                if (s_loaded) return;
                if (!Directory.Exists(s_folder)) Directory.CreateDirectory(s_folder);
                if (File.Exists(s_file))
                {
                    try
                    {
                        var json = File.ReadAllText(s_file);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                            {
                                var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                                foreach (var prop in doc.RootElement.EnumerateObject())
                                    dict[prop.Name] = prop.Value.Clone();
                                s_cache = dict;
                            }
                        }
                    }
                    catch { }
                }
                s_loaded = true;
            }
            finally { s_lock.ExitWriteLock(); }
        }

        private static void Persist()
        {
            try
            {
                s_lock.EnterReadLock();
                var obj = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in s_cache)
                {
                    obj[kv.Key] = kv.Value.ValueKind switch
                    {
                        JsonValueKind.String => kv.Value.GetString(),
                        JsonValueKind.Number => kv.Value.TryGetDouble(out var d) ? d : kv.Value.ToString(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => null,
                        _ => kv.Value.ToString()
                    };
                }
                var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = false });
                Directory.CreateDirectory(s_folder);
                File.WriteAllText(s_file, json);
            }
            catch { }
            finally { if (s_lock.IsReadLockHeld) s_lock.ExitReadLock(); }
        }

        public static double GetDouble(string key, double @default = 0)
        {
            EnsureLoaded();
            s_lock.EnterReadLock();
            try
            {
                if (s_cache.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d)) return d;
                return @default;
            }
            finally { s_lock.ExitReadLock(); }
        }
        public static bool GetBool(string key, bool @default = false)
        {
            EnsureLoaded();
            s_lock.EnterReadLock();
            try
            {
                if (s_cache.TryGetValue(key, out var el))
                {
                    if (el.ValueKind == JsonValueKind.True) return true;
                    if (el.ValueKind == JsonValueKind.False) return false;
                    if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var b)) return b;
                }
                return @default;
            }
            finally { s_lock.ExitReadLock(); }
        }
        public static string GetString(string key, string @default = "")
        {
            EnsureLoaded();
            s_lock.EnterReadLock();
            try
            {
                if (s_cache.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
                {
                    return el.GetString() ?? @default;
                }
                return @default;
            }
            finally { s_lock.ExitReadLock(); }
        }
        public static void SetDouble(string key, double value)
        {
            EnsureLoaded();
            s_lock.EnterWriteLock();
            try
            {
                using var doc = JsonDocument.Parse(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                s_cache[key] = doc.RootElement.Clone();
            }
            catch { }
            finally { s_lock.ExitWriteLock(); }
            Persist();
        }
        public static void SetBool(string key, bool value)
        {
            EnsureLoaded();
            s_lock.EnterWriteLock();
            try
            {
                using var doc = JsonDocument.Parse(value ? "true" : "false");
                s_cache[key] = doc.RootElement.Clone();
            }
            catch { }
            finally { s_lock.ExitWriteLock(); }
            Persist();
        }
        public static void SetString(string key, string value)
        {
            EnsureLoaded();
            s_lock.EnterWriteLock();
            try
            {
                string safe = value ?? string.Empty;
                using var doc = JsonDocument.Parse("\"" + safe.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
                s_cache[key] = doc.RootElement.Clone();
            }
            catch { }
            finally { s_lock.ExitWriteLock(); }
            Persist();
        }
    }
}
