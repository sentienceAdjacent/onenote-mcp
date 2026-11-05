using Microsoft.Extensions.Logging;
using OneNoteMcp.Core.Settings;

namespace OneNoteMcp.Server.Logging;

/// <summary>
/// The startup lines. The settings line carries the status, the path and the field-naming reason, never the
/// settings values.
/// </summary>
public static partial class StartupLog
{
    public static void Started(ILogger logger, string version, int processId, SettingsSnapshot settings, FileLogStart fileLog)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(fileLog);

        ServerStarted(logger, version, processId, settings.Status.ToString(), settings.Path, settings.Reason ?? "-", fileLog.FilePath ?? "-");
        if (fileLog.FailureReason is { } failure)
        {
            FileLogDisabled(logger, fileLog.Directory, failure);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "server started version={Version} pid={ProcessId} settingsStatus={SettingsStatus} settingsPath={SettingsPath} settingsReason={SettingsReason} logFile={LogFile}")]
    private static partial void ServerStarted(ILogger logger, string version, int processId, string settingsStatus, string settingsPath, string settingsReason, string logFile);

    [LoggerMessage(Level = LogLevel.Warning, Message = "file log disabled, logging to stderr only logDir={LogDir} reason={Reason}")]
    private static partial void FileLogDisabled(ILogger logger, string logDir, string reason);
}
