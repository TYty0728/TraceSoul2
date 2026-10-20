using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace TraceSoul2.Host;

public sealed class WxxzDeviceControlWorker : BackgroundService
{
    private readonly SoulRuntime runtime;
    public WxxzDeviceControlWorker(SoulRuntime runtime){this.runtime=runtime;}
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while(!stoppingToken.IsCancellationRequested)
            {
                await runtime.WxxzDevice.TickAsync(stoppingToken);
                await Task.Delay(1000,stoppingToken);
            }
        }
        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){ }
    }
}
