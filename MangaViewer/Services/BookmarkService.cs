using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MangaViewer.Services
{
    /// <summary>
    /// BookmarkService
    /// Purpose: Persist per-folder bookmark list in a lightweight JSON file (bookmarks.json) residing in the opened
    /// manga folder. Each bookmark is the full file path of an image.
    /// Design Goals:
    ///  - Zero serialization reflection cost (AOT friendly) by building JSON manually.
    ///  - Robust against malformed JSON (silently ignores parse failures).
    ///  - Case-insensitive path handling to avoid duplicates across different casing.
    ///  - Simple CRUD semantics with immediate persistence on mutation (Add / Remove).
    /// Threading: Intended to be called from UI thread; minimal locking (HashSet not thread-safe). If future
    /// background access is needed, wrap mutation in a lock.
    /// Error Handling: All IO exceptions swallowed; failures simply result in no-op (user experience: bookmarks may not update).
    /// Extension Ideas:
    ///  - Add incremental save debounce (batch writes) if folders contain very large bookmark churn.
    ///  - Store relative paths to improve portability when folder root moves.
    ///  - Add versioning or additional metadata (e.g., note, timestamp) to JSON shape.
    /// JSON Shape: { "bookmarks": ["C:\\Full\\Path1.jpg", "C:\\Full\\Path2.png", ...] }
    /// </summary>
    public sealed class BookmarkService
    {
        private static readonly Lazy<BookmarkService> _instance = new(() => new BookmarkService());
        public static BookmarkService Instance => _instance.Value;

        private string? _currentFolderPath;
        private readonly HashSet<string> _items = new(StringComparer.OrdinalIgnoreCase);
        private const string FileName = "bookmarks.json";

        private BookmarkService() { }

        /// <summary>
        /// Load bookmarks for the target folder. If the file does not exist a new one is created.
        /// </summary>
        public void LoadForFolder(string? folderPath)
        {
            _items.Clear();
            _currentFolderPath = null;
            if (string.IsNullOrWhiteSpace(folderPath)) return;
            _currentFolderPath = folderPath;
            try
            {
                var filePath = Path.Combine(folderPath, FileName);
                if (!File.Exists(filePath)) { Save(); return; }
                var json = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(json)) return;
                ParseJson(json);
            }
            catch { }
        }

        /// <summary>Returns all bookmark paths (copy of internal set).</summary>
        public IReadOnlyList<string> GetAll() => _items.ToList();
        /// <summary>Check if path already bookmarked.</summary>
        public bool Contains(string path) => _items.Contains(path);
        /// <summary>Add a bookmark; persists immediately if new.</summary>
        public bool Add(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (_items.Add(path)) { Save(); return true; }
            return false;
        }
        /// <summary>Remove a bookmark; persists immediately if existed.</summary>
        public bool Remove(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (_items.Remove(path)) { Save(); return true; }
            return false;
        }

        /// <summary>
        /// Persist current bookmarks to disk. Swallows IO exceptions.
        /// </summary>
        private void Save()
        {
            if (string.IsNullOrWhiteSpace(_currentFolderPath)) return;
            try
            {
                var filePath = Path.Combine(_currentFolderPath, FileName);
                var json = BuildJson(_items);
                File.WriteAllText(filePath, json);
            }
            catch { }
        }

        /// <summary>
        /// Manual JSON builder for array of strings; escapes quotes and backslashes only.
        /// </summary>
        private static string BuildJson(IEnumerable<string> items)
        {
            var sb = new StringBuilder();
            sb.Append("{\"bookmarks\": [");
            bool first = true;
            foreach (var it in items)
            {
                if (string.IsNullOrWhiteSpace(it)) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"');
                foreach (char c in it)
                {
                    if (c == '\\') sb.Append("\\\\");
                    else if (c == '"') sb.Append("\\\"");
                    else sb.Append(c);
                }
                sb.Append('"');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// Parse bookmarks JSON into internal set. Malformed JSON ignored.
        /// </summary>
        private void ParseJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("bookmarks", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.String)
                        {
                            var v = el.GetString();
                            if (!string.IsNullOrWhiteSpace(v)) _items.Add(v);
                        }
                    }
                }
            }
            catch { }
        }
    }
}
