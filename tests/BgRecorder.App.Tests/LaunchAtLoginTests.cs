using BgRecorder.App;
using Xunit;

namespace BgRecorder.App.Tests;

public sealed class LaunchAtLoginTests
{
    private const string Exe = @"C:\Apps\BgRecorder\BgRecorder.App.exe";
    private const string Quoted = "\"" + Exe + "\"";

    [Fact]
    public void Enabling_with_no_key_writes_the_quoted_exe_path()
    {
        var key = new FakeRunKey();

        var outcome = LaunchAtLogin.Reconcile(key, enabled: true, Exe);

        Assert.Equal(RunKeyOutcome.Written, outcome);
        Assert.Equal(Quoted, key.Value);
    }

    [Fact]
    public void Enabling_over_a_stale_command_heals_it_to_the_current_path()
    {
        // The app moved (e.g. an update changed the install dir): the old command must not survive.
        var key = new FakeRunKey { Value = "\"C:\\OldPlace\\BgRecorder.App.exe\"" };

        var outcome = LaunchAtLogin.Reconcile(key, enabled: true, Exe);

        Assert.Equal(RunKeyOutcome.Written, outcome);
        Assert.Equal(Quoted, key.Value);
    }

    [Fact]
    public void Enabling_when_already_correct_touches_nothing()
    {
        var key = new FakeRunKey { Value = Quoted };

        var outcome = LaunchAtLogin.Reconcile(key, enabled: true, Exe);

        Assert.Equal(RunKeyOutcome.AlreadyCorrect, outcome);
        Assert.Equal(0, key.Writes);
    }

    [Fact]
    public void Path_comparison_ignores_case_like_the_file_system_does()
    {
        var key = new FakeRunKey { Value = Quoted.ToUpperInvariant() };

        var outcome = LaunchAtLogin.Reconcile(key, enabled: true, Exe);

        Assert.Equal(RunKeyOutcome.AlreadyCorrect, outcome);
    }

    [Fact]
    public void Disabling_removes_an_existing_key()
    {
        var key = new FakeRunKey { Value = Quoted };

        var outcome = LaunchAtLogin.Reconcile(key, enabled: false, Exe);

        Assert.Equal(RunKeyOutcome.Removed, outcome);
        Assert.Null(key.Value);
    }

    [Fact]
    public void Disabling_when_absent_touches_nothing()
    {
        var key = new FakeRunKey();

        var outcome = LaunchAtLogin.Reconcile(key, enabled: false, Exe);

        Assert.Equal(RunKeyOutcome.AlreadyAbsent, outcome);
        Assert.Equal(0, key.Writes);
        Assert.Equal(0, key.Deletes);
    }

    private sealed class FakeRunKey : IRunKey
    {
        public string? Value { get; set; }

        public int Writes { get; private set; }

        public int Deletes { get; private set; }

        public string? Get() => Value;

        public void Set(string command)
        {
            Writes++;
            Value = command;
        }

        public void Delete()
        {
            Deletes++;
            Value = null;
        }
    }
}
