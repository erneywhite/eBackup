using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using eBackup.Core.Abstractions;
using eBackup.Core.Engine;
using eBackup.Core.Model;
using eBackup.Service.Security;
using Xunit;

namespace eBackup.Tests.Service;

/// <summary>
/// Служба под SYSTEM читает/пишет только то, что владелец задачи может сам: политика записи
/// при восстановлении, проверки в движке и реальная Authz-проверка прав (последняя — только Windows).
/// </summary>
public class PathAccessTests
{
    /// <summary>Фейковые права: разрешено всё, кроме перечисленных путей (и всего под ними).</summary>
    private sealed class FakeAccess(params string[] denied) : IPathAccess
    {
        public List<string> Asked { get; } = [];
        public bool CanRead(string path) => Allowed(path);
        public bool CanWrite(string path) => Allowed(path);
        private bool Allowed(string path)
        {
            Asked.Add(path);
            return !denied.Any(d => eBackup.Core.Paths.PathSafety.IsWithin(d, path));
        }
        public void Dispose() { }
    }

    private sealed class DirModule(string id, string tokenPath) : IBackupModule
    {
        public string Id => id;
        public string DisplayName => id;
        public Task<IReadOnlyList<PathEntry>> DiscoverAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PathEntry>>(
                [new PathEntry { TokenPath = tokenPath, Type = PathEntryType.Directory, ArchivePath = id + "/data" }]);
    }

    private static string Temp(string tag) => Path.Combine(Path.GetTempPath(), $"ebk-{tag}-{Guid.NewGuid():N}");

    // ---- RestoreWritePolicy ----

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ObsDeclares =
        new Dictionary<string, IReadOnlyList<string>> { ["obs"] = ["{PROGRAMFILES}/obs-studio/obs-plugins"] };

    [Fact]
    public void Policy_Allows_Module_Declared_ProgramFiles_Path_Without_User_Rights()
    {
        // Вариант «как раньше»: плагины OBS в Program Files пишутся и без прав пользователя.
        var everythingDenied = new FakeAccess(Path.GetPathRoot(Path.GetTempPath())!);
        var policy = new RestoreWritePolicy(everythingDenied, ObsDeclares, protectedRoots: []);

        Assert.True(policy.CanWrite("obs", "{PROGRAMFILES}/obs-studio/obs-plugins", @"C:\Program Files\obs-studio\obs-plugins\x.dll"));
        Assert.True(policy.CanWrite("obs", "{PROGRAMFILES}/obs-studio/obs-plugins/sub", @"C:\Program Files\obs-studio\obs-plugins\sub\x.dll"));
        Assert.Empty(everythingDenied.Asked);
    }

    [Theory]
    [InlineData("obs", "{PROGRAMFILES}/SomeService")]               // не объявлено модулем
    [InlineData("obs", "{PROGRAMFILES}/obs-studio/obs-plugins-evil")] // «похожий» префикс — не считается
    [InlineData("vts", "{PROGRAMFILES}/obs-studio/obs-plugins")]     // объявлено ДРУГИМ модулем
    [InlineData("obs", "C:/Windows/System32")]                       // сырой абсолютный путь из архива
    [InlineData("obs", "{APPDATA}/obs-studio")]                      // профиль — по правам пользователя
    public void Policy_Falls_Back_To_User_Rights_Outside_Declared_Paths(string module, string token)
    {
        var dest = Path.Combine(Temp("dst"), "x.dll");
        var denied = new FakeAccess(dest);
        var allowed = new FakeAccess();

        Assert.False(new RestoreWritePolicy(denied, ObsDeclares, protectedRoots: []).CanWrite(module, token, dest));
        Assert.True(new RestoreWritePolicy(allowed, ObsDeclares, protectedRoots: []).CanWrite(module, token, dest));
        Assert.Single(denied.Asked);
    }

    [Fact]
    public void Policy_Never_Writes_Into_Protected_Roots()
    {
        // Данные/конфиг eBackup и папка службы — никогда, даже по объявленному модулем пути и при полных правах.
        var root = Temp("prot");
        var policy = new RestoreWritePolicy(new FakeAccess(), ObsDeclares, protectedRoots: [root]);

        Assert.False(policy.CanWrite("obs", "{PROGRAMFILES}/obs-studio/obs-plugins", Path.Combine(root, "keys", "k")));
        Assert.False(policy.CanWriteIntoFolder(Path.Combine(root, "config")));
        Assert.True(policy.CanWriteIntoFolder(Temp("other")));
    }

    [Fact]
    public void Policy_Folder_Mode_Ignores_Module_Exemptions()
    {
        // «Восстановить в папку»: исключения модулей не действуют — иначе TargetDir=System32 прошёл бы.
        var dest = Path.Combine(Temp("dst"), "obs", "install", "x.dll");
        var policy = new RestoreWritePolicy(new FakeAccess(dest), ObsDeclares, protectedRoots: []);
        Assert.False(policy.CanWriteIntoFolder(dest));
    }

    [Fact]
    public async Task Collect_Takes_Only_Machine_Paths_Declared_By_Each_Module()
    {
        var declared = await RestoreWritePolicy.CollectDeclaredMachinePathsAsync(
            [new DirModule("obs", "{PROGRAMFILES}/obs-studio/obs-plugins/"), new DirModule("cfg", "{APPDATA}/cfg"),
             new DirModule("pd", "{PROGRAMDATA}\\Vendor\\App")],
            t => t, CancellationToken.None);

        Assert.Equal(["{PROGRAMFILES}/obs-studio/obs-plugins"], declared["obs"]);
        Assert.Equal(["{PROGRAMDATA}/Vendor/App"], declared["pd"]);
        Assert.False(declared.ContainsKey("cfg"));
    }

    // ---- движок: проверки прав ----

    [Fact]
    public async Task Backup_Skips_Sources_The_User_Cannot_Read()
    {
        var root = Temp("bkacl");
        try
        {
            var src = Path.Combine(root, "src");
            Directory.CreateDirectory(Path.Combine(src, "secret"));
            await File.WriteAllTextAsync(Path.Combine(src, "ok.txt"), "ok");
            await File.WriteAllTextAsync(Path.Combine(src, "secret", "s.txt"), "s");
            var otherDir = Path.Combine(root, "other");
            Directory.CreateDirectory(otherDir);
            await File.WriteAllTextAsync(Path.Combine(otherDir, "o.txt"), "o");

            var access = new FakeAccess(Path.Combine(src, "secret"), otherDir);
            var engine = new BackupEngine();
            var archive = await engine.CreateBackupAsync(
                [new DirModule("a", src), new DirModule("b", otherDir)], Path.Combine(root, "build"), "t",
                canReadSource: access.CanRead);

            using var zip = System.IO.Compression.ZipFile.OpenRead(archive);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("data/a/data/ok.txt", names);
            Assert.DoesNotContain(names, n => n.Contains("secret"));
            Assert.DoesNotContain(names, n => n.StartsWith("data/b/"));
            Assert.Equal(2, engine.LastSkippedCount); // secret/s.txt + папка other целиком
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Restore_Skips_Targets_The_User_Cannot_Write_And_Counts_Them()
    {
        var root = Temp("rsacl");
        try
        {
            var src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            await File.WriteAllTextAsync(Path.Combine(src, "a.txt"), "A");
            await File.WriteAllTextAsync(Path.Combine(src, "b.txt"), "B");
            var archive = await new BackupEngine().CreateBackupAsync([new DirModule("m", src)], Path.Combine(root, "build"), "t");

            var dst = Path.Combine(root, "dst");
            var deniedFile = Path.Combine(dst, "m", "data", "a.txt");
            var asked = new List<(string Module, string Token, string Dest)>();
            var engine = new BackupEngine();
            await engine.RestoreAsync(archive, destinationRootOverride: dst,
                canWriteTarget: (m, t, d) => { asked.Add((m, t, d)); return d != deniedFile; });

            Assert.False(File.Exists(deniedFile));
            Assert.True(File.Exists(Path.Combine(dst, "m", "data", "b.txt")));
            Assert.Equal(1, engine.LastRestoreSkippedCount);
            Assert.All(asked, a => Assert.Equal("m", a.Module));
            Assert.All(asked, a => Assert.Equal(src, a.Token));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---- реальная Authz-проверка (Windows) ----

    [Fact]
    public void Authz_Current_User_Can_Read_And_Write_Own_Temp_But_Not_Denied_Folder()
    {
        var sid = WindowsIdentity.GetCurrent().User!;
        var root = Temp("authz");
        var denied = Path.Combine(root, "denied");
        Directory.CreateDirectory(denied);
        File.WriteAllText(Path.Combine(root, "own.txt"), "x");
        try
        {
            // Явный запрет текущему пользователю на чтение и запись в «denied».
            var di = new DirectoryInfo(denied);
            var sec = di.GetAccessControl();
            sec.AddAccessRule(new FileSystemAccessRule(sid,
                FileSystemRights.ReadData | FileSystemRights.WriteData | FileSystemRights.CreateDirectories,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
                AccessControlType.Deny));
            di.SetAccessControl(sec);

            using var access = new AuthzPathAccess(sid.Value, profileRoot: null);
            Assert.True(access.UsesAuthz);

            Assert.True(access.CanRead(Path.Combine(root, "own.txt")));
            Assert.True(access.CanWrite(Path.Combine(root, "own.txt")));
            Assert.True(access.CanWrite(Path.Combine(root, "new", "deep", "file.txt"))); // ещё не существует

            Assert.False(access.CanRead(denied));
            Assert.False(access.CanWrite(Path.Combine(denied, "file.txt")));
            Assert.False(access.CanWrite(Path.Combine(denied, "sub", "file.txt")));
        }
        finally
        {
            try
            {
                var di = new DirectoryInfo(denied);
                var sec = di.GetAccessControl();
                sec.PurgeAccessRules(sid);
                di.SetAccessControl(sec);
            }
            catch { /* best effort */ }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Authz_Fallback_Without_Context_Allows_Only_Profile()
    {
        // Authz-контекст не построился (здесь — испорченный SID) → разрешено только внутри профиля.
        var profile = Temp("prof");
        using var access = new AuthzPathAccess("not-a-sid", profileRoot: profile);
        Assert.False(access.UsesAuthz);

        Assert.True(access.CanWrite(Path.Combine(profile, "AppData", "x")));
        Assert.False(access.CanWrite(Path.Combine(Temp("elsewhere"), "x")));
    }
}
