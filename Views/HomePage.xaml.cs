﻿using System.Diagnostics;
using System.Management;
using System.ServiceProcess;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Networking.Connectivity;

namespace RyTuneX.Views;

public sealed partial class HomePage : Page
{
    private readonly string _versionDescription;
    private readonly PerformanceCounter cpuCounter;
    private readonly PerformanceCounter diskCounter;
    private readonly CancellationTokenSource _cancellationTokenSource;

    public HomePage()
    {
        InitializeComponent();
        LogHelper.Log("Initializing HomePage");
        _versionDescription = "Version " + SettingsPage.GetVersionDescription();
        // Initialize performance counters
        cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        diskCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total");

        _cancellationTokenSource = new CancellationTokenSource();
        _ = UpdateSystemStatsAsync(_cancellationTokenSource.Token);
        Unloaded += HomePage_Unloaded;
    }

    private async Task UpdateSystemStatsAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Fetch static values once at the beginning
            var installedAppsCount = await Task.Run(() => GetInstalledAppsCount()).ConfigureAwait(false);
            var servicesCount = await Task.Run(() => GetServicesCount()).ConfigureAwait(false);
            var processesCount = await Task.Run(() => GetProcessesCount()).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                // Fetch real-time usage values concurrently
                var cpuUsageTask = Task.Run(() => GetCpuUsage());
                var ramUsageTask = Task.Run(() => GetRamUsage());
                var diskUsageTask = Task.Run(() => GetDiskUsage());
                var networkUsageTask = Task.Run(() => GetNetworkUsage());
                var gpuUsageTask = Task.Run(() => GetGpuUsage());

                await Task.WhenAll(cpuUsageTask, ramUsageTask, diskUsageTask, networkUsageTask, gpuUsageTask).ConfigureAwait(false);

                var cpuUsage = cpuUsageTask.Result;
                var ramUsage = ramUsageTask.Result;
                var diskUsage = diskUsageTask.Result;
                var networkUsage = networkUsageTask.Result;
                var gpuUsage = gpuUsageTask.Result;

                try
                {
                    // Update the UI on the main thread
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (this.Visibility == Visibility.Visible)
                        {
                            cpuUsageText.Text = $"{cpuUsage}%";
                            ramUsageText.Text = $"{ramUsage}%";
                            diskUsageText.Text = $"{diskUsage}%";
                            networkUsageText.Text = $"{networkUsage}%";
                            installedAppsCountText.Text = installedAppsCount.ToString();
                            processesCountText.Text = processesCount.ToString();
                            servicesCountText.Text = servicesCount.ToString();
                            gpuUsageText.Text = $"{gpuUsage}%";

                            cpuUsageText.Visibility = Visibility.Visible;
                            ramUsageText.Visibility = Visibility.Visible;
                            diskUsageText.Visibility = Visibility.Visible;
                            networkUsageText.Visibility = Visibility.Visible;
                            installedAppsCountText.Visibility = Visibility.Visible;
                            processesCountText.Visibility = Visibility.Visible;
                            servicesCountText.Visibility = Visibility.Visible;
                            gpuUsageText.Visibility = Visibility.Visible;
                        }
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error updating UI: {ex.Message}");
                }
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("UpdateSystemStats task was canceled.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unexpected error: {ex.Message}");
        }
    }


    private void HomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        _cancellationTokenSource.Cancel();
    }

    private int GetCpuUsage()
    {
        return (int)cpuCounter.NextValue();
    }

    private int GetRamUsage()
    {
        var wql = new ObjectQuery("SELECT FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem");
        var searcher = new ManagementObjectSearcher(wql);
        foreach (ManagementObject queryObj in searcher.Get())
        {
            var freeMemory = Convert.ToUInt64(queryObj["FreePhysicalMemory"]);
            var totalMemory = Convert.ToUInt64(queryObj["TotalVisibleMemorySize"]);
            var usedMemory = totalMemory - freeMemory;
            return (int)((usedMemory * 100) / totalMemory);
        }
        return 0;
    }

    private int GetDiskUsage()
    {
        return (int)diskCounter.NextValue();
    }

    private int GetNetworkUsage()
    {
        var profile = NetworkInformation.GetInternetConnectionProfile();
        if (profile != null)
        {
            var usageStates = new NetworkUsageStates
            {
                Roaming = TriStates.DoNotCare,
                Shared = TriStates.DoNotCare
            };

            var usageDetails = profile.GetNetworkUsageAsync(
                DateTime.Now.AddMinutes(-1),
                DateTime.Now,
                DataUsageGranularity.PerMinute,
                usageStates).GetAwaiter().GetResult();

            foreach (var usage in usageDetails)
            {
                return (int)((usage.BytesReceived / 60) / 1024);
            }
        }
        return 0;
    }

    private int GetInstalledAppsCount()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-Command \"(Get-AppxPackage -AllUsers | Select-Object -Unique Name).Count\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            return process != null && int.TryParse(process.StandardOutput.ReadToEnd().Trim(), out var appCount)
                ? appCount
                : 0;
        }
        catch (Exception ex)
        {
            LogHelper.LogError($"Failed to count Installed apps: {ex.Message}");
            return 0;
        }
    }

    private int GetProcessesCount()
    {
        return Process.GetProcesses().Length;
    }

    private int GetServicesCount()
    {
        var services = ServiceController.GetServices();
        return services.Length;
    }

    public static async Task<int> GetGpuUsage()
    {
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            var counterNames = category.GetInstanceNames();
            var gpuCounters = new List<PerformanceCounter>();
            var result = 0f;

            foreach (var counterName in counterNames)
            {
                if (counterName.EndsWith("engtype_3D"))
                {
                    foreach (var counter in category.GetCounters(counterName))
                    {
                        if (counter.CounterName == "Utilization Percentage")
                        {
                            gpuCounters.Add(counter);
                        }
                    }
                }
            }

            gpuCounters.ForEach(x =>
            {
                _ = x.NextValue();
            });
            await Task.Delay(1000);
            gpuCounters.ForEach(x =>
            {
                result += x.NextValue();
            });
            return (int)result;
        }
        catch
        {
            return 0;
        }
    }

    private void GithubButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://rayenghanmi.github.io/rytunex",
            UseShellExecute = true
        });
    }

    private void DiscordButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://discord.gg/gyBzyd364t",
            UseShellExecute = true
        });
    }
}
