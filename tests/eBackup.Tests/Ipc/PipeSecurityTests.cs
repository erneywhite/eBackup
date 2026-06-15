using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using eBackup.Ipc.Server;
using Xunit;

namespace eBackup.Tests.Ipc;

public class PipeSecurityTests
{
    [Fact]
    public void Dacl_Is_Locked_To_System_Admins_Interactive_Only()
    {
        var ps = PipeSecurityFactory.Create();

        Assert.True(ps.AreAccessRulesProtected); // наследование выключено
        Assert.Equal("S-1-5-18", ((SecurityIdentifier)ps.GetOwner(typeof(SecurityIdentifier))!).Value);

        var rules = ps.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        PipeAccessRule? Find(string sid, AccessControlType t) =>
            rules.FirstOrDefault(r => ((SecurityIdentifier)r.IdentityReference).Value == sid && r.AccessControlType == t);

        Assert.Equal(PipeAccessRights.FullControl, Find("S-1-5-18", AccessControlType.Allow)!.PipeAccessRights);
        Assert.Equal(PipeAccessRights.FullControl, Find("S-1-5-32-544", AccessControlType.Allow)!.PipeAccessRights);

        var iu = Find("S-1-5-4", AccessControlType.Allow)!;            // INTERACTIVE — ограниченно
        Assert.False(iu.PipeAccessRights.HasFlag(PipeAccessRights.FullControl));
        Assert.False(iu.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance));

        Assert.NotNull(Find("S-1-5-2", AccessControlType.Deny));        // NETWORK запрещён
        Assert.NotNull(Find("S-1-5-7", AccessControlType.Deny));        // ANONYMOUS запрещён

        // Никаких широких SID (Everyone / Authenticated Users).
        Assert.DoesNotContain(rules, r => ((SecurityIdentifier)r.IdentityReference).Value is "S-1-1-0" or "S-1-5-11");
    }

    [Fact]
    public void Sddl_Excludes_Everyone_And_AuthenticatedUsers()
    {
        var sddl = PipeSecurityFactory.Sddl();
        Assert.StartsWith("O:SY", sddl);       // владелец SYSTEM
        Assert.Contains("D:P", sddl);          // DACL protected
        Assert.DoesNotContain(";;;WD)", sddl); // Everyone
        Assert.DoesNotContain(";;;AU)", sddl); // Authenticated Users
    }
}
