using DNSManagerPro.App.Dialogs;
using DNSManagerPro.Core.Configuration;
using DNSManagerPro.Core.Providers;
using DNSManagerPro.Core.Settings;
using DNSManagerPro.Data;
using DNSManagerPro.Infrastructure;
using DNSManagerPro.Tests.Configuration;
using DNSManagerPro.Tests.Profiles;
using DNSManagerPro.Windows.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DNSManagerPro.Tests.Core;

public sealed class SettingsPhase7Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dnsmp-s7-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // temp
        }
    }

    private SettingsService Svc(string name = "settings.json") => new(Path.Combine(_dir, name), NullLogger<SettingsService>.Instance);

    [Fact]
    public void New_fields_roundtrip_and_enums_are_stored_as_names()
    {
        var s = Svc();
        s.Update(c => c with { StartWithWindows = true, MinimizeToTray = true, ConfirmDnsChanges = false, Ipv6Handling = Ipv6Preference.Never, BenchmarkRepetitions = 9, BenchmarkDomains = ["example.com"] });
        Assert.Contains("\"Never\"", File.ReadAllText(Path.Combine(_dir, "settings.json")), StringComparison.Ordinal);
        var again = Svc();
        again.Load();
        Assert.Equal(Ipv6Preference.Never, again.Current.Ipv6Handling);
        Assert.False(again.Current.ConfirmDnsChanges);
        Assert.Equal(9, again.Current.BenchmarkRepetitions);
        Assert.Equal(["example.com"], again.Current.BenchmarkDomains);
    }

    [Fact]
    public void Defaults_are_safe_confirmation_on_everything_optional_off()
    {
        var d = new AppSettings();
        Assert.True(d.ConfirmDnsChanges);
        Assert.False(d.StartWithWindows);
        Assert.False(d.MonitoringEnabled);
        Assert.False(d.PreferEncryptedDns);
        Assert.Equal(Ipv6Preference.Auto, d.Ipv6Handling);
    }

    [Fact]
    public void Sanitize_clamps_numbers_and_drops_invalid_domains()
    {
        var s = new AppSettings
        {
            BenchmarkRepetitions = 5000,
            BenchmarkTimeoutMs = 1,
            BenchmarkParallelism = 0,
            BenchmarkDomains = ["Good.COM", "good.com", "bad domain", "nodot", "-x.com", "a;rm.com"],
            Ipv6Handling = (Ipv6Preference)99,
            DefaultAdapterId = "  {abc}  ",
        }.Sanitized();
        Assert.Equal(100, s.BenchmarkRepetitions);
        Assert.Equal(200, s.BenchmarkTimeoutMs);
        Assert.Equal(1, s.BenchmarkParallelism);
        Assert.Equal(["good.com"], s.BenchmarkDomains);
        Assert.Equal(Ipv6Preference.Auto, s.Ipv6Handling);
        Assert.Equal("{abc}", s.DefaultAdapterId);
    }

    [Fact]
    public void Sanitize_falls_back_to_default_domains_when_none_are_valid_and_rejects_odd_adapter_ids()
    {
        var s = new AppSettings { BenchmarkDomains = ["!!"], DefaultAdapterId = "bad\nid" }.Sanitized();
        Assert.Equal(AppSettings.DefaultDomains, s.BenchmarkDomains);
        Assert.Null(s.DefaultAdapterId);
        Assert.Null((new AppSettings { DefaultAdapterId = new string('x', 500) }).Sanitized().DefaultAdapterId);
        Assert.Equal(20, new AppSettings { BenchmarkDomains = [.. Enumerable.Range(0, 40).Select(i => $"d{i}.com")] }.Sanitized().BenchmarkDomains.Count);
    }

    [Fact]
    public void Hand_edited_file_with_unknown_enum_text_is_recovered_not_crashed()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ \"Ipv6Handling\": \"Sometimes\" }");
        var s = Svc();
        s.Load();
        Assert.Equal(Ipv6Preference.Auto, s.Current.Ipv6Handling);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void Export_then_import_restores_values_and_fires_changed()
    {
        var a = Svc("a.json");
        a.Update(c => c with { BenchmarkRepetitions = 12, LaunchMinimized = true });
        var export = Path.Combine(_dir, "export.json");
        a.ExportTo(export);

        var b = Svc("b.json");
        var fired = 0;
        b.Changed += _ => fired++;
        b.ImportFrom(export);
        Assert.Equal(12, b.Current.BenchmarkRepetitions);
        Assert.True(b.Current.LaunchMinimized);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Import_of_garbage_or_oversized_file_changes_nothing()
    {
        var s = Svc();
        s.Update(c => c with { BenchmarkRepetitions = 7 });
        Directory.CreateDirectory(_dir);
        var bad = Path.Combine(_dir, "bad.json");
        File.WriteAllText(bad, "not json at all");
        Assert.ThrowsAny<Exception>(() => s.ImportFrom(bad));
        var big = Path.Combine(_dir, "big.json");
        File.WriteAllText(big, new string(' ', (int)SettingsService.MaxFileBytes + 10));
        Assert.Throws<InvalidDataException>(() => s.ImportFrom(big));
        Assert.Equal(7, s.Current.BenchmarkRepetitions);
    }

    [Fact]
    public void Import_clamps_out_of_range_values()
    {
        Directory.CreateDirectory(_dir);
        var f = Path.Combine(_dir, "x.json");
        File.WriteAllText(f, "{ \"BenchmarkTimeoutMs\": 99999999, \"MonitoringIntervalMinutes\": -5 }");
        var s = Svc();
        s.ImportFrom(f);
        Assert.Equal(10_000, s.Current.BenchmarkTimeoutMs);
        Assert.Equal(AppSettings.MinIntervalMinutes, s.Current.MonitoringIntervalMinutes);
    }

    [Fact]
    public void Reset_restores_defaults()
    {
        var s = Svc();
        s.Update(c => c with { MonitoringEnabled = true, ConfirmDnsChanges = false });
        s.ResetToDefaults();
        Assert.False(s.Current.MonitoringEnabled);
        Assert.True(s.Current.ConfirmDnsChanges);
    }
}

public class StartupRegistrationTests
{
    [Fact]
    public void Command_quotes_the_path_and_adds_minimized_flag_on_request()
    {
        Assert.Equal("\"C:\\Program Files\\DNS Manager Pro\\App.exe\"", StartupRegistration.BuildCommand("C:\\Program Files\\DNS Manager Pro\\App.exe", false));
        Assert.EndsWith(" --minimized", StartupRegistration.BuildCommand("C:\\a b\\App.exe", true), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:\\a\" --evil \"b.exe")]
    [InlineData("C:\\a\nb.exe")]
    public void Command_rejects_paths_that_could_escape_the_quoting(string path) =>
        Assert.Throws<ArgumentException>(() => StartupRegistration.BuildCommand(path, false));
}

public sealed class DatabaseMaintenanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dnsmp-m-{Guid.NewGuid():N}");
    private readonly string _live;
    private readonly SqliteDatabase _db;
    private readonly DatabaseMaintenance _maint;

    public DatabaseMaintenanceTests()
    {
        Directory.CreateDirectory(_dir);
        _live = Path.Combine(_dir, "live.db");
        _db = new SqliteDatabase(_live);
        _db.Migrate();
        _maint = new DatabaseMaintenance(_db, _live);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // temp
        }
    }

    private static DnsSnapshot Snap() => new("{g}", 3, "Ethernet", false, ["192.168.1.1"], true, [], DateTime.UtcNow);

    private async Task AddHistoryAsync()
    {
        var id = await new SqliteHistoryRepository(_db).BeginAsync("{g}", "Ethernet", Snap(), "IPv4: 1.1.1.1", "Apply");
        await new SqliteHistoryRepository(_db).CompleteAsync(id, "Success", null);
    }

    [Fact]
    public async Task Backup_produces_a_valid_database_with_the_same_data_and_can_overwrite()
    {
        await AddHistoryAsync();
        var dest = Path.Combine(_dir, "backup.db");
        Assert.True((await _maint.BackupAsync(dest)).Success);
        Assert.True(DatabaseMaintenance.Validate(dest).Success);
        Assert.True((await _maint.BackupAsync(dest)).Success); // overwriting an existing backup works
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.NotNull(await new SqliteHistoryRepository(new SqliteDatabase(dest)).GetLastSuccessfulChangeAsync("{g}"));
    }

    [Fact]
    public async Task Backup_refuses_to_target_the_live_database()
    {
        Assert.False((await _maint.BackupAsync(_live)).Success);
    }

    [Fact]
    public void Validate_rejects_missing_non_sqlite_and_foreign_databases()
    {
        Assert.False(DatabaseMaintenance.Validate(Path.Combine(_dir, "nope.db")).Success);
        var junk = Path.Combine(_dir, "junk.db");
        File.WriteAllText(junk, "this is not a database");
        Assert.False(DatabaseMaintenance.Validate(junk).Success);

        var foreign = Path.Combine(_dir, "foreign.db");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={foreign};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE x(a); PRAGMA user_version = 3;";
            cmd.ExecuteNonQuery();
        }

        Assert.False(DatabaseMaintenance.Validate(foreign).Success);
    }

    [Fact]
    public void Validate_rejects_a_backup_from_a_newer_version()
    {
        var newer = Path.Combine(_dir, "newer.db");
        File.Copy(_live, newer);
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={newer};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"PRAGMA user_version = {SqliteDatabase.LatestVersion + 1};";
            cmd.ExecuteNonQuery();
        }

        var r = DatabaseMaintenance.Validate(newer);
        Assert.False(r.Success);
        Assert.Contains("newer", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restore_replaces_data_keeps_a_safety_copy_and_leaves_live_untouched_when_the_backup_is_invalid()
    {
        var backup = Path.Combine(_dir, "b.db");
        await _maint.BackupAsync(backup);   // backup has no history
        await AddHistoryAsync();            // live now has history

        var safetyDir = Path.Combine(_dir, "safety");
        var junk = Path.Combine(_dir, "junk.db");
        File.WriteAllText(junk, "garbage");
        Assert.False(_maint.Restore(junk, safetyDir).Success);
        Assert.NotNull(await new SqliteHistoryRepository(_db).GetLastSuccessfulChangeAsync("{g}")); // unchanged

        var r = _maint.Restore(backup, safetyDir);
        Assert.True(r.Success, r.Message);
        Assert.Null(await new SqliteHistoryRepository(_db).GetLastSuccessfulChangeAsync("{g}"));   // restored (empty) data
        var safety = Assert.Single(Directory.GetFiles(safetyDir, "before-restore-*.db"));
        Assert.NotNull(await new SqliteHistoryRepository(new SqliteDatabase(safety)).GetLastSuccessfulChangeAsync("{g}"));
        Assert.False(File.Exists(_live + ".restore.tmp"));
    }

    [Fact]
    public async Task Clear_history_removes_history_but_keeps_providers()
    {
        await AddHistoryAsync();
        await new SqliteProviderRepository(_db).SeedAsync();
        var before = (await new SqliteProviderRepository(_db).GetAllAsync()).Count;
        Assert.True((await _maint.ClearHistoryAsync()).Success);
        Assert.Empty(await new SqliteHistoryRepository(_db).GetRecentAsync(10));
        Assert.Equal(before, (await new SqliteProviderRepository(_db).GetAllAsync()).Count);
    }
}

public class ApplyDialogOptionsTests
{
    private static readonly DnsProvider Cloudflare = BuiltInProviders.All.First(p => p.Id == "cloudflare");

    private static (ApplyDnsViewModel Vm, TwoAdapterSystem Sys) Build(ApplyDnsOptions options, string preselect = "{a}")
    {
        var sys = new TwoAdapterSystem();
        var svc = new DnsChangeService(sys, sys, new FakeHistory(), new FakePrivilege(true), new FakeProbe(), new FakeCache(), NullLogger<DnsChangeService>.Instance) { VerifyDelay = TimeSpan.Zero };
        var req = new ApplyDnsRequest("Apply", "Cloudflare", Cloudflare, null, string.Empty);
        return (new ApplyDnsViewModel(req, sys.Adapters, preselect, svc, new FakeElevation(), true, null, options), sys);
    }

    [Fact]
    public async Task Confirmation_needs_two_clicks_and_changes_nothing_after_the_first()
    {
        var (vm, sys) = Build(new ApplyDnsOptions(RequireConfirmation: true));
        Assert.Equal("Review and apply", vm.PrimaryText);
        await vm.PrimaryCommand.ExecuteAsync(null);
        Assert.True(vm.IsArmed);
        Assert.False(vm.Succeeded);
        Assert.Empty(sys.AppliedTo);
        Assert.Equal("Yes, apply these changes", vm.PrimaryText);
        await vm.PrimaryCommand.ExecuteAsync(null);
        Assert.True(vm.Succeeded);
        Assert.Equal(["{a}"], sys.AppliedTo);
    }

    [Fact]
    public async Task Changing_the_selection_cancels_a_pending_confirmation()
    {
        var (vm, sys) = Build(new ApplyDnsOptions(RequireConfirmation: true));
        await vm.PrimaryCommand.ExecuteAsync(null);
        Assert.True(vm.IsArmed);
        vm.Choices[1].IsChecked = true;
        Assert.False(vm.IsArmed);
        await vm.PrimaryCommand.ExecuteAsync(null);   // arms again, still nothing applied
        Assert.Empty(sys.AppliedTo);
    }

    [Fact]
    public async Task Without_confirmation_a_single_click_applies()
    {
        var (vm, sys) = Build(new ApplyDnsOptions(RequireConfirmation: false));
        await vm.PrimaryCommand.ExecuteAsync(null);
        Assert.Equal(["{a}"], sys.AppliedTo);
    }

    [Fact]
    public void Ipv6_preference_never_keeps_ipv6_unchanged_and_always_includes_it()
    {
        Assert.Equal(FamilyChoice.Unchanged, Build(new ApplyDnsOptions(Ipv6: Ipv6Preference.Never)).Vm.ChoiceV6);
        Assert.Equal(FamilyChoice.Source, Build(new ApplyDnsOptions(Ipv6: Ipv6Preference.Always)).Vm.ChoiceV6);
        Assert.Equal(FamilyChoice.Unchanged, Build(new ApplyDnsOptions(Ipv6: Ipv6Preference.Auto)).Vm.ChoiceV6);   // test adapters have no IPv6
    }
}
