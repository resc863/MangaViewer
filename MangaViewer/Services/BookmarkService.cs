using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MangaViewer.Services
{
    /// <summary>
    /// Per-folder bookmark persistence. Stores a JSON file in the opened folder.
    /// JSON shape: { "bookmarks": [ "fullPath1", "fullPath2", ... ] }
    /// AOT-friendly: manual JSON build/parse (avoids generic reflection-based serialization).
    /// </summary>
    public sealed class BookmarkService
    {
        private static readonly Lazy<BookmarkService> _instance = new(() => new BookmarkService());
        public static BookmarkService Instance => _instance.Value;

        private string? _currentFolderPath;
        private readonly HashSet<string> _items = new(StringComparer.OrdinalIgnoreCase);
        private const string FileName = "bookmarks.json";

        private BookmarkService() { }

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

        public IReadOnlyList<string> GetAll() => _items.ToList();
        public bool Contains(string path) => _items.Contains(path);
        public bool Add(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (_items.Add(path)) { Save(); return true; }
            return false;
        }
        public bool Remove(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (_items.Remove(path)) { Save(); return true; }
            return false;
        }

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

        // Manual JSON builder (minimal escaping for quotes and backslashes)
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
