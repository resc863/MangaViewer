// Project: MangaViewer
// File: Services/LibraryService.cs
// Purpose: Manages manga library folders, saves/loads library paths, and scans for manga folders.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace MangaViewer.Services
{
    public class LibraryService
    {
        private static readonly string s_folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MangaViewer");
        private static readonly string s_libraryFile = Path.Combine(s_folder, "library.json");

        private List<string> _libraryPaths = new();

        public LibraryService()
        {
            LoadLibraryPaths();
        }

        public List<string> GetLibraryPaths() => new(_libraryPaths);

        public void AddLibraryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            if (_libraryPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) return;
            _libraryPaths.Add(path);
            SaveLibraryPaths();
        }

        public void RemoveLibraryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            _libraryPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            SaveLibraryPaths();
        }

        public void MoveLibraryPath(int oldIndex, int newIndex)
        {
            if (oldIndex < 0 || oldIndex >= _libraryPaths.Count) return;
            if (newIndex < 0 || newIndex >= _libraryPaths.Count) return;
            if (oldIndex == newIndex) return;
            
            var item = _libraryPaths[oldIndex];
            _libraryPaths.RemoveAt(oldIndex);
            _libraryPaths.Insert(newIndex, item);
            SaveLibraryPaths();
        }

        public async Task<List<MangaFolderInfo>> ScanLibraryAsync()
        {
            var result = new List<MangaFolderInfo>();
            
            foreach (var libraryPath in _libraryPaths)
            {
                if (!Directory.Exists(libraryPath)) continue;
                
                var folders = await Task.Run(() => GetMangaFolders(libraryPath));
                result.AddRange(folders);
            }
            
            return result;
        }

        private List<MangaFolderInfo> GetMangaFolders(string libraryPath)
        {
            var result = new List<MangaFolderInfo>();
            
            try
            {
                var directories = Directory.GetDirectories(libraryPath);
                
                foreach (var dir in directories)
                {
                    var firstImage = GetFirstImageInFolder(dir);
                    if (firstImage != null)
                    {
                        result.Add(new MangaFolderInfo
                        {
                            FolderPath = dir,
                            FolderName = Path.GetFileName(dir),
                            FirstImagePath = firstImage
                        });
                    }
                }
                
                result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FolderName, b.FolderName));
            }
            catch { }
            
            return result;
        }

        private string? GetFirstImageInFolder(string folderPath)
        {
            try
            {
                var imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".avif", ".gif" };
                
                var files = Directory.GetFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly);
                
                var imageFiles = files
                    .Where(f => imageExtensions.Contains(Path.GetExtension(f)))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();
                
                return imageFiles.FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private void LoadLibraryPaths()
        {
            try
            {
                if (!File.Exists(s_libraryFile)) return;
                
                var json = File.ReadAllText(s_libraryFile);
                var data = JsonSerializer.Deserialize<LibraryData>(json);
                
                if (data?.Paths != null)
                {
                    _libraryPaths = data.Paths.Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)).ToList();
                }
            }
            catch { }
        }

        private void SaveLibraryPaths()
        {
            try
            {
                Directory.CreateDirectory(s_folder);
                
                var data = new LibraryData { Paths = _libraryPaths };
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                
                File.WriteAllText(s_libraryFile, json);
            }
            catch { }
        }

        private class LibraryData
        {
            public List<string> Paths { get; set; } = new();
        }
    }

    public class MangaFolderInfo
    {
        public string FolderPath { get; set; } = string.Empty;
        public string FolderName { get; set; } = string.Empty;
        public string? FirstImagePath { get; set; }
    }
}
