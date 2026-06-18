using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace eBackup.Ipc.Server;

/// <summary>
/// Строит DACL named-pipe службы и (на S4) сам серверный стрим. Граница привилегий:
/// SYSTEM+Администраторы — полный доступ; INTERACTIVE — только подключиться/читать/писать
/// (НЕ создавать новые инстансы); NETWORK и ANONYMOUS — явный запрет; Everyone/Authenticated
/// Users не упоминаются вовсе. Владелец — SYSTEM, наследование выключено.
/// </summary>
public static class PipeSecurityFactory
{
    public const string DefaultPipeName = "eBackup.service.v1";

    // Явные SID-строки (надёжнее, чем гадать имена WellKnownSidType).
    private const string SidSystem = "S-1-5-18";
    private const string SidAdministrators = "S-1-5-32-544";
    private const string SidInteractive = "S-1-5-4";
    private const string SidNetwork = "S-1-5-2";
    private const string SidAnonymous = "S-1-5-7";

    public static PipeSecurity Create()
    {
        var system = new SecurityIdentifier(SidSystem);
        var admins = new SecurityIdentifier(SidAdministrators);
        var interactive = new SecurityIdentifier(SidInteractive);
        var network = new SecurityIdentifier(SidNetwork);
        var anonymous = new SecurityIdentifier(SidAnonymous);

        var ps = new PipeSecurity();
        // Владельца НЕ задаём явно: в проде создатель = LocalSystem → владелец = SYSTEM сам по себе,
        // а явный SetOwner(SYSTEM) требует привилегии и ломал бы консольную отладку под обычным юзером.
        // Граница — это DACL ниже; клиент в проде дополнительно проверяет owner пайпа == SYSTEM.
        ps.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        // Запреты (приоритетнее любых allow).
        ps.AddAccessRule(new PipeAccessRule(network, PipeAccessRights.FullControl, AccessControlType.Deny));
        ps.AddAccessRule(new PipeAccessRule(anonymous, PipeAccessRights.FullControl, AccessControlType.Deny));

        // Разрешения.
        ps.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(admins, PipeAccessRights.FullControl, AccessControlType.Allow));
        // INTERACTIVE: подключиться + читать/писать, но НЕ CreateNewInstance.
        ps.AddAccessRule(new PipeAccessRule(interactive, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        return ps;
    }

    /// <summary>SDDL построенного дескриптора (для golden-теста против расширения прав).</summary>
    public static string Sddl() => Create().GetSecurityDescriptorSddlForm(AccessControlSections.All);

    /// <summary>
    /// Создать серверный стрим с этим DACL. На ПЕРВОМ инстансе — FirstPipeInstance: служба падает,
    /// если имя уже занято (защита от сквоттинга). Последующие инстансы accept-цикла создаются без него.
    /// </summary>
    public static NamedPipeServerStream CreateServerStream(string pipeName = DefaultPipeName, bool firstInstance = true)
        => NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: Create());
}
