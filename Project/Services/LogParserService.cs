using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OneCIntegrationService.Models;

namespace OneCIntegrationService.Services;

public sealed class LogParserService
{
    private const int ReverseReadBufferSize = 64 * 1024;
    private const int ReverseScanCheckIntervalBytes = 1024 * 1024;
    private const int RegexTimeoutSeconds = 2;
    private const int AbsoluteMaxLogEntryLength = 2000;
    private const long DefaultMaxTailBytes = 10L * 1024 * 1024;

    private readonly IOptionsMonitor<MonitorSettings> _settingsMonitor;
    private readonly ILogger<LogParserService> _logger;
    private readonly ConcurrentDictionary<string, string> _invalidMaskErrors =
        new(StringComparer.OrdinalIgnoreCase);

    public LogParserService(
        IOptionsMonitor<MonitorSettings> settingsMonitor,
        ILogger<LogParserService> logger)
    {
        _settingsMonitor = settingsMonitor;
        _logger = logger;
    }

    public List<RegisterEventRequest> ParseAndMatchFile(
        string filePath,
        IReadOnlyList<LogMaskDto> masksForFile,
        DateTime windowStart,
        DateTime windowEnd,
        Encoding encoding)
    {
        var results = new List<RegisterEventRequest>();
        if (masksForFile.Count == 0)
            return results;

        var settings = _settingsMonitor.CurrentValue;
        string[] dateFormats = settings.SupportedDateFormats?
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];

        if (dateFormats.Length == 0)
        {
            dateFormats =
            [
                "yyyy-MM-dd HH:mm:ss",
                "yyyy-MM-dd HH:mm:ss.fff",
                "yyyy-MM-dd'T'HH:mm:ss",
                "dd.MM.yyyy HH:mm:ss",
                "dd.MM.yyyy HH:mm:ss.fff",
                "yyyyMMddHHmmss"
            ];
        }

        var validMasks = new List<(LogMaskDto Mask, Regex TimeRegex)>();
        foreach (var mask in masksForFile)
        {
            if (TryGetCompiledTimeRegex(mask, out var timeRegex))
                validMasks.Add((mask, timeRegex!));
        }

        if (validMasks.Count == 0)
            return results;

        long maxTailBytes = settings.MaxTailBytes > 0
            ? Math.Min(settings.MaxTailBytes, DefaultMaxTailBytes)
            : DefaultMaxTailBytes;

        // Ошибки чтения намеренно передаются рабочему процессу, чтобы он мог
        // зарегистрировать FileHealthState и повторить чтение при следующем poll.
        List<string> relevantLines = ReadTailLinesReverse(
            filePath,
            encoding,
            maxTailBytes,
            windowStart,
            validMasks.Select(m => m.TimeRegex).ToArray(),
            dateFormats);

        if (relevantLines.Count == 0)
            return results;

        int configuredMaxLength = settings.MaxLogEntryLength > 0
            ? settings.MaxLogEntryLength
            : AbsoluteMaxLogEntryLength;
        int maxEntryLength = Math.Clamp(configuredMaxLength, 1, AbsoluteMaxLogEntryLength);

        foreach (var (mask, timeRegex) in validMasks)
        {
            var entries = GroupMultilineEntries(relevantLines, mask, timeRegex, dateFormats);
            var matchedEntries = new List<(DateTime Timestamp, string Text)>();

            foreach (var entry in entries)
            {
                if (entry.Timestamp < windowStart || entry.Timestamp > windowEnd)
                    continue;

                if (IsMatch(entry.Text, mask))
                    matchedEntries.Add(entry);
            }

            if (matchedEntries.Count == 0)
                continue;

            DateTime firstTimestamp = matchedEntries[0].Timestamp;
            string combinedRawEntry = string.Join("\n---\n", matchedEntries.Select(e => e.Text));
            combinedRawEntry = TruncateWithMarker(combinedRawEntry, maxEntryLength);

            string formattedText = FormatEventText(mask, firstTimestamp, combinedRawEntry);
            results.Add(new RegisterEventRequest
            {
                MaskId = mask.MaskId,
                EventTime = firstTimestamp.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                EventText = formattedText
            });
        }

        return results;
    }

    private static string TruncateWithMarker(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;

        if (maxLength <= 3)
            return value[..maxLength];

        return value[..(maxLength - 3)] + "...";
    }

    private static List<string> ReadTailLinesReverse(
        string filePath,
        Encoding encoding,
        long maxTailBytes,
        DateTime windowStart,
        IReadOnlyList<Regex> timeRegexes,
        string[] dateFormats)
    {
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        long fileLength = stream.Length;
        if (fileLength == 0)
            return [];

        long effectiveMaxBytes = maxTailBytes > 0
            ? Math.Min(maxTailBytes, DefaultMaxTailBytes)
            : DefaultMaxTailBytes;
        long minAllowedPosition = Math.Max(0, fileLength - effectiveMaxBytes);
        long currentPosition = fileLength;

        var collectedBlocks = new List<byte[]>();
        byte[] buffer = new byte[ReverseReadBufferSize];
        long bytesCollected = 0;
        long nextBoundaryCheck = ReverseScanCheckIntervalBytes;
        bool reachedOldRecords = false;

        while (currentPosition > minAllowedPosition && !reachedOldRecords)
        {
            int bytesToRead = (int)Math.Min(ReverseReadBufferSize, currentPosition - minAllowedPosition);
            currentPosition -= bytesToRead;
            stream.Seek(currentPosition, SeekOrigin.Begin);

            int bytesRead = 0;
            while (bytesRead < bytesToRead)
            {
                int read = stream.Read(buffer, bytesRead, bytesToRead - bytesRead);
                if (read == 0)
                    break;
                bytesRead += read;
            }

            if (bytesRead == 0)
                break;

            var chunk = new byte[bytesRead];
            Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);
            collectedBlocks.Add(chunk); // блоки добавляются от конца файла к началу
            bytesCollected += bytesRead;

            // Декодируем объединенные блоки, а не каждый блок отдельно:
            // так многобайтовые символы и метки времени на границе 64 КБ не теряются.
            if (bytesCollected >= nextBoundaryCheck || currentPosition == 0 || bytesCollected >= effectiveMaxBytes)
            {
                byte[] candidateBytes = CombineReverseBlocks(collectedBlocks);
                string candidateText = encoding.GetString(candidateBytes);
                if (candidateText.Length > 0 && candidateText[0] == '\uFEFF')
                    candidateText = candidateText[1..];
                bool hasPartialFirstLine = !IsAtLineBoundary(stream, currentPosition, encoding);
                reachedOldRecords = AllMasksHaveReachedWindowStart(
                    candidateText,
                    hasPartialFirstLine,
                    timeRegexes,
                    dateFormats,
                    windowStart);
                nextBoundaryCheck = bytesCollected + ReverseScanCheckIntervalBytes;
            }
        }

        if (collectedBlocks.Count == 0)
            return [];

        byte[] combinedBytes = CombineReverseBlocks(collectedBlocks);
        string fullTailText = encoding.GetString(combinedBytes);
        if (fullTailText.Length > 0 && fullTailText[0] == '\uFEFF')
            fullTailText = fullTailText[1..];

        bool finalFirstLineIsPartial = !IsAtLineBoundary(stream, currentPosition, encoding);
        var lines = new List<string>();
        using var reader = new StringReader(fullTailText);

        // Пропускаем только строку, начало которой действительно попало внутрь записи.
        if (finalFirstLineIsPartial)
            reader.ReadLine();

        string? textLine;
        while ((textLine = reader.ReadLine()) is not null)
            lines.Add(textLine);

        return lines;
    }


    private static bool IsAtLineBoundary(FileStream stream, long position, Encoding encoding)
    {
        if (position <= 0)
            return true;

        int codeUnitSize = encoding.CodePage switch
        {
            1200 or 1201 => 2, // UTF-16 LE / BE
            12000 or 12001 => 4, // UTF-32 LE / BE
            _ => 1
        };

        if (position < codeUnitSize)
            return true;

        byte[] previousUnit = new byte[codeUnitSize];
        stream.Seek(position - codeUnitSize, SeekOrigin.Begin);
        int bytesRead = 0;
        while (bytesRead < codeUnitSize)
        {
            int read = stream.Read(previousUnit, bytesRead, codeUnitSize - bytesRead);
            if (read == 0)
                return false;
            bytesRead += read;
        }

        string previousCharacter = encoding.GetString(previousUnit);
        return previousCharacter.EndsWith("\n", StringComparison.Ordinal) ||
               previousCharacter.EndsWith("\r", StringComparison.Ordinal);
    }

    private static byte[] CombineReverseBlocks(IReadOnlyList<byte[]> reverseBlocks)
    {
        int totalBytes = reverseBlocks.Sum(block => block.Length);
        var combined = new byte[totalBytes];
        int offset = 0;

        for (int i = reverseBlocks.Count - 1; i >= 0; i--)
        {
            byte[] block = reverseBlocks[i];
            Buffer.BlockCopy(block, 0, combined, offset, block.Length);
            offset += block.Length;
        }

        return combined;
    }

    private static bool AllMasksHaveReachedWindowStart(
        string candidateText,
        bool hasPartialFirstLine,
        IReadOnlyList<Regex> timeRegexes,
        string[] dateFormats,
        DateTime windowStart)
    {
        using var reader = new StringReader(candidateText);
        if (hasPartialFirstLine)
            reader.ReadLine();

        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
            lines.Add(line);

        if (lines.Count == 0)
            return false;

        foreach (Regex regex in timeRegexes)
        {
            DateTime? firstTimestamp = null;
            foreach (string candidateLine in lines)
            {
                try
                {
                    Match match = regex.Match(candidateLine);
                    if (match.Success && match.Index == 0 &&
                        TryParseTimestamp(match.Value, dateFormats, out DateTime parsed))
                    {
                        firstTimestamp = parsed;
                        break;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // В этом случае не прекращаем чтение по времени: финальная
                    // группировка зарегистрирует ошибку конфигурации маски.
                    break;
                }
            }

            if (!firstTimestamp.HasValue || firstTimestamp.Value >= windowStart)
                return false;
        }

        return true;
    }

    private bool TryGetCompiledTimeRegex(LogMaskDto mask, out Regex? regex)
    {
        regex = null;
        if (string.IsNullOrWhiteSpace(mask.TimeRegex))
        {
            LogMaskConfigErrorOnce(mask, "В маске не задано регулярное выражение времени (timeRegex). Маска пропущена.");
            return false;
        }

        try
        {
            regex = new Regex(
                mask.TimeRegex,
                RegexOptions.Compiled | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(RegexTimeoutSeconds));
            string currentKey = $"{mask.MaskId}|{mask.TimeRegex}";
            bool currentPatternTimedOut =
                _invalidMaskErrors.TryGetValue(currentKey, out string? currentError) &&
                currentError.StartsWith("Превышено время выполнения", StringComparison.Ordinal);
            if (!currentPatternTimedOut)
                ClearInvalidMaskErrors(mask.MaskId);
            return true;
        }
        catch (ArgumentException ex)
        {
            LogMaskConfigErrorOnce(mask, $"Некорректное регулярное выражение времени '{mask.TimeRegex}': {ex.Message}");
            return false;
        }
    }

    private void LogMaskConfigErrorOnce(LogMaskDto mask, string errorMessage)
    {
        string key = $"{mask.MaskId}|{mask.TimeRegex}";
        ClearInvalidMaskErrors(mask.MaskId, key);

        if (_invalidMaskErrors.TryAdd(key, errorMessage))
        {
            _logger.LogError(
                "Ошибка конфигурации маски '{MaskName}' ({MaskId}): {Error}",
                mask.MaskName,
                mask.MaskId,
                errorMessage);
        }
    }

    private void ClearInvalidMaskErrors(string maskId, string? keepKey = null)
    {
        string prefix = maskId + "|";
        foreach (string key in _invalidMaskErrors.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(key, keepKey, StringComparison.OrdinalIgnoreCase))
            {
                _invalidMaskErrors.TryRemove(key, out _);
            }
        }
    }

    private List<(DateTime Timestamp, string Text)> GroupMultilineEntries(
        List<string> lines,
        LogMaskDto mask,
        Regex timeRegex,
        string[] dateFormats)
    {
        var entries = new List<(DateTime Timestamp, string Text)>();
        DateTime? currentTimestamp = null;
        var currentEntryBuilder = new StringBuilder();

        foreach (string line in lines)
        {
            Match match;
            try
            {
                match = timeRegex.Match(line);
            }
            catch (RegexMatchTimeoutException ex)
            {
                LogMaskConfigErrorOnce(mask, $"Превышено время выполнения timeRegex: {ex.Message}");
                return [];
            }

            if (match.Success && match.Index == 0 &&
                TryParseTimestamp(match.Value, dateFormats, out DateTime parsedTime))
            {
                if (currentTimestamp.HasValue)
                {
                    entries.Add((currentTimestamp.Value, currentEntryBuilder.ToString()));
                    currentEntryBuilder.Clear();
                }

                currentTimestamp = parsedTime;
                currentEntryBuilder.Append(line);
                continue;
            }

            // Строки до первой распознанной даты игнорируются: достоверно
            // отнести их к скользящему временному окну невозможно.
            if (currentTimestamp.HasValue)
            {
                currentEntryBuilder.AppendLine();
                currentEntryBuilder.Append(line);
            }
        }

        if (currentTimestamp.HasValue && currentEntryBuilder.Length > 0)
            entries.Add((currentTimestamp.Value, currentEntryBuilder.ToString()));

        return entries;
    }

    private static bool TryParseTimestamp(string rawDate, string[] formats, out DateTime result)
    {
        return DateTime.TryParseExact(
            rawDate,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out result);
    }

    private bool IsMatch(string entryText, LogMaskDto mask)
    {
        if (string.IsNullOrWhiteSpace(mask.SearchPattern))
            return false;

        StringComparison comparison = mask.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        if (!string.IsNullOrWhiteSpace(mask.ExcludeWords))
        {
            string[] excludes = mask.ExcludeWords.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (string word in excludes)
            {
                if (entryText.Contains(word, comparison))
                    return false;
            }
        }

        if (mask.SearchType == "РегулярноеВыражение")
        {
            try
            {
                var options = RegexOptions.Singleline |
                    (mask.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                return Regex.IsMatch(
                    entryText,
                    mask.SearchPattern,
                    options,
                    TimeSpan.FromSeconds(RegexTimeoutSeconds));
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "Некорректное Regex-условие маски {MaskName}: {Pattern}",
                    mask.MaskName, mask.SearchPattern);
                return false;
            }
            catch (RegexMatchTimeoutException ex)
            {
                _logger.LogWarning(ex, "Превышено время выполнения Regex-условия маски {MaskName}: {Pattern}",
                    mask.MaskName, mask.SearchPattern);
                return false;
            }
        }

        string normalizedText = Regex.Replace(entryText, @"\s+", " ");
        string[] queryWords = Regex.Split(mask.SearchPattern.Trim(), @"\s+")
            .Where(word => !string.IsNullOrEmpty(word))
            .ToArray();

        if (queryWords.Length == 0)
            return false;

        switch (mask.SearchType)
        {
            case "ВсеСловаИ":
                return queryWords.All(word => normalizedText.Contains(word, comparison));

            case "ЛюбоеСловоИЛИ":
                return queryWords.Any(word => normalizedText.Contains(word, comparison));

            case "ПоследовательностьСлов":
            default:
                int currentIndex = 0;
                foreach (string word in queryWords)
                {
                    int foundIndex = normalizedText.IndexOf(word, currentIndex, comparison);
                    if (foundIndex < 0)
                        return false;
                    currentIndex = foundIndex + word.Length;
                }
                return true;
        }
    }

    private static string FormatEventText(LogMaskDto mask, DateTime eventTime, string logEntry)
    {
        string formattedTime = eventTime.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        string maskName = mask.MaskName ?? string.Empty;
        string messageTemplate = mask.MessageTemplate ?? string.Empty;
        if (string.IsNullOrWhiteSpace(messageTemplate))
            return $"[{formattedTime}] {maskName}:\n{logEntry}";

        return messageTemplate
            .Replace("{EventTime}", formattedTime, StringComparison.OrdinalIgnoreCase)
            .Replace("{MaskName}", maskName, StringComparison.OrdinalIgnoreCase)
            .Replace("{LogEntry}", logEntry, StringComparison.OrdinalIgnoreCase);
    }
}
