using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using eBackup.Core.Paths;

namespace eBackup.Service.Security;

/// <summary>
/// «Может ли ВЛАДЕЛЕЦ задачи сам прочитать/записать этот путь». Служба работает под SYSTEM и обходит
/// любые ACL — без этой проверки обычный пользователь через неё читал бы чужие профили и писал бы
/// куда угодно (System32 и т.п.). Проверяем права пользователя, а действие выполняет служба.
/// </summary>
public interface IPathAccess : IDisposable
{
    /// <summary>Прочитать файл / перечислить папку.</summary>
    bool CanRead(string path);

    /// <summary>Записать файл (существующий — перезапись; новый — создание в ближайшей существующей папке).</summary>
    bool CanWrite(string path);
}

/// <summary>
/// Проверка через Windows Authz API по SID (без токена: работает и для расписаний, когда пользователь
/// не вошёл). Группы пользователя (в т.ч. «Администраторы» — без UAC-фильтра) вычисляет сама Authz,
/// поэтому для админа поведение не меняется. Дескриптор берётся по имени пути — junction/symlink в пути
/// разворачиваются, т.е. проверяется РЕАЛЬНАЯ цель. Если Authz-контекст построить не удалось (напр.
/// доменный SID без связи с DC) — откат: разрешено только внутри профиля пользователя.
/// </summary>
public sealed class AuthzPathAccess : IPathAccess
{
    private const uint FileReadData = 0x1;     // = FILE_LIST_DIRECTORY для папок
    private const uint FileWriteData = 0x2;    // = FILE_ADD_FILE для папок
    private const uint FileAddSubdirectory = 0x4;

    private readonly IntPtr _rm;
    private readonly IntPtr _ctx;
    private readonly string? _profileRoot; // для отката без Authz
    private bool _disposed;

    public AuthzPathAccess(string sid, string? profileRoot)
    {
        _profileRoot = profileRoot;
        try
        {
            var sidObj = new SecurityIdentifier(sid);
            var sidBytes = new byte[sidObj.BinaryLength];
            sidObj.GetBinaryForm(sidBytes, 0);

            if (!AuthzInitializeResourceManager(AuthzRmFlagNoAudit, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, null, out _rm))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!AuthzInitializeContextFromSid(0, sidBytes, _rm, IntPtr.Zero, default, IntPtr.Zero, out _ctx))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            if (_rm != IntPtr.Zero) AuthzFreeResourceManager(_rm);
            _rm = IntPtr.Zero;
            _ctx = IntPtr.Zero;
        }
    }

    /// <summary>Authz доступен (иначе работает откат «только профиль»).</summary>
    public bool UsesAuthz => _ctx != IntPtr.Zero;

    public bool CanRead(string path)
    {
        var full = Path.GetFullPath(path);
        if (!UsesAuthz) return WithinProfile(full);
        return Check(full, FileReadData);
    }

    public bool CanWrite(string path)
    {
        var full = Path.GetFullPath(path);
        if (!UsesAuthz) return WithinProfile(full);

        if (File.Exists(full))
            return Check(full, FileWriteData);
        if (Directory.Exists(full))
            return Check(full, FileWriteData); // папка-цель: право создавать в ней файлы

        // Новый файл: права на ближайшей существующей папке. Прямой родитель — создать файл;
        // дальше по дереву — создать подпапку (промежуточные папки создаст движок).
        var parent = Path.GetDirectoryName(full);
        var needed = FileWriteData;
        while (parent is not null && !Directory.Exists(parent))
        {
            parent = Path.GetDirectoryName(parent);
            needed = FileAddSubdirectory;
        }
        return parent is not null && Check(parent, needed);
    }

    private bool WithinProfile(string full)
        => _profileRoot is not null && PathSafety.IsWithin(_profileRoot, full);

    private bool Check(string fullPath, uint desired)
    {
        byte[] sd;
        try
        {
            const AccessControlSections sections =
                AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
            FileSystemSecurity security = Directory.Exists(fullPath)
                ? new DirectoryInfo(fullPath).GetAccessControl(sections)
                : new FileInfo(fullPath).GetAccessControl(sections);
            sd = security.GetSecurityDescriptorBinaryForm();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or PrivilegeNotHeldException or PlatformNotSupportedException)
        {
            return false; // не смогли прочитать ACL — не разрешаем
        }

        var sdHandle = GCHandle.Alloc(sd, GCHandleType.Pinned);
        var granted = Marshal.AllocHGlobal(sizeof(uint));
        var error = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            var request = new AuthzAccessRequest { DesiredAccess = desired };
            var reply = new AuthzAccessReply { ResultListLength = 1, GrantedAccessMask = granted, Error = error };
            if (!AuthzAccessCheck(0, _ctx, ref request, IntPtr.Zero, sdHandle.AddrOfPinnedObject(),
                    IntPtr.Zero, 0, ref reply, IntPtr.Zero))
                return false;
            var grantedMask = (uint)Marshal.ReadInt32(granted);
            return Marshal.ReadInt32(error) == 0 && (grantedMask & desired) == desired;
        }
        finally
        {
            Marshal.FreeHGlobal(error);
            Marshal.FreeHGlobal(granted);
            sdHandle.Free();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ctx != IntPtr.Zero) AuthzFreeContext(_ctx);
        if (_rm != IntPtr.Zero) AuthzFreeResourceManager(_rm);
    }

    // ---- Authz P/Invoke ----

    private const uint AuthzRmFlagNoAudit = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AuthzAccessRequest
    {
        public uint DesiredAccess;
        public IntPtr PrincipalSelfSid;
        public IntPtr ObjectTypeList;
        public uint ObjectTypeListLength;
        public IntPtr OptionalArguments;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AuthzAccessReply
    {
        public uint ResultListLength;
        public IntPtr GrantedAccessMask;
        public IntPtr SaclEvaluationResults;
        public IntPtr Error;
    }

    [DllImport("authz.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool AuthzInitializeResourceManager(uint flags, IntPtr dynamicAccessCheck,
        IntPtr computeDynamicGroups, IntPtr freeDynamicGroups, string? name, out IntPtr resourceManager);

    [DllImport("authz.dll", SetLastError = true)]
    private static extern bool AuthzInitializeContextFromSid(uint flags, byte[] userSid, IntPtr resourceManager,
        IntPtr expirationTime, Luid identifier, IntPtr dynamicGroupArgs, out IntPtr clientContext);

    [DllImport("authz.dll", SetLastError = true)]
    private static extern bool AuthzAccessCheck(uint flags, IntPtr clientContext, ref AuthzAccessRequest request,
        IntPtr auditEvent, IntPtr securityDescriptor, IntPtr optionalSecurityDescriptorArray,
        uint optionalSecurityDescriptorCount, ref AuthzAccessReply reply, IntPtr accessCheckResults);

    [DllImport("authz.dll", SetLastError = true)]
    private static extern bool AuthzFreeContext(IntPtr clientContext);

    [DllImport("authz.dll", SetLastError = true)]
    private static extern bool AuthzFreeResourceManager(IntPtr resourceManager);
}
