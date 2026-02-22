using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    ///  - Provides generic Get&lt;T&gt;/Set&lt;T&gt; methods for type-safe access.
    ///  - Provides GetSecret/SetSecret methods for DPAPI-encrypted sensitive values (e.g. API keys).
    ///  - Swallows IO/parse errors; corrupted file replaced silently on next successful write.
    /// </summary>
    public static class SettingsProvider
    {
        private static readonly string s_folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MangaViewer");
        private static readonly string s_file = Path.Combine(s_folder, "settings.json");
        private static readonly ReaderWriterLockSlim s_lock = new(LockRecursionPolicy.NoRecursion);
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
                Dictionary<string, object?> toSerialize;
                
                s_lock.EnterReadLock();
                try
                {
                    toSerialize = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kvp in s_cache)
                    {
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
                }
                finally { s_lock.ExitReadLock(); }
                
                var json = JsonSerializer.Serialize(toSerialize, s_options);
                Directory.CreateDirectory(s_folder);
                File.WriteAllText(s_file, json);
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

                if (value is T typedValue)
                    return typedValue;

                if (value is JsonElement element)
                {
                    try 
                    { 
                        return JsonSerializer.Deserialize<T>(element.GetRawText(), s_options) ?? @default; 
                    }
                    catch { return @default; }
                }

                try
                {
                    if (typeof(T) == typeof(double) && value != null)
                        return (T)(object)Convert.ToDouble(value);
                    if (typeof(T) == typeof(int) && value != null)
                        return (T)(object)Convert.ToInt32(value);
                    if (typeof(T) == typeof(long) && value != null)
                        return (T)(object)Convert.ToInt64(value);
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

        /// <summary>
        /// Check if a key exists in settings.
        /// </summary>
        public static bool Contains(string key)
        {
            EnsureLoaded();
            
            s_lock.EnterReadLock();
            try
            {
                return s_cache.ContainsKey(key);
            }
            finally { s_lock.ExitReadLock(); }
        }

        /// <summary>
        /// Remove a key from settings.
        /// </summary>
        public static bool Remove(string key)
        {
            EnsureLoaded();

            s_lock.EnterWriteLock();
            try
            {
                var removed = s_cache.Remove(key);
                if (removed)
                    Persist();
                return removed;
            }
            finally { s_lock.ExitWriteLock(); }
        }

        private const string SecretPrefix = "dpapi:";

        /// <summary>
        /// Get a DPAPI-encrypted secret. Automatically migrates legacy plaintext values on first read.
        /// </summary>
        public static string GetSecret(string key, string @default = "")
        {
            var raw = Get<string>(key, "");
            if (string.IsNullOrEmpty(raw))
                return @default;

            if (raw.StartsWith(SecretPrefix, StringComparison.Ordinal))
            {
                try
                {
                    var encrypted = Convert.FromBase64String(raw[SecretPrefix.Length..]);
                    var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(decrypted);
                }
                catch
                {
                    return @default;
                }
            }

            // Legacy plaintext value: encrypt and persist
            SetSecret(key, raw);
            return raw;
        }

        /// <summary>
        /// Store a secret encrypted with DPAPI (CurrentUser scope).
        /// </summary>
        public static void SetSecret(string key, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                Set(key, "");
                return;
            }

            try
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                Set(key, SecretPrefix + Convert.ToBase64String(encrypted));
            }
            catch
            {
                Set(key, value);
            }
        }
    }
}
