using Oadm.Plugins.Pki.Certificates;
using Oadm.Plugins.Pki.Tools;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>Who may call the methods of the tools (docs/spec/mcp.md "Plugins"): the reads like the page's, Create like the page's Create.</summary>
public sealed class ToolTests
{
    [Fact]
    public void The_reads_are_for_operators_and_Create_needs_the_rights_of_the_page_with_a_preview_that_leaves_no_audit_entry()
    {
        ICorePlugin plugin = new PkiPlugin();
        Assert.Equal(["certificates", "policies", "certificate_create"], plugin.Tools.Select(t => t.Name));
        Assert.Equal("pki", plugin.ToolArea ?? "pki");
        foreach (var read in new[] { PkiTools.Certificates.Method, PkiTools.Policies.Method })
        {
            Assert.Equal(UserRole.Operator, plugin.RequiredRole(read));
            Assert.False(plugin.IsAudited(read));
            Assert.False(plugin.RequiresAllDevices(read)); // a group reads its own devices' certificates
        }

        var create = PkiTools.CertificateCreate;
        foreach (var method in new[] { create.PreviewMethod, create.RunMethod })
        {
            Assert.Equal(UserRole.Admin, plugin.RequiredRole(method));
            Assert.True(plugin.RequiresAllDevices(method));
            Assert.Null(plugin.RequiredPermission(method)); // "Manage the certificate authority", like the page's Create
        }

        Assert.False(plugin.IsAudited(create.PreviewMethod)); // it only checks
        Assert.True(plugin.IsAudited(create.RunMethod));
        Assert.True(create.Changes);
        Assert.Equal(UserRole.Admin, plugin.RequiredRole(CertificateMethods.Create));
    }
}
