using Microsoft.Win32;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace BrightnessToggle;

internal interface IStartupSettings
{
    Task<bool> IsEnabled();
    Task<bool> SetEnabled(bool enabled);
}

internal sealed class StartupSettings : IStartupSettings
{
    private const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName="BrightnessToggle";
    private const string TaskId="BrightnessToggleStartup";
    internal static string Command(string path)=>"\""+path+"\" --startup";
    internal static bool IsStartup(string[] args)=>args.Any(a=>a.Equals("--startup",StringComparison.OrdinalIgnoreCase));
    private static bool Packaged
    {
        get {uint length=0;return GetCurrentPackageFullName(ref length,IntPtr.Zero)!=15700;}
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern int GetCurrentPackageFullName(ref uint length,IntPtr name);
    public async Task<bool> IsEnabled()
    {
        if(Packaged){var task=await StartupTask.GetAsync(TaskId);return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;}
        using var key=Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(ValueName) as string,Command(Environment.ProcessPath!),StringComparison.OrdinalIgnoreCase);
    }
    public async Task<bool> SetEnabled(bool enabled)
    {
        if(Packaged)
        {
            var task=await StartupTask.GetAsync(TaskId);
            if(enabled)await task.RequestEnableAsync();else task.Disable();
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        using(var key=Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if(enabled)key.SetValue(ValueName,Command(Environment.ProcessPath!),RegistryValueKind.String);
            else key.DeleteValue(ValueName,false);
        }
        return await IsEnabled();
    }
}
