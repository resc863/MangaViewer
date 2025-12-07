// Project: MangaViewer
// File: Services/LibraryService.cs
// Purpose: Manages manga library folders, saves/loads library paths, and scans for manga folders.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MangaViewer.Services
{
    public class LibraryService
    {
        private static readonly string s_folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MangaViewer");
        private static readonly string s_libraryFile = Path.Combine(s_folder, "library.json");
        private static readonly JsonSerializerOptions s_options = new() 
        { 
            WriteIndented = true
        };

        private static readonly HashSet<string> s_imageExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".avif", ".gif" };

        private List<string> _libraryPaths = new();
        private readonly SemaphoreSlim _lock = new(1, 1);

        public LibraryService()
        {
            _ = LoadLibraryPathsAsync();
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

        public async Task<List<MangaFolderInfo>> ScanLibraryAsync(CancellationToken cancellationToken = default)
        {
            var result = new List<MangaFolderInfo>();
            
            List<string> pathsCopy;
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                pathsCopy = new List<string>(_libraryPaths);
            }
            finally { _lock.Release(); }
            
            foreach (var libraryPath in pathsCopy)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (!Directory.Exists(libraryPath)) continue;
                
                var folders = await Task.Run(() => GetMangaFolders(libraryPath, cancellationToken), cancellationToken).ConfigureAwait(false);
                result.AddRange(folders);
            }
            
            return result;
        }

        private List<MangaFolderInfo> GetMangaFolders(string libraryPath, CancellationToken cancellationToken)
        {
            var result = new List<MangaFolderInfo>();
            
            try
            {
                var directories = Directory.EnumerateDirectories(libraryPath);
                
                foreach (var dir in directories)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    
                    var firstImage = GetFirstImageInFolder(dir);
                    if (firstImage != null)
                    {
                        result.Add(new MangaFolderInfo
                        {
                            FolderPath = dir,
                            FolderName = Path.GetFileName(dir) ?? string.Empty,
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
                return Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => s_imageExtensions.Contains(Path.GetExtension(f)))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
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

        public class LibraryData
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
