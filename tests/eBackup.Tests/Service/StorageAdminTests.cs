using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using eBackup.Core.Crypto;
using eBackup.Core.History;
using eBackup.Core.Modules;
using eBackup.Core.Security;
using eBackup.Ipc.Contracts;
using eBackup.Ipc.Server;
using eBackup.Security;
using eBackup.Service.Handlers;
using eBackup.Service.Jobs;
using eBackup.Storage;
using Xunit;

namespace eBackup.Tests.Service;

public sealed class StorageAdminTests : IDisposable
{
    private readonly string _root;

    public StorageAdminTests() => _root = Path.Combine(Path.GetTempPath(), $"ebk-stadmin-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private static readonly CallerContext Caller = new("S-1-5-21-1", IsAdmin: false);

    private (ServiceHandlers handlers, StorageStore storages, JobManager jobs) Build()
    {
        var history = new HistoryStore(Path.Combine(_root, "hist"));
        var storages = new StorageStore(
            new MachineKeyProtector(new MachineKeyStore(Path.Combine(_root, "key", "machine.key"))),
            Path.Combine(_root, "cfg", "storages.json"),
            Path.Combine(_root, "cfg", "connections.json"));
        var jobs = new JobManager(new BackupRunner(_ => []), rid => new JobChannel(history, rid));
        var handlers = new ServiceHandlers(jobs, history, new ModuleRegistry([]), storages, "inst", "1.2.0");
        return (handlers, storages, jobs);
    }

    [Fact]
    public async Task Upsert_Encrypts_Secret_With_Machine_Key_And_Lists()
    {
        var (handlers, storages, jobs) = Build();
        await using var _ = jobs;

        await handlers.UpsertStorageAsync(new StorageInput
        {
            Id = "nas",
            Name = "NAS",
            Kind = "Sftp",
            Settings = new() { ["host"] = "192.168.1.2", ["port"] = "2022", ["username"] = "u" },
            PlaintextSecrets = new() { ["password"] = "hunter2" },
        }, Caller, default);

        var nas = Assert.Single(await handlers.ListStoragesAsync(Caller, default));
        Assert.Equal("NAS", nas.Name);
        Assert.Equal("Sftp", nas.Kind);
        Assert.True(nas.HasSecret);

        // Секрет реально под машинным ключом — и служба расшифровывает его обратно.
        var saved = (await storages.LoadAsync()).Single(s => s.Id == "nas");
        Assert.Equal(SecretScheme.MachineKeyV1, SecretSchemeDetector.Detect(saved.ProtectedPassword));
        Assert.Equal("hunter2", storages.Unprotect(saved.ProtectedPassword!));
        Assert.Equal("192.168.1.2", saved.Host);
        Assert.Equal(2022, saved.Port);
    }

    [Fact]
    public async Task Upsert_RoundTrips_All_Fields_And_Secrets()
    {
        // Полный round-trip всех полей хранилища и шести секретов через StorageInput → SavedStorage:
        // секреты шифруются машинным ключом и расшифровываются обратно, несекретные поля сохраняются.
        var (handlers, storages, jobs) = Build();
        await using var _ = jobs;

        await handlers.UpsertStorageAsync(new StorageInput
        {
            Id = "full", Name = "Full", Kind = "S3",
            Settings = new()
            {
                ["path"] = "D:\\b", ["shareUsername"] = "su", ["host"] = "h", ["port"] = "2022",
                ["username"] = "u", ["remoteDirectory"] = "rd", ["useFtps"] = "true",
                ["allowUntrustedCertificate"] = "true", ["serviceUrl"] = "svc", ["bucket"] = "bk",
                ["accessKeyId"] = "ak", ["forcePathStyle"] = "false",
            },
            PlaintextSecrets = new()
            {
                ["sharePassword"] = "sp", ["password"] = "pw", ["privateKey"] = "pk",
                ["keyPassphrase"] = "kp", ["secretKey"] = "sk", ["oauthToken"] = "ot",
            },
        }, Caller, default);

        var s = (await storages.LoadAsync()).Single(x => x.Id == "full");
        Assert.Equal("D:\\b", s.Path);
        Assert.Equal("su", s.ShareUsername);
        Assert.Equal("h", s.Host);
        Assert.Equal(2022, s.Port);
        Assert.Equal("u", s.Username);
        Assert.Equal("rd", s.RemoteDirectory);
        Assert.True(s.UseFtps);
        Assert.True(s.AllowUntrustedCertificate);
        Assert.Equal("svc", s.ServiceUrl);
        Assert.Equal("bk", s.Bucket);
        Assert.Equal("ak", s.AccessKeyId);
        Assert.False(s.ForcePathStyle);
        Assert.Equal("sp", storages.Unprotect(s.ProtectedSharePassword!));
        Assert.Equal("pw", storages.Unprotect(s.ProtectedPassword!));
        Assert.Equal("pk", storages.Unprotect(s.ProtectedPrivateKey!));
        Assert.Equal("kp", storages.Unprotect(s.ProtectedKeyPassphrase!));
        Assert.Equal("sk", storages.Unprotect(s.ProtectedSecretKey!));
        Assert.Equal("ot", storages.Unprotect(s.ProtectedOAuthToken!));
    }

    [Fact]
    public async Task Edit_Without_Secret_Keeps_It_And_GetStorage_Returns_Fields()
    {
        var (handlers, storages, jobs) = Build();
        await using var _ = jobs;

        await handlers.UpsertStorageAsync(new StorageInput
        {
            Id = "nas", Name = "NAS", Kind = "Sftp",
            Settings = new() { ["host"] = "h", ["port"] = "2022", ["username"] = "u" },
            PlaintextSecrets = new() { ["password"] = "hunter2" },
        }, Caller, default);

        // правка БЕЗ ввода пароля (поле оставлено пустым) — только переименование
        await handlers.UpsertStorageAsync(new StorageInput
        {
            Id = "nas", Name = "NAS-2", Kind = "Sftp",
            Settings = new() { ["host"] = "h", ["port"] = "2022", ["username"] = "u" },
        }, Caller, default);

        var saved = (await storages.LoadAsync()).Single(s => s.Id == "nas");
        Assert.Equal("NAS-2", saved.Name);
        Assert.Equal("hunter2", storages.Unprotect(saved.ProtectedPassword!)); // секрет не потерян

        var detail = await handlers.GetStorageAsync(new GetStorageRequest { Id = "nas" }, Caller, default);
        Assert.Equal("NAS-2", detail.Name);
        Assert.Equal("h", detail.Settings["host"]);
        Assert.Equal("2022", detail.Settings["port"]);
        Assert.Contains("password", detail.PresentSecrets); // редактор покажет «оставить прежний»
    }

    [Fact]
    public async Task TestStorage_LocalFolder_Reports_Ok_And_FreeBytes()
    {
        var (handlers, _, jobs) = Build();
        await using var __ = jobs;

        var dir = Path.Combine(_root, "dest");
        Directory.CreateDirectory(dir);
        await handlers.UpsertStorageAsync(new StorageInput
        {
            Id = "d", Name = "D", Kind = "LocalFolder", Settings = new() { ["path"] = dir },
        }, Caller, default);

        var res = await handlers.TestStorageAsync(new TestStorageRequest { StorageId = "d" }, Caller, default);
        Assert.True(res.Ok);
        Assert.NotNull(res.FreeBytes);
    }

    [Fact]
    public async Task ListArchives_Returns_Files_In_Storage()
    {
        var (handlers, _, jobs) = Build();
        await using var __ = jobs;

        var dir = Path.Combine(_root, "arch");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "b1.ebk"), "x");
        await File.WriteAllTextAsync(Path.Combine(dir, "b2.ebk"), "yy");

        await handlers.UpsertStorageAsync(new StorageInput
        {
            Id = "d", Name = "D", Kind = "LocalFolder", Settings = new() { ["path"] = dir },
        }, Caller, default);

        var files = await handlers.ListArchivesAsync(new ListArchivesRequest { StorageId = "d" }, Caller, default);
        Assert.Contains(files, f => f.Name == "b1.ebk");
        Assert.Contains(files, f => f.Name == "b2.ebk" && f.Length == 2);
    }

    [Fact]
    public async Task ListArchives_Flags_Encrypted_Archives()
    {
        var (handlers, _, jobs) = Build();
        await using var __ = jobs;

        var dir = Path.Combine(_root, "arch-enc");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "plain.ebk"), "not really ebke");
        var src = Path.Combine(_root, "src.bin");
        await File.WriteAllTextAsync(src, "secret payload");
        await ArchiveCipher.EncryptAsync(src, Path.Combine(dir, "enc.ebk"), "pass");

        await handlers.UpsertStorageAsync(new StorageInput
        {
            Id = "e", Name = "E", Kind = "LocalFolder", Settings = new() { ["path"] = dir },
        }, Caller, default);

        var files = await handlers.ListArchivesAsync(
            new ListArchivesRequest { StorageId = "e", IncludeEncryption = true }, Caller, default);
        Assert.True(files.Single(f => f.Name == "enc.ebk").Encrypted, "EBKE-архив должен быть помечен зашифрованным");
        Assert.False(files.Single(f => f.Name == "plain.ebk").Encrypted, "обычный .ebk не должен быть помечен");
    }

    [Fact]
    public async Task DeleteArchive_Removes_File_In_Storage()
    {
        var (handlers, _, jobs) = Build();
        await using var __ = jobs;

        var dir = Path.Combine(_root, "arch2");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "old.ebk"), "x");
        await handlers.UpsertStorageAsync(new StorageInput
        {
            Id = "d", Name = "D", Kind = "LocalFolder", Settings = new() { ["path"] = dir },
        }, Caller, default);

        await handlers.DeleteArchiveAsync(new DeleteArchiveRequest { StorageId = "d", RemoteName = "old.ebk" }, Caller, default);
        Assert.False(File.Exists(Path.Combine(dir, "old.ebk")));
    }

    [Fact]
    public async Task Delete_Removes_Storage()
    {
        var (handlers, _, jobs) = Build();
        await using var __ = jobs;

        await handlers.UpsertStorageAsync(new StorageInput
        {
            Id = "x", Name = "X", Kind = "LocalFolder", Settings = new() { ["path"] = @"D:\Backups" },
        }, Caller, default);
        Assert.Single(await handlers.ListStoragesAsync(Caller, default));

        await handlers.DeleteStorageAsync(new DeleteByIdRequest { Id = "x" }, Caller, default);
        Assert.Empty(await handlers.ListStoragesAsync(Caller, default));
    }
}
