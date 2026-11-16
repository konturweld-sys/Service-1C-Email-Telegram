using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using Meziantou.Framework.Win32;
using Microsoft.Extensions.Options;
using OneCIntegrationService.Models;

namespace OneCIntegrationService.Services;

public sealed class OneCComClient : IDisposable
{
    private readonly IOptionsMonitor<OneCConnectionSettings> _settingsMonitor;
    private readonly ILogger<OneCComClient> _logger;
    private readonly BlockingCollection<Action> _staQueue = new();
    private readonly Thread _staThread;
    private int _disposeStarted;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OneCComClient(
        IOptionsMonitor<OneCConnectionSettings> settingsMonitor,
        ILogger<OneCComClient> logger)
    {
        _settingsMonitor = settingsMonitor;
        _logger = logger;

        _staThread = new Thread(StaThreadLoop)
        {
            IsBackground = true,
            Name = "OneCComStaThread"
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
    }

    private void StaThreadLoop()
    {
        try
        {
            foreach (Action workItem in _staQueue.GetConsumingEnumerable())
                workItem();
        }
        finally
        {
            // Очередь освобождается самим STA-потоком только после выхода из цикла.
            // Dispose не уничтожает ее, пока COM-вызов еще выполняется.
            _staQueue.Dispose();
        }
    }

    private T RunOnStaThread<T>(Func<T> func)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _staQueue.Add(() =>
            {
                try
                {
                    completion.TrySetResult(func());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            throw new ObjectDisposedException(nameof(OneCComClient), $"Очередь STA уже закрывается: {ex.Message}");
        }

        return completion.Task.GetAwaiter().GetResult();
    }

    public List<LogMaskDto>? FetchActiveMasks()
    {
        try
        {
            string? responseJson = ExecuteComCall(module =>
                (string)module.ПолучитьАктивныеМаскиJSON());

            if (string.IsNullOrWhiteSpace(responseJson))
                return null;

            GetMasksResponse? response = JsonSerializer.Deserialize<GetMasksResponse>(responseJson, JsonOptions);
            if (response is null || !response.Success)
            {
                _logger.LogError("1С вернула ошибку при получении масок: {Error}",
                    response?.Error ?? "Неизвестная ошибка");
                return null;
            }

            // null означает некорректный/неполный ответ и сохраняет предыдущие маски.
            // Только success=true вместе с masks=[] очищает конфигурацию мониторинга.
            if (response.Masks is null)
            {
                _logger.LogError("1С вернула success=true, но поле masks отсутствует или равно null.");
                return null;
            }

            return response.Masks;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка COM-соединения при получении списка активных масок из 1С.");
            return null;
        }
    }

    public void SendEvent(RegisterEventRequest request)
    {
        try
        {
            string requestJson = JsonSerializer.Serialize(request, JsonOptions);
            string? responseJson = ExecuteComCall(module =>
                (string)module.ЗарегистрироватьСобытиеЛога(requestJson));

            if (string.IsNullOrWhiteSpace(responseJson))
            {
                _logger.LogWarning("Пустой ответ от 1С при регистрации события по маске {MaskId}.", request.MaskId);
                return;
            }

            RegisterEventResponse? response = JsonSerializer.Deserialize<RegisterEventResponse>(responseJson, JsonOptions);
            if (response is null || !response.Success)
            {
                _logger.LogWarning("1С отклонила событие (маска {MaskId}): {Error}",
                    request.MaskId, response?.Error ?? "Неизвестная ошибка");
            }
        }
        catch (Exception ex)
        {
            // По согласованному ТЗ локальной очереди нет: ошибка передачи
            // фиксируется в журнале службы, но не повторяется локально.
            _logger.LogError(ex,
                "Сбой передачи события в 1С через COM (маска {MaskId}). Событие не будет локально повторено.",
                request.MaskId);
        }
    }

    private T ExecuteComCall<T>(Func<dynamic, T> action)
    {
        return RunOnStaThread(() =>
        {
            OneCConnectionSettings settings = _settingsMonitor.CurrentValue;
            if (string.IsNullOrWhiteSpace(settings.ConnectionString))
                throw new InvalidOperationException("В appsettings.json не задана строка подключения OneCConnection:ConnectionString.");

            if (string.IsNullOrWhiteSpace(settings.CredentialTargetName))
                throw new InvalidOperationException("В appsettings.json не задано имя учетных данных OneCConnection:CredentialTargetName.");

            var credential = CredentialManager.ReadCredential(settings.CredentialTargetName)
                ?? throw new InvalidOperationException(
                    $"Учетные данные '{settings.CredentialTargetName}' не найдены в Диспетчере учетных данных Windows для учетной записи службы.");

            string connectionBase = settings.ConnectionString.TrimEnd();
            if (!connectionBase.EndsWith(';'))
                connectionBase += ";";

            string escapedUser = EscapeComParameter(credential.UserName ?? string.Empty);
            string escapedPassword = EscapeComParameter(credential.Password ?? string.Empty);
            string fullConnectionString =
                $"{connectionBase}Usr=\"{escapedUser}\";Pwd=\"{escapedPassword}\";";

            Type comType = Type.GetTypeFromProgID("V83.COMConnector")
                ?? throw new InvalidOperationException(
                    "Компонента V83.COMConnector (x64) не зарегистрирована в системе.");

            dynamic? connector = null;
            dynamic? connection = null;
            dynamic? comModule = null;

            try
            {
                connector = Activator.CreateInstance(comType);
                if (connector is null)
                    throw new InvalidOperationException("Не удалось создать объект V83.COMConnector.");

                connection = connector.Connect(fullConnectionString);
                if (connection is null)
                    throw new InvalidOperationException("V83.COMConnector вернул пустое COM-соединение.");

                comModule = connection.ЕРП_ИнтеграцияЛогиCOM;
                if (comModule is null)
                    throw new InvalidOperationException("Внешнее соединение 1С не предоставило модуль ЕРП_ИнтеграцияЛогиCOM.");

                return action(comModule);
            }
            finally
            {
                // RCW освобождаются строго в обратном порядке создания.
                ReleaseComSafe(comModule);
                ReleaseComSafe(connection);
                ReleaseComSafe(connector);
            }
        });
    }

    private static string EscapeComParameter(string value) => value.Replace("\"", "\"\"");

    private void ReleaseComSafe(object? comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject))
            return;

        try
        {
            Marshal.ReleaseComObject(comObject);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось освободить один из COM RCW при завершении вызова.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        _staQueue.CompleteAdding();
        if (!_staThread.Join(TimeSpan.FromSeconds(30)))
        {
            // Не вызываем Dispose очереди из другого потока: STA-поток может
            // все еще исполнять COM-вызов и читать очередь после его завершения.
            _logger.LogError(
                "STA-поток COM не завершился за 30 секунд. Очередь останется выделенной до завершения потока; принудительное освобождение запрещено.");
        }
    }
}
