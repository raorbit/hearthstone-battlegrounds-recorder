using Microsoft.Win32;

namespace BgRecorder.App;

/// <summary>Narrow seam over one HKCU Run-key value so the reconcile rule is testable without a registry.</summary>
internal interface IRunKey
{
    /// <summary>The current command registered for this app, or null when absent.</summary>
    string? Get();

    void Set(string command);

    void Delete();
}

/// <summary>What a reconcile pass did — for the startup log line.</summary>
internal enum RunKeyOutcome
{
    AlreadyCorrect,
    Written,
    Removed,
    AlreadyAbsent,
}

/// <summary>
/// Keeps the HKCU Run key in step with <c>AppSettings.LaunchAtLogin</c>. Reconcile-shaped rather than
/// toggle-shaped on purpose: it runs at every startup and on every settings change, so a stale command
/// (the app moved — e.g. a Velopack update changed the install path) heals itself instead of silently
/// launching the old binary.
/// </summary>
internal static class LaunchAtLogin
{
    public static RunKeyOutcome Reconcile(IRunKey key, bool enabled, string exePath)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);

        string? current = key.Get();

        if (!enabled)
        {
            if (current is null)
            {
                return RunKeyOutcome.AlreadyAbsent;
            }

            key.Delete();
            return RunKeyOutcome.Removed;
        }

        string desired = "\"" + exePath + "\"";
        if (string.Equals(current, desired, StringComparison.OrdinalIgnoreCase))
        {
            return RunKeyOutcome.AlreadyCorrect;
        }

        key.Set(desired);
        return RunKeyOutcome.Written;
    }
}

/// <summary>The real Run-key value under HKCU (per-user, no elevation needed).</summary>
internal sealed class WindowsRunKey : IRunKey
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BgRecorder";

    public string? Get()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) as string;
    }

    public void Set(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
