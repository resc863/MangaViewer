// Project: MangaViewer
// File: Services/LibraryService.cs
// Purpose: Manages manga library folders, saves/loads library paths, and scans for manga folders.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MangaViewer.Services
{
    public class LibraryService
    {
        private static readonly string s_folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MangaViewer");
        private static readonly string s_libraryFile = Path.Combine(s_folder, "library.json");
        private static readonly JsonSerializerOptions s_options = new() { WriteIndented = true };

        private List<string> _libraryPaths = new();
        private readonly SemaphoreSlim _lock = new(1, 1);

        public LibraryService()
        {
            // Synchronous load on construction for immediate availability
            _ = LoadLibraryPathsAsync().ConfigureAwait(false);
        }

        public List<string> GetLibraryPaths() => new(_libraryPaths);

        public async Task AddLibraryPathAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_libraryPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) return;
                _libraryPaths.Add(path);
            }
            finally { _lock.Release(); }
            
            await SaveLibraryPathsAsync().ConfigureAwait(false);
        }

        // Legacy sync method for compatibility
        public void AddLibraryPath(string path)
        {
            _ = AddLibraryPathAsync(path).ConfigureAwait(false);
        }

        public async Task RemoveLibraryPathAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                _libraryPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            }
            finally { _lock.Release(); }
            
            await SaveLibraryPathsAsync().ConfigureAwait(false);
        }

        // Legacy sync method for compatibility
        public void RemoveLibraryPath(string path)
        {
            _ = RemoveLibraryPathAsync(path).ConfigureAwait(false);
        }

        public async Task MoveLibraryPathAsync(int oldIndex, int newIndex)
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (oldIndex < 0 || oldIndex >= _libraryPaths.Count) return;
                if (newIndex < 0 || newIndex >= _libraryPaths.Count) return;
                if (oldIndex == newIndex) return;
                
                var item = _libraryPaths[oldIndex];
                _libraryPaths.RemoveAt(oldIndex);
                _libraryPaths.Insert(newIndex, item);
            }
            finally { _lock.Release(); }
            
            await SaveLibraryPathsAsync().ConfigureAwait(false);
        }

        // Legacy sync method for compatibility
        public void MoveLibraryPath(int oldIndex, int newIndex)
        {
            _ = MoveLibraryPathAsync(oldIndex, newIndex).ConfigureAwait(false);
        }

        public async Task<List<MangaFolderInfo>> ScanLibraryAsync()
        {
            var result = new List<MangaFolderInfo>();
            
            List<string> pathsCopy;
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                pathsCopy = new List<string>(_libraryPaths);
            }
            finally { _lock.Release(); }
            
            foreach (var libraryPath in pathsCopy)
            {
                if (!Directory.Exists(libraryPath)) continue;
                
                var folders = await Task.Run(() => GetMangaFolders(libraryPath)).ConfigureAwait(false);
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

        private async Task LoadLibraryPathsAsync()
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!File.Exists(s_libraryFile)) return;
                
                var json = await File.ReadAllTextAsync(s_libraryFile).ConfigureAwait(false);
                var data = JsonSerializer.Deserialize<LibraryData>(json, s_options);
                
                if (data?.Paths != null)
                {
                    _libraryPaths = data.Paths
                        .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                        .ToList();
                }
            }
            catch { }
            finally { _lock.Release(); }
        }

        private async Task SaveLibraryPathsAsync()
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(s_folder);
                
                var data = new LibraryData { Paths = new List<string>(_libraryPaths) };
                var json = JsonSerializer.Serialize(data, s_options);
                
                await File.WriteAllTextAsync(s_libraryFile, json).ConfigureAwait(false);
            }
            catch { }
            finally { _lock.Release(); }
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
