using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MangaViewer.Services
{
    /// <summary>
    /// Per-folder bookmark persistence. Stores a JSON file in the opened folder.
    /// JSON shape: { "bookmarks": [ "fullPath1", "fullPath2", ... ] }
    /// </summary>
    public sealed class BookmarkService
    {
        private static readonly Lazy<BookmarkService> _instance = new(() => new BookmarkService());
        public static BookmarkService Instance => _instance.Value;

        private string? _currentFolderPath;
        private readonly HashSet<string> _items = new(StringComparer.OrdinalIgnoreCase);

        private const string FileName = "bookmarks.json";

        private BookmarkService()
        {
        }

        public void LoadForFolder(string? folderPath)
        {
            _items.Clear();
            _currentFolderPath = null;

            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            _currentFolderPath = folderPath;

            try
            {
                var filePath = Path.Combine(folderPath, FileName);
                if (!File.Exists(filePath))
                {
                    // Create an empty file the first time a folder is opened.
                    Save();
                    return;
                }

                var json = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                var doc = JsonSerializer.Deserialize<BookmarkFile>(json);
                if (doc?.Bookmarks != null)
                {
                    foreach (var p in doc.Bookmarks)
                    {
                        if (!string.IsNullOrWhiteSpace(p))
                        {
                            _items.Add(p);
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        public IReadOnlyList<string> GetAll() => _items.ToList();

        public bool Contains(string path) => _items.Contains(path);

        public bool Add(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (_items.Add(path))
            {
                Save();
                return true;
            }

            return false;
        }

        public bool Remove(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (_items.Remove(path))
            {
                Save();
                return true;
            }

            return false;
        }

        private void Save()
        {
            if (string.IsNullOrWhiteSpace(_currentFolderPath))
            {
                return;
            }

            try
            {
                var filePath = Path.Combine(_currentFolderPath, FileName);
                var model = new BookmarkFile { Bookmarks = _items.ToList() };
                var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
            }
            catch
            {
                // ignore
            }
        }

        private sealed class BookmarkFile
        {
            public List<string> Bookmarks { get; set; } = new();
        }
    }
}
