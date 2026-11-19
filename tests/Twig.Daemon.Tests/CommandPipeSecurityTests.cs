using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Bench.Server;
using Shouldly;
using Xunit;

namespace Twig.Daemon.Tests;

public sealed class CommandPipeSecurityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnerMetadataDoesNotBroadenUserOnlyDacl(bool distinctOwner)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User.ShouldNotBeNull();
        var owner = distinctOwner
            ? new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
            : identity.Owner.ShouldNotBeNull();
        if (distinctOwner) (owner != user).ShouldBeTrue("fixture must distinguish Token.Owner from Token.User");

        var security = CommandListener.WindowsSecurity(user, owner);

        (security.GetOwner(typeof(SecurityIdentifier)) == owner).ShouldBeTrue();
        AssertRestrictedDacl(security, user);
    }

    [Fact]
    public void RealListenerUsesTokenOwnerAndRetainsFirstInstanceAndRestrictedDacl()
    {
        if (!OperatingSystem.IsWindows()) return;
        var name = "twig-command-security-test-" + Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();
        using var server = CommandListener.Open(name, first: true);

        var security = server.GetAccessControl();
        (security.GetOwner(typeof(SecurityIdentifier)) == identity.Owner).ShouldBeTrue();
        AssertRestrictedDacl(security, identity.User.ShouldNotBeNull());
        var collision = Record.Exception(() => CommandListener.Open(name, first: true).Dispose());
        (collision is IOException or UnauthorizedAccessException).ShouldBeTrue();
    }

    [SupportedOSPlatform("windows")]
    private static void AssertRestrictedDacl(PipeSecurity security, SecurityIdentifier user)
    {
        security.AreAccessRulesProtected.ShouldBeTrue();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
        rules.Length.ShouldBe(2);
        var allow = rules.Single(rule => rule.AccessControlType == AccessControlType.Allow);
        (allow.IdentityReference == user).ShouldBeTrue();
        allow.PipeAccessRights.ShouldBe(PipeAccessRights.FullControl);
        allow.IsInherited.ShouldBeFalse();
        var deny = rules.Single(rule => rule.AccessControlType == AccessControlType.Deny);
        (deny.IdentityReference == new SecurityIdentifier(WellKnownSidType.NetworkSid, null)).ShouldBeTrue();
        deny.PipeAccessRights.ShouldBe(PipeAccessRights.FullControl);
        deny.IsInherited.ShouldBeFalse();
    }
}
