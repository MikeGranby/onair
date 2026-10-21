using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace OnAir;

/// <summary>
/// Detects microphone use from the CapabilityAccessManager consent store. Windows keeps a key per
/// app with LastUsedTimeStart/LastUsedTimeStop FILETIME values; while an app holds the microphone,
/// LastUsedTimeStop is 0. Desktop (non-packaged) apps live under a "NonPackaged" subkey, named by
/// their exe path with '\' replaced by '#'.
/// </summary>
sealed class MicrophoneMonitor : IDisposable
{
    const string ConsentStorePath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    const int RegNotifyChangeName = 0x1;
    const int RegNotifyChangeLastSet = 0x4;
    const int RegNotifyThreadAgnostic = 0x10000000;

    readonly List<(RegistryKey Key, AutoResetEvent Changed)> _roots = [];

    public MicrophoneMonitor()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            var key = hive.OpenSubKey(ConsentStorePath);
            if (key == null)
                continue;
            var changed = new AutoResetEvent(false);
            Arm(key, changed);
            _roots.Add((key, changed));
        }
        if (_roots.Count == 0)
            throw new InvalidOperationException("The microphone consent store was not found in the registry (requires Windows 10 1903 or later).");
    }

    /// <summary>Apps currently holding the microphone: package family names, or exe paths for desktop apps.</summary>
    public IReadOnlyList<string> GetInUseApps()
    {
        var apps = new List<string>();
        foreach (var (key, _) in _roots)
            Scan(key, apps, nonPackaged: false);
        return apps.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Blocks until the consent store changes, the timeout elapses, or one of <paramref name="others"/> is signaled.</summary>
    public void WaitForChange(TimeSpan timeout, params WaitHandle[] others)
    {
        var handles = _roots.Select(r => (WaitHandle)r.Changed).Concat(others).ToArray();
        int index = WaitHandle.WaitAny(handles, timeout);
        // A notification fires once; re-arm before the caller re-reads so no change is missed.
        if (index < _roots.Count)
            Arm(_roots[index].Key, _roots[index].Changed);
    }

    public static string FriendlyName(string app) =>
        app.Contains('\\') ? Path.GetFileName(app) : app.Split('_')[0];

    static void Scan(RegistryKey parent, List<string> apps, bool nonPackaged)
    {
        foreach (var name in parent.GetSubKeyNames())
        {
            try
            {
                using var key = parent.OpenSubKey(name);
                if (key == null)
                    continue;
                if (!nonPackaged && name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                    Scan(key, apps, nonPackaged: true);
                else if (IsInUse(key))
                    apps.Add(nonPackaged ? name.Replace('#', '\\') : name);
            }
            catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException)
            {
                // The key was deleted mid-scan or is not readable; skip it.
            }
        }
    }

    static bool IsInUse(RegistryKey key) =>
        ReadFileTime(key, "LastUsedTimeStart") != 0 && ReadFileTime(key, "LastUsedTimeStop") == 0;

    static long ReadFileTime(RegistryKey key, string name) => key.GetValue(name) switch
    {
        long l => l,
        int i => i,
        _ => 0,
    };

    static void Arm(RegistryKey key, AutoResetEvent changed)
    {
        int error = RegNotifyChangeKeyValue(
            key.Handle,
            bWatchSubtree: true,
            RegNotifyChangeName | RegNotifyChangeLastSet | RegNotifyThreadAgnostic,
            changed.SafeWaitHandle,
            fAsynchronous: true);
        if (error != 0)
            throw new Win32Exception(error);
    }

    [DllImport("advapi32.dll")]
    static extern int RegNotifyChangeKeyValue(SafeRegistryHandle hKey, bool bWatchSubtree, int dwNotifyFilter, SafeWaitHandle hEvent, bool fAsynchronous);

    public void Dispose()
    {
        foreach (var (key, changed) in _roots)
        {
            key.Dispose();
            changed.Dispose();
        }
        _roots.Clear();
    }
}
