using System.Diagnostics;
using MarketNewsAdmin.Models;

namespace MarketNewsAdmin.Services;

public sealed class PipelineRunnerService(IWebHostEnvironment environment, ILogger<PipelineRunnerService> logger)
{
    private const int MaximumConsoleLines = 1000;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly object _consoleLock = new();
    private readonly List<string> _consoleLines = [];
    private bool _isRunning;
    private int? _exitCode;

    public PipelineConsoleViewModel GetConsole()
    {
        lock (_consoleLock)
            return new PipelineConsoleViewModel(_isRunning, _exitCode, _consoleLines.ToList());
    }

    public bool TryStart()
    {
        if (!_runLock.Wait(0))
            return false;

        try
        {
            var startInfo = CreateStartInfo();
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The report pipeline process could not be started.");

            lock (_consoleLock)
            {
                _consoleLines.Clear();
                _isRunning = true;
                _exitCode = null;
            }

            process.OutputDataReceived += (_, eventArgs) => AppendConsoleLine(eventArgs.Data);
            process.ErrorDataReceived += (_, eventArgs) => AppendConsoleLine(eventArgs.Data, isError: true);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                logger.LogInformation("Manual report pipeline exited with code {ExitCode}.", process.ExitCode);
                lock (_consoleLock)
                {
                    _isRunning = false;
                    _exitCode = process.ExitCode;
                }
                process.Dispose();
                _runLock.Release();
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            logger.LogInformation("Manual report pipeline started with process ID {ProcessId}.", process.Id);
            return true;
        }
        catch
        {
            _runLock.Release();
            throw;
        }
    }

    private void AppendConsoleLine(string? line, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        lock (_consoleLock)
        {
            _consoleLines.Add(isError ? $"[stderr] {line}" : line);
            if (_consoleLines.Count > MaximumConsoleLines)
                _consoleLines.RemoveRange(0, _consoleLines.Count - MaximumConsoleLines);
        }
    }

    private ProcessStartInfo CreateStartInfo()
    {
        var publishedPipeline = Path.Combine(environment.ContentRootPath, "pipeline", "MarketNewsApp.dll");
        if (File.Exists(publishedPipeline))
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(publishedPipeline)!,
            };
            startInfo.ArgumentList.Add(publishedPipeline);
            startInfo.ArgumentList.Add("--now");
            return startInfo;
        }

        var projectPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "MarketNewsApp", "MarketNewsApp.csproj"));
        if (!File.Exists(projectPath))
            throw new FileNotFoundException("MarketNewsApp was not found for the manual report run.", projectPath);

        var developmentStartInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
        };
        developmentStartInfo.ArgumentList.Add("run");
        developmentStartInfo.ArgumentList.Add("--project");
        developmentStartInfo.ArgumentList.Add(projectPath);
        developmentStartInfo.ArgumentList.Add("--");
        developmentStartInfo.ArgumentList.Add("--now");
        return developmentStartInfo;
    }
}