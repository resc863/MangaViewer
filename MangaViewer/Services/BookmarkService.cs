using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MangaViewer.Services
{
    /// <summary>
    /// BookmarkService
    /// Purpose: Persist per-folder bookmark list in a lightweight JSON file (bookmarks.json) residing in the opened
    /// manga folder. Each bookmark is stored as a relative path from the opened manga folder.
    /// Design Goals:
    ///  - Zero serialization reflection cost (AOT friendly) using Source Generator.
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
    /// JSON Shape: { "bookmarks": ["001.jpg", "sub\\002.png", ...] }
    /// </summary>
    public sealed class BookmarkService
    {
        private static readonly Lazy<BookmarkService> _instance = new(() => new BookmarkService());
        public static BookmarkService Instance => _instance.Value;

        private static readonly JsonSerializerOptions s_options = new()
        {
            WriteIndented = true
        };

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

                var data = JsonSerializer.Deserialize<BookmarkData>(json, s_options);
                if (data?.Bookmarks != null)
                {
                    foreach (var item in data.Bookmarks)
                    {
                        var fullPath = TryResolveToFullPath(item);
                        if (!string.IsNullOrWhiteSpace(fullPath))
                            _items.Add(fullPath);
                    }
                }
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
            var fullPath = TryNormalizeToFullPath(path);
            if (string.IsNullOrWhiteSpace(fullPath)) return false;
            if (_items.Add(fullPath)) { Save(); return true; }
            return false;
        }

        /// <summary>Remove a bookmark; persists immediately if existed.</summary>
        public bool Remove(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var fullPath = TryNormalizeToFullPath(path);
            if (string.IsNullOrWhiteSpace(fullPath)) return false;
            if (_items.Remove(fullPath)) { Save(); return true; }
            return false;
        }

        /// <summary>
        /// Persist current bookmarks to disk using Source Generator. Swallows IO exceptions.
        /// </summary>
        private void Save()
        {
            if (string.IsNullOrWhiteSpace(_currentFolderPath)) return;
            try
            {
                var filePath = Path.Combine(_currentFolderPath, FileName);
                var data = new BookmarkData
                {
                    Bookmarks = _items
                        .Select(TryGetRelativePath)
                        .Where(static p => !string.IsNullOrWhiteSpace(p))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()!
                };
                var json = JsonSerializer.Serialize(data, s_options);
                File.WriteAllText(filePath, json);
            }
            catch { }
        }

        private string? TryGetRelativePath(string? fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return null;
            if (string.IsNullOrWhiteSpace(_currentFolderPath)) return null;

            try
            {
                var normalizedFull = Path.GetFullPath(fullPath);
                var normalizedRoot = Path.GetFullPath(_currentFolderPath);

                var relative = Path.GetRelativePath(normalizedRoot, normalizedFull);
                if (relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    string.Equals(relative, "..", StringComparison.Ordinal))
                {
                    return null;
                }

                return relative;
            }
            catch
            {
                return null;
            }
        }

        private string? TryNormalizeToFullPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (string.IsNullOrWhiteSpace(_currentFolderPath)) return null;

            try
            {
                var candidate = path;
                if (!Path.IsPathRooted(candidate))
                    candidate = Path.Combine(_currentFolderPath, candidate);

                var full = Path.GetFullPath(candidate);
                var root = Path.GetFullPath(_currentFolderPath);

                if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return full;
            }
            catch
            {
                return null;
            }
        }

        private string? TryResolveToFullPath(string? storedPath)
        {
            if (string.IsNullOrWhiteSpace(storedPath)) return null;
            if (string.IsNullOrWhiteSpace(_currentFolderPath)) return null;

            // Back-compat: allow previously-saved absolute paths.
            if (Path.IsPathRooted(storedPath))
                return TryNormalizeToFullPath(storedPath);

            return TryNormalizeToFullPath(storedPath);
        }

        public class BookmarkData
        {
            [JsonPropertyName("bookmarks")]
            public List<string> Bookmarks { get; set; } = new();
        }
    }
}
