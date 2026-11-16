using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Options;
using OneCIntegrationService.Models;

namespace OneCIntegrationService.Services;

public sealed class LogMonitorWorker : BackgroundService
{
    private readonly OneCComClient _comClient;
    private readonly LogParserService _parserService;
    private readonly IOptionsMonitor<MonitorSettings> _monitorSettings;
    private readonly ILogger<LogMonitorWorker> _logger;

    private readonly object _syncLock = new();
    private Dictionary<string, List<LogMaskDto>> _masksByFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DirectoryWatcherState> _watchersByDir = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _dirtyFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileHealthState> _fileErrorStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, long Length)> _lastObservedFileInfo =
        new(StringComparer.OrdinalIgnoreCase);

    public LogMonitorWorker(
        OneCComClient comClient,
        LogParserService parserService,
        IOptionsMonitor<MonitorSettings> monitorSettings,
        ILogger<LogMonitorWorker> logger)
    {
        _comClient = comClient;
        _parserService = parserService;
        _monitorSettings = monitorSettings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Служба мониторинга логов 1С (OneCIntegrationService) запущена.");

        try
        {
            SyncMasksAndWatchers();
            DateTime lastMaskSyncUtc = DateTime.UtcNow;
            DateTime lastPollCheckUtc = DateTime.UtcNow;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    MonitorSettings settings = _monitorSettings.CurrentValue;
                    DateTime nowUtc = DateTime.UtcNow;

                    int maskSyncSeconds = Math.Max(15, settings.MaskSyncIntervalSeconds);
                    int pollSeconds = Math.Max(5, settings.FallbackPollIntervalSeconds);

                    if ((nowUtc - lastMaskSyncUtc).TotalSeconds >= maskSyncSeconds)
                    {
                        SyncMasksAndWatchers();
                        lastMaskSyncUtc = nowUtc;
                    }

                    // События watcher и периодический опрос сводятся в один набор:
                    // каждый путь обрабатывается не более одного раза за итерацию.
                    var filesToProcessThisCycle = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string changedFile in _dirtyFiles.Keys)
                    {
                        if (_dirtyFiles.TryRemove(changedFile, out _))
                            filesToProcessThisCycle.Add(changedFile);
                    }

                    if ((nowUtc - lastPollCheckUtc).TotalSeconds >= pollSeconds)
                    {
                        lock (_syncLock)
                        {
                            EnsureWatchersForExistingDirectories();
                            foreach (string filePath in _masksByFile.Keys)
                            {
                                if (HasFileChangedOrNeedsRetry(filePath))
                                    filesToProcessThisCycle.Add(filePath);
                            }
                        }

                        lastPollCheckUtc = nowUtc;
                    }

                    foreach (string filePath in filesToProcessThisCycle)
                        ProcessFileSafe(filePath);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Необработанная ошибка в рабочем цикле службы.");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            lock (_syncLock)
                DisposeAllWatchers();
            _logger.LogInformation("Служба мониторинга логов 1С остановлена.");
        }
    }

    private bool HasFileChangedOrNeedsRetry(string filePath)
    {
        // Любая зафиксированная ошибка повторно проверяется на каждом poll.
        // Повторные одинаковые ошибки не дублируются в журнале.
        if (_fileErrorStates.ContainsKey(filePath))
            return true;

        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
                return true;

            var currentMeta = (info.LastWriteTimeUtc, info.Length);
            return !_lastObservedFileInfo.TryGetValue(filePath, out var previousMeta) ||
                   previousMeta != currentMeta;
        }
        catch (Exception ex)
        {
            ReportFileErrorOnce(
                filePath,
                $"MetadataError:{ex.GetType().FullName}",
                $"Не удалось получить метаданные файла лога {filePath}: {ex.Message}");
            return true;
        }
    }

    private void SyncMasksAndWatchers()
    {
        List<LogMaskDto>? masks = _comClient.FetchActiveMasks();
        if (masks is null)
        {
            // Ошибка COM не должна очищать ранее загруженные маски.
            _logger.LogWarning("Список масок не обновлен: 1С не вернула успешный ответ. Используется предыдущая конфигурация.");
            return;
        }

        var grouped = new Dictionary<string, List<LogMaskDto>>(StringComparer.OrdinalIgnoreCase);
        foreach (LogMaskDto mask in masks)
        {
            if (mask is null || string.IsNullOrWhiteSpace(mask.FilePath))
                continue;

            string rawPath = mask.FilePath.Trim();
            if (rawPath.IndexOfAny(['*', '?']) >= 0 || !Path.IsPathFullyQualified(rawPath))
            {
                _logger.LogWarning("Маска {MaskId} пропущена: путь должен быть абсолютным и указывать на конкретный файл без wildcard: {Path}",
                    mask.MaskId, rawPath);
                continue;
            }

            try
            {
                string fullPath = Path.GetFullPath(rawPath);
                if (!grouped.TryGetValue(fullPath, out List<LogMaskDto>? list))
                {
                    list = [];
                    grouped.Add(fullPath, list);
                }
                list.Add(mask);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Маска {MaskId} пропущена: некорректный путь к файлу {Path}",
                    mask.MaskId, rawPath);
            }
        }

        lock (_syncLock)
        {
            _masksByFile = grouped;
            ReconcileWatchers();

            var activePaths = new HashSet<string>(grouped.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (string path in _lastObservedFileInfo.Keys)
            {
                if (!activePaths.Contains(path))
                    _lastObservedFileInfo.TryRemove(path, out _);
            }
            foreach (string path in _fileErrorStates.Keys)
            {
                if (!activePaths.Contains(path))
                    _fileErrorStates.TryRemove(path, out _);
            }
            foreach (string path in _dirtyFiles.Keys)
            {
                if (!activePaths.Contains(path))
                    _dirtyFiles.TryRemove(path, out _);
            }

            if (grouped.Count == 0)
            {
                _lastObservedFileInfo.Clear();
                _fileErrorStates.Clear();
                _dirtyFiles.Clear();
            }
        }

        _logger.LogInformation(
            "Синхронизация масок завершена. Активных масок: {MaskCount}, уникальных файлов: {FileCount}.",
            masks.Count,
            grouped.Count);
    }

    private void ReconcileWatchers()
    {
        var filesByDirectory = GetFilesByDirectory();
        var requiredDirectories = new HashSet<string>(filesByDirectory.Keys, StringComparer.OrdinalIgnoreCase);

        foreach (string existingDirectory in _watchersByDir.Keys.ToList())
        {
            if (!requiredDirectories.Contains(existingDirectory))
            {
                DirectoryWatcherState state = _watchersByDir[existingDirectory];
                _watchersByDir.Remove(existingDirectory);
                DisposeWatcher(state.Watcher);
            }
        }

        foreach (var pair in filesByDirectory)
        {
            if (_watchersByDir.TryGetValue(pair.Key, out DirectoryWatcherState? existing))
            {
                if (Directory.Exists(pair.Key))
                {
                    // Меняем ссылку целиком: обработчики событий читают неизменяемый HashSet.
                    existing.AllowedFileNames = pair.Value;
                    continue;
                }

                _watchersByDir.Remove(pair.Key);
                DisposeWatcher(existing.Watcher);
            }

            if (Directory.Exists(pair.Key))
                CreateWatcherForDirectory(pair.Key, pair.Value);
            else
                _logger.LogWarning("Каталог отслеживаемого лог-файла пока не существует: {Directory}", pair.Key);
        }
    }

    private Dictionary<string, HashSet<string>> GetFilesByDirectory()
    {
        return _masksByFile.Keys
            .Select(path => new { Directory = Path.GetDirectoryName(path), FileName = Path.GetFileName(path) })
            .Where(item => !string.IsNullOrWhiteSpace(item.Directory) && !string.IsNullOrWhiteSpace(item.FileName))
            .GroupBy(item => item.Directory!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new HashSet<string>(group.Select(item => item.FileName!), StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
    }

    private void EnsureWatchersForExistingDirectories()
    {
        foreach (var pair in GetFilesByDirectory())
        {
            if (_watchersByDir.TryGetValue(pair.Key, out DirectoryWatcherState? existing))
            {
                if (!Directory.Exists(pair.Key))
                {
                    _watchersByDir.Remove(pair.Key);
                    DisposeWatcher(existing.Watcher);
                }
                else
                {
                    existing.AllowedFileNames = pair.Value;
                    continue;
                }
            }

            if (Directory.Exists(pair.Key))
                CreateWatcherForDirectory(pair.Key, pair.Value);
        }
    }

    private void CreateWatcherForDirectory(string directory, HashSet<string> allowedFileNames)
    {
        try
        {
            var state = new DirectoryWatcherState(allowedFileNames);
            var watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size |
                               NotifyFilters.CreationTime | NotifyFilters.FileName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = false
            };

            void OnFileChangedOrCreated(object sender, FileSystemEventArgs e)
            {
                if (!string.IsNullOrEmpty(e.Name) && state.AllowedFileNames.Contains(e.Name))
                    _dirtyFiles[e.FullPath] = 1;
            }

            watcher.Changed += OnFileChangedOrCreated;
            watcher.Created += OnFileChangedOrCreated;

            watcher.Deleted += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Name) && state.AllowedFileNames.Contains(e.Name))
                {
                    _lastObservedFileInfo.TryRemove(e.FullPath, out _);
                    ReportFileErrorOnce(e.FullPath, "Missing", $"Отслеживаемый файл лога удален: {e.FullPath}");
                }
            };

            watcher.Renamed += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.OldName) && state.AllowedFileNames.Contains(e.OldName))
                {
                    _lastObservedFileInfo.TryRemove(e.OldFullPath, out _);
                    ReportFileErrorOnce(e.OldFullPath, "Missing", $"Отслеживаемый файл лога переименован: {e.OldFullPath}");
                }

                if (!string.IsNullOrEmpty(e.Name) && state.AllowedFileNames.Contains(e.Name))
                    _dirtyFiles[e.FullPath] = 1;
            };

            watcher.Error += (sender, e) =>
            {
                _logger.LogWarning(e.GetException(),
                    "Ошибка FileSystemWatcher для каталога {Directory}; наблюдатель будет пересоздан.", directory);

                lock (_syncLock)
                {
                    if (_watchersByDir.TryGetValue(directory, out DirectoryWatcherState? current) &&
                        ReferenceEquals(current.Watcher, watcher))
                    {
                        _watchersByDir.Remove(directory);
                        DisposeWatcher(watcher);
                        if (Directory.Exists(directory))
                            CreateWatcherForDirectory(directory, GetFilesByDirectory().GetValueOrDefault(directory)
                                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    }
                }
            };

            state.Watcher = watcher;
            _watchersByDir[directory] = state;
            try
            {
                watcher.EnableRaisingEvents = true;
            }
            catch
            {
                _watchersByDir.Remove(directory);
                DisposeWatcher(watcher);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось создать FileSystemWatcher для каталога {Directory}.", directory);
        }
    }

    private void ProcessFileSafe(string filePath)
    {
        List<LogMaskDto> masks;
        lock (_syncLock)
        {
            if (!_masksByFile.TryGetValue(filePath, out List<LogMaskDto>? configuredMasks) || configuredMasks.Count == 0)
                return;
            masks = configuredMasks.ToList();
        }

        try
        {
            var fileInfo = new FileInfo(filePath);
            fileInfo.Refresh();
            if (!fileInfo.Exists)
            {
                _lastObservedFileInfo.TryRemove(filePath, out _);
                ReportFileErrorOnce(filePath, "Missing", $"Отслеживаемый файл лога не найден: {filePath}");
                return;
            }

            var beforeRead = (fileInfo.LastWriteTimeUtc, fileInfo.Length);
            MonitorSettings settings = _monitorSettings.CurrentValue;
            DateTime windowEnd = DateTime.Now;
            long lookbackSeconds = Math.Max(1L, Math.Abs((long)settings.LookbackWindowSeconds));
            DateTime windowStart = windowEnd.AddSeconds(-lookbackSeconds);

            Encoding encoding;
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                encoding = Encoding.GetEncoding(settings.LogFileEncoding);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось использовать кодировку {Encoding}; применяется UTF-8.",
                    settings.LogFileEncoding);
                encoding = Encoding.UTF8;
            }

            List<RegisterEventRequest> events = _parserService.ParseAndMatchFile(
                filePath, masks, windowStart, windowEnd, encoding);

            fileInfo.Refresh();
            if (!fileInfo.Exists)
            {
                _lastObservedFileInfo.TryRemove(filePath, out _);
                ReportFileErrorOnce(filePath, "Missing", $"Файл лога исчез во время чтения: {filePath}");
                return;
            }

            var afterRead = (fileInfo.LastWriteTimeUtc, fileInfo.Length);
            _lastObservedFileInfo[filePath] = afterRead;
            if (beforeRead != afterRead)
            {
                // Файл изменился во время чтения. Повторно обработаем его в следующем цикле.
                _dirtyFiles[filePath] = 1;
            }

            if (_fileErrorStates.TryRemove(filePath, out _))
                _logger.LogInformation("Доступ к файлу лога восстановлен: {FilePath}", filePath);

            foreach (RegisterEventRequest logEvent in events)
                _comClient.SendEvent(logEvent);
        }
        catch (Exception ex)
        {
            ReportFileErrorOnce(
                filePath,
                $"ReadError:{ex.GetType().FullName}",
                $"Ошибка чтения файла лога {filePath}: {ex.Message}");
        }
    }

    private void ReportFileErrorOnce(string filePath, string errorKey, string message)
    {
        var newState = new FileHealthState(errorKey, message, DateTime.UtcNow);
        if (!_fileErrorStates.TryGetValue(filePath, out FileHealthState? existing) ||
            !string.Equals(existing.ErrorKey, errorKey, StringComparison.Ordinal))
        {
            _fileErrorStates[filePath] = newState;
            _logger.LogWarning("{Message}", message);
        }
    }

    private void DisposeWatcher(FileSystemWatcher watcher)
    {
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ошибка при освобождении FileSystemWatcher.");
        }
    }

    private void DisposeAllWatchers()
    {
        foreach (DirectoryWatcherState state in _watchersByDir.Values)
            DisposeWatcher(state.Watcher);
        _watchersByDir.Clear();
    }

    private sealed class DirectoryWatcherState(HashSet<string> allowedFileNames)
    {
        public HashSet<string> AllowedFileNames { get; set; } = allowedFileNames;
        public FileSystemWatcher Watcher { get; set; } = null!;
    }

    private sealed record FileHealthState(string ErrorKey, string Message, DateTime FirstObservedUtc);
}
