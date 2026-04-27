using System;
using System.Collections.Generic;
using System.Globalization;
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
        private enum SettingValueKind
        {
            Null,
            String,
            Boolean,
            Integer,
            Number,
            RawJson
        }

        private readonly struct SettingValue
        {
            public SettingValueKind Kind { get; }
            public string? StringValue { get; }
            public bool BooleanValue { get; }
            public long IntegerValue { get; }
            public double NumberValue { get; }
            public string? RawJson { get; }

            private SettingValue(SettingValueKind kind, string? stringValue = null, bool booleanValue = false, long integerValue = 0, double numberValue = 0, string? rawJson = null)
            {
                Kind = kind;
                StringValue = stringValue;
                BooleanValue = booleanValue;
                IntegerValue = integerValue;
                NumberValue = numberValue;
                RawJson = rawJson;
            }

            public static SettingValue FromString(string? value) => new(SettingValueKind.String, stringValue: value ?? string.Empty);
            public static SettingValue FromBoolean(bool value) => new(SettingValueKind.Boolean, booleanValue: value);
            public static SettingValue FromInteger(long value) => new(SettingValueKind.Integer, integerValue: value);
            public static SettingValue FromNumber(double value) => new(SettingValueKind.Number, numberValue: value);
            public static SettingValue FromNull() => new(SettingValueKind.Null);
            public static SettingValue FromRawJson(string rawJson) => new(SettingValueKind.RawJson, rawJson: rawJson);

            public static SettingValue FromJsonElement(JsonElement element)
            {
                return element.ValueKind switch
                {
                    JsonValueKind.String => FromString(element.GetString()),
                    JsonValueKind.True => FromBoolean(true),
                    JsonValueKind.False => FromBoolean(false),
                    JsonValueKind.Number => element.TryGetInt64(out var integerValue)
                        ? FromInteger(integerValue)
                        : (element.TryGetDouble(out var numberValue)
                            ? FromNumber(numberValue)
                            : FromRawJson(element.GetRawText())),
                    JsonValueKind.Null => FromNull(),
                    _ => FromRawJson(element.GetRawText())
                };
            }

            public void Write(Utf8JsonWriter writer, string key)
            {
                writer.WritePropertyName(key);
                switch (Kind)
                {
                    case SettingValueKind.String:
                        writer.WriteStringValue(StringValue);
                        break;
                    case SettingValueKind.Boolean:
                        writer.WriteBooleanValue(BooleanValue);
                        break;
                    case SettingValueKind.Integer:
                        writer.WriteNumberValue(IntegerValue);
                        break;
                    case SettingValueKind.Number:
                        writer.WriteNumberValue(NumberValue);
                        break;
                    case SettingValueKind.RawJson:
                        writer.WriteRawValue(RawJson ?? "null");
                        break;
                    default:
                        writer.WriteNullValue();
                        break;
                }
            }
        }

        private static readonly string s_folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MangaViewer");
        private static readonly string s_file = Path.Combine(s_folder, "settings.json");
        private static readonly ReaderWriterLockSlim s_lock = new(LockRecursionPolicy.NoRecursion);

        private static Dictionary<string, SettingValue> s_cache = new(StringComparer.OrdinalIgnoreCase);
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
                            using var document = JsonDocument.Parse(json);
                            if (document.RootElement.ValueKind == JsonValueKind.Object)
                            {
                                var typedCache = new Dictionary<string, SettingValue>(StringComparer.OrdinalIgnoreCase);
                                foreach (var property in document.RootElement.EnumerateObject())
                                {
                                    typedCache[property.Name] = SettingValue.FromJsonElement(property.Value);
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
                Dictionary<string, SettingValue> toSerialize;
                
                s_lock.EnterReadLock();
                try
                {
                    toSerialize = new Dictionary<string, SettingValue>(s_cache, StringComparer.OrdinalIgnoreCase);
                }
                finally { s_lock.ExitReadLock(); }

                Directory.CreateDirectory(s_folder);
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    foreach (var kvp in s_cache)
                    {
                        kvp.Value.Write(writer, kvp.Key);
                    }
                    writer.WriteEndObject();
                }

                File.WriteAllText(s_file, Encoding.UTF8.GetString(stream.ToArray()));
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

                Type targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

                if (targetType == typeof(string))
                    return (T)(object)GetStringValue(value, @default?.ToString() ?? string.Empty);

                if (targetType == typeof(bool) && TryGetBooleanValue(value, out bool boolValue))
                    return (T)(object)boolValue;

                if (targetType == typeof(int) && TryGetInt32Value(value, out int intValue))
                    return (T)(object)intValue;

                if (targetType == typeof(long) && TryGetInt64Value(value, out long longValue))
                    return (T)(object)longValue;

                if (targetType == typeof(double) && TryGetDoubleValue(value, out double doubleValue))
                    return (T)(object)doubleValue;

                if (targetType == typeof(float) && TryGetDoubleValue(value, out double floatSource))
                    return (T)(object)(float)floatSource;

                if (targetType.IsEnum)
                {
                    if (TryGetStringOrNumericValue(value, out string? enumText))
                    {
                        try
                        {
                            object enumValue = Enum.Parse(targetType, enumText, ignoreCase: true);
                            return (T)enumValue;
                        }
                        catch
                        {
                        }
                    }
                }

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
                s_cache[key] = CreateSettingValue(value);
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

        private static string GetStringValue(SettingValue value, string defaultValue)
        {
            return value.Kind switch
            {
                SettingValueKind.String => value.StringValue ?? string.Empty,
                SettingValueKind.Boolean => value.BooleanValue ? bool.TrueString : bool.FalseString,
                SettingValueKind.Integer => value.IntegerValue.ToString(CultureInfo.InvariantCulture),
                SettingValueKind.Number => value.NumberValue.ToString("R", CultureInfo.InvariantCulture),
                SettingValueKind.RawJson => value.RawJson ?? defaultValue,
                _ => defaultValue
            };
        }

        private static bool TryGetBooleanValue(SettingValue value, out bool result)
        {
            switch (value.Kind)
            {
                case SettingValueKind.Boolean:
                    result = value.BooleanValue;
                    return true;
                case SettingValueKind.String:
                    return bool.TryParse(value.StringValue, out result);
                case SettingValueKind.Integer:
                    result = value.IntegerValue != 0;
                    return true;
                case SettingValueKind.Number:
                    result = Math.Abs(value.NumberValue) > double.Epsilon;
                    return true;
                default:
                    result = default;
                    return false;
            }
        }

        private static bool TryGetInt32Value(SettingValue value, out int result)
        {
            if (TryGetInt64Value(value, out long longResult) && longResult >= int.MinValue && longResult <= int.MaxValue)
            {
                result = (int)longResult;
                return true;
            }

            result = default;
            return false;
        }

        private static bool TryGetInt64Value(SettingValue value, out long result)
        {
            switch (value.Kind)
            {
                case SettingValueKind.Integer:
                    result = value.IntegerValue;
                    return true;
                case SettingValueKind.Number:
                    if (Math.Abs(value.NumberValue % 1) < double.Epsilon && value.NumberValue >= long.MinValue && value.NumberValue <= long.MaxValue)
                    {
                        result = (long)value.NumberValue;
                        return true;
                    }
                    break;
                case SettingValueKind.String:
                    return long.TryParse(value.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
            }

            result = default;
            return false;
        }

        private static bool TryGetDoubleValue(SettingValue value, out double result)
        {
            switch (value.Kind)
            {
                case SettingValueKind.Integer:
                    result = value.IntegerValue;
                    return true;
                case SettingValueKind.Number:
                    result = value.NumberValue;
                    return true;
                case SettingValueKind.String:
                    return double.TryParse(value.StringValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out result);
            }

            result = default;
            return false;
        }

        private static bool TryGetStringOrNumericValue(SettingValue value, out string? result)
        {
            switch (value.Kind)
            {
                case SettingValueKind.String:
                    result = value.StringValue;
                    return !string.IsNullOrWhiteSpace(result);
                case SettingValueKind.Integer:
                    result = value.IntegerValue.ToString(CultureInfo.InvariantCulture);
                    return true;
                case SettingValueKind.Number:
                    result = value.NumberValue.ToString("R", CultureInfo.InvariantCulture);
                    return true;
                default:
                    result = null;
                    return false;
            }
        }

        private static SettingValue CreateSettingValue<T>(T value)
        {
            if (value == null)
                return SettingValue.FromNull();

            object boxed = value;
            return boxed switch
            {
                string stringValue => SettingValue.FromString(stringValue),
                bool boolValue => SettingValue.FromBoolean(boolValue),
                int intValue => SettingValue.FromInteger(intValue),
                long longValue => SettingValue.FromInteger(longValue),
                short shortValue => SettingValue.FromInteger(shortValue),
                byte byteValue => SettingValue.FromInteger(byteValue),
                double doubleValue => SettingValue.FromNumber(doubleValue),
                float floatValue => SettingValue.FromNumber(floatValue),
                decimal decimalValue => SettingValue.FromNumber((double)decimalValue),
                Enum enumValue => SettingValue.FromInteger(Convert.ToInt64(enumValue, CultureInfo.InvariantCulture)),
                IFormattable formattable => SettingValue.FromString(formattable.ToString(null, CultureInfo.InvariantCulture)),
                _ => SettingValue.FromString(boxed.ToString())
            };
        }
    }
}
