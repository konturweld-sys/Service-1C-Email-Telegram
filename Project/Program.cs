using System.Diagnostics;
using OneCIntegrationService.Models;
using OneCIntegrationService.Services;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "OneCIntegrationService";
});

try
{
    if (!EventLog.SourceExists("OneCIntegrationService"))
        EventLog.CreateEventSource("OneCIntegrationService", "Application");

    // Реальная запись проверяет доступ сервисной учетной записи к Application.
    // Ошибка доступа фиксируется в stderr и не препятствует запуску службы.
    EventLog.WriteEntry(
        "OneCIntegrationService",
        "Служба OneCIntegrationService запущена; проверка записи в журнал Application выполнена.",
        EventLogEntryType.Information);
}
catch (Exception ex)
{
    // Источник должен быть создан администратором install-service.ps1.
    // Отсутствие прав у сервисной учетной записи не должно останавливать службу.
    Console.Error.WriteLine($"Не удалось проверить запись в Windows Event Log: {ex.Message}");
}

builder.Services.Configure<OneCConnectionSettings>(
    builder.Configuration.GetSection("OneCConnection"));
builder.Services.Configure<MonitorSettings>(
    builder.Configuration.GetSection("MonitorSettings"));

builder.Services.AddSingleton<OneCComClient>();
builder.Services.AddSingleton<LogParserService>();
builder.Services.AddHostedService<LogMonitorWorker>();

var host = builder.Build();
host.Run();