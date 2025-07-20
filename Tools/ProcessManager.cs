using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace Tools;

public class ProcessManager : IHostedService, IDisposable
{
    private readonly ConcurrentDictionary<int, Process> _managedProcesses;
    private bool _disposed;

    public ProcessManager()
    {
        _managedProcesses = new ConcurrentDictionary<int, Process>();
        
        // Register shutdown handler for unexpected terminations
        AppDomain.CurrentDomain.ProcessExit += (s, e) => CleanupProcesses();
    }

    public Process StartManagedProcess(ProcessStartInfo startInfo)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ProcessManager));
        }

        var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start process");
        }

        _managedProcesses.TryAdd(process.Id, process);
        
        // Automatically remove from dictionary when process exits normally
        process.Exited += (sender, args) =>
        {
            if (sender is Process p)
            {
                _managedProcesses.TryRemove(p.Id, out _);
            }
        };
        
        process.EnableRaisingEvents = true;
        
        return process;
    }

    private void CleanupProcesses()
    {
        foreach (var process in _managedProcesses.Values)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // Best effort cleanup, ignore errors
            }
            finally
            {
                process.Dispose();
            }
        }
        
        _managedProcesses.Clear();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        CleanupProcesses();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            CleanupProcesses();
            _disposed = true;
        }
    }
}
