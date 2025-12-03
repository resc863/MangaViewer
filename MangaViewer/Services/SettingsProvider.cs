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
    ///  - Stores values as typed objects using modern JSON serialization.
    ///  - Provides generic Get<T>/Set<T> methods for type-safe access.
    ///  - Swallows IO/parse errors; corrupted file replaced silently on next successful write.
    /// Extension Ideas:
    ///  - Add change notifications (events) for reactive UI binding.
    ///  - Provide async APIs if heavy writes become performance concern.
    /// </summary>
    public static class SettingsProvider
    {
        private static readonly string s_folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MangaViewer");
        private static readonly string s_file = Path.Combine(s_folder, "settings.json");
        private static readonly ReaderWriterLockSlim s_lock = new();
        private static readonly JsonSerializerOptions s_options = new() 
        { 
            WriteIndented = false,
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true
        };
        
        private static Dictionary<string, object?> s_cache = new(StringComparer.OrdinalIgnoreCase);
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
                            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, s_options);
                            if (dict != null)
                            {
                                var typedCache = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                                foreach (var kvp in dict)
                                {
                                    typedCache[kvp.Key] = kvp.Value;
                                }
                                s_cache = typedCache;
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
                try
                {
                    var toSerialize = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kvp in s_cache)
                    {
                        // Convert JsonElement back to primitive types for cleaner JSON
                        if (kvp.Value is JsonElement element)
                        {
                            toSerialize[kvp.Key] = element.ValueKind switch
                            {
                                JsonValueKind.String => element.GetString(),
                                JsonValueKind.Number => element.TryGetDouble(out var d) ? d : 0.0,
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                JsonValueKind.Null => null,
                                _ => kvp.Value
                            };
                        }
                        else
                        {
                            toSerialize[kvp.Key] = kvp.Value;
                        }
                    }
                    
                    var json = JsonSerializer.Serialize(toSerialize, s_options);
                    Directory.CreateDirectory(s_folder);
                    File.WriteAllText(s_file, json);
                }
                finally { s_lock.ExitReadLock(); }
            }
            catch { }
        }

        /// <summary>
        /// Get a typed value from settings. Returns default if key not found or conversion fails.
        /// </summary>
        public static T Get<T>(string key, T @default = default!)
        {
            EnsureLoaded();
            s_lock.EnterReadLock();
            try
            {
                if (!s_cache.TryGetValue(key, out var value))
                    return @default;

                // Direct type match
                if (value is T typedValue)
                    return typedValue;

                // JsonElement conversion
                if (value is JsonElement element)
                {
                    try 
                    { 
                        return JsonSerializer.Deserialize<T>(element.GetRawText(), s_options) ?? @default; 
                    }
                    catch { return @default; }
                }

                // Try convert for primitive types
                try
                {
                    if (typeof(T) == typeof(double) && value != null)
                        return (T)(object)Convert.ToDouble(value);
                    if (typeof(T) == typeof(bool) && value != null)
                        return (T)(object)Convert.ToBoolean(value);
                    if (typeof(T) == typeof(string))
                        return (T)(object)(value?.ToString() ?? string.Empty);
                }
                catch { }

                return @default;
            }
            finally { s_lock.ExitReadLock(); }
        }

        /// <summary>
        /// Set a typed value in settings and persist immediately.
        /// </summary>
        public static void Set<T>(string key, T value)
        {
            EnsureLoaded();
            s_lock.EnterWriteLock();
            try
            {
                s_cache[key] = value;
            }
            finally { s_lock.ExitWriteLock(); }
            Persist();
        }

        // Legacy compatibility methods
        public static double GetDouble(string key, double @default = 0) => Get(key, @default);
        public static bool GetBool(string key, bool @default = false) => Get(key, @default);
        public static string GetString(string key, string @default = "") => Get(key, @default);
        public static void SetDouble(string key, double value) => Set(key, value);
        public static void SetBool(string key, bool value) => Set(key, value);
        public static void SetString(string key, string value) => Set(key, value);
    }
}
