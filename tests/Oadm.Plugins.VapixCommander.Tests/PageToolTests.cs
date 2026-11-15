using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Plugins;

using SdkTesting = Oadm.Sdk.Testing;

namespace Oadm.Plugins.VapixCommander.Tests;

/// <summary>
/// The saved commands as tools (docs/spec/mcp.md "Plugins", decision 2026-11-15): Save, Delete and Import as a preview and a run
/// with the page's checks and rights, Export as a read; no password passes a tool.
/// </summary>
public sealed class PageToolTests
{
    private static string LibraryDirectory => Path.Combine(AppContext.BaseDirectory, "Library");

    private static async Task<(SdkTesting.FakeCoreContext Ctx, VapixCommanderPlugin Plugin)> StartAsync()
    {
        var ctx = new SdkTesting.FakeCoreContext();
        var plugin = new VapixCommanderPlugin(LibraryDirectory);
        await plugin.StartAsync(ctx, CancellationToken.None);
        return (ctx, plugin);
    }

    private static string CommandJson(string name, string? id = null)
    {
        var command = Samples.Brand;
        command.Id = id ?? string.Empty;
        command.Name = name;
        command.Category = string.Empty;
        return JsonSerializer.Serialize(command, VapixCommander.CommandJson.Strict);
    }

    private static async Task<JsonObject> CallAsync(SdkTesting.FakeCoreContext ctx, VapixCommanderPlugin plugin, ICallContext call, string method, JsonObject payload) =>
        JsonNode.Parse((await ctx.InvokeAsync(plugin, call, method, payload.ToJsonString()))!)!.AsObject();

    private static string Stored(SdkTesting.FakeCoreContext ctx) =>
        string.Join('\n', ctx.PluginSettings.Values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));

    [Fact]
    public void The_tools_have_the_rights_of_the_page_methods_they_wrap()
    {
        ICorePlugin core = new VapixCommanderPlugin(LibraryDirectory);
        foreach (var (tool, page) in new[] { ("saved_save", CommanderMethods.Save), ("saved_delete", CommanderMethods.Delete), ("saved_import", CommanderMethods.Import) })
        {
            foreach (var method in new[] { "tool." + tool, "tool." + tool + ".preview" })
            {
                Assert.Equal("saved", core.RequiredPermission(method));
                Assert.Equal(core.RequiredPermission(page), core.RequiredPermission(method));
                Assert.True(core.RequiresAllDevices(method));
                Assert.Equal(core.IsAudited(page), core.IsAudited(method));
            }
        }

        Assert.Equal(PermissionKeys.Show, core.RequiredPermission("tool.saved_export"));
        Assert.False(core.RequiresAllDevices("tool.saved_export"));
        Assert.Equal("rollout", core.RequiredPermission(CommanderTools.CommandRun.Method)); // the existing tool keeps its right
        Assert.True(CommanderPageTools.Set.ToolOf("tool.saved_delete")!.Destructive);
        Assert.Contains(core.Tools, t => t.Name == "saved_save" && t.Changes);
    }

    [Fact]
    public async Task Save_previews_with_the_pages_checks_writes_once_and_Delete_removes_it()
    {
        var (ctx, plugin) = await StartAsync();
        var admin = ctx.CallAsAllDevices();
        var before = Stored(ctx);

        var plan = await CallAsync(ctx, plugin, admin, "tool.saved_save.preview", new JsonObject { ["command"] = CommandJson("Brand of the lobby") });
        Assert.Equal("create", plan["change"]!.GetValue<string>());
        Assert.Equal("custom.brand-of-the-lobby", plan["id"]!.GetValue<string>());
        Assert.Equal(1, plan["will_change"]!.GetValue<int>());
        Assert.Equal("saved command", plan["unit"]!.GetValue<string>());
        Assert.Equal(before, Stored(ctx)); // the preview writes nothing

        var saved = await CallAsync(ctx, plugin, admin, "tool.saved_save", new JsonObject { ["command"] = CommandJson("Brand of the lobby") });
        Assert.Equal("custom.brand-of-the-lobby", saved["id"]!.GetValue<string>());
        var item = Assert.Single(await plugin.SavedStore.ListAsync(CancellationToken.None));
        Assert.Equal("admin", item.UpdatedBy);

        var replace = await CallAsync(ctx, plugin, admin, "tool.saved_save.preview", new JsonObject { ["command"] = CommandJson("Brand of the lobby", "custom.brand-of-the-lobby") });
        Assert.Equal("replace", replace["change"]!.GetValue<string>());

        var delete = await CallAsync(ctx, plugin, admin, "tool.saved_delete.preview", new JsonObject { ["id"] = "custom.brand-of-the-lobby" });
        Assert.Equal("Brand of the lobby", delete["name"]!.GetValue<string>());
        Assert.Single(await plugin.SavedStore.ListAsync(CancellationToken.None));
        await CallAsync(ctx, plugin, admin, "tool.saved_delete", new JsonObject { ["id"] = "custom.brand-of-the-lobby" });
        Assert.Empty(await plugin.SavedStore.ListAsync(CancellationToken.None));
        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.InvokeAsync(plugin, admin, "tool.saved_delete", """{"id":"custom.brand-of-the-lobby"}"""));
        Assert.Equal(CommanderPageTools.Gone, again.Message);
    }

    [Fact]
    public async Task Invalid_commands_and_passwords_are_refused_with_field_errors_and_nothing_is_written()
    {
        var (ctx, plugin) = await StartAsync();
        var admin = ctx.CallAsAllDevices();
        var before = Stored(ctx);

        var shortName = await CallAsync(ctx, plugin, admin, "tool.saved_save", new JsonObject { ["command"] = CommandJson("ab") });
        Assert.Equal("command", shortName["errors"]![0]!["field"]!.GetValue<string>());
        Assert.Contains("3 to 80", shortName["errors"]![0]!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        var broken = await CallAsync(ctx, plugin, admin, "tool.saved_save", new JsonObject { ["command"] = """{"name":"x","what":1}""" });
        Assert.Equal("command", broken["errors"]![0]!["field"]!.GetValue<string>());

        var withPassword = Samples.Brand;
        withPassword.Id = string.Empty;
        withPassword.Name = "Brand with a password";
        withPassword.Fields.Add(new CommandField { Name = "pass", Label = "Password", Type = FieldTypes.Password, Default = JsonSerializer.SerializeToElement("secret1") });
        var refused = await CallAsync(ctx, plugin, admin, "tool.saved_save", new JsonObject { ["command"] = JsonSerializer.Serialize(withPassword, VapixCommander.CommandJson.Strict) });
        Assert.Equal(CommanderPageTools.NoPasswords, refused["errors"]![0]!["error"]!.GetValue<string>());

        var unknownPrevious = await CallAsync(ctx, plugin, admin, "tool.saved_save.preview", new JsonObject { ["command"] = CommandJson("Brand two"), ["previous_id"] = "custom.none" });
        Assert.Equal("previous_id", unknownPrevious["errors"]![0]!["field"]!.GetValue<string>());
        var badFile = await CallAsync(ctx, plugin, admin, "tool.saved_import.preview", new JsonObject { ["file"] = "{not json" });
        Assert.Equal("file", badFile["errors"]![0]!["field"]!.GetValue<string>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.InvokeAsync(plugin, admin, "tool.saved_delete.preview", """{"id":"custom.none"}"""));
        Assert.Equal(before, Stored(ctx));
    }

    [Fact]
    public async Task Import_previews_the_commands_of_a_file_and_Export_answers_the_file_without_secrets()
    {
        var (ctx, plugin) = await StartAsync();
        var admin = ctx.CallAsAllDevices();
        var one = Samples.Brand;
        one.Id = "custom.brand-a";
        one.Name = "Brand A";
        one.Category = CommandCategories.Custom;
        var community = Samples.Brand;
        community.Id = "custom.brand-b";
        community.Name = "Brand B";
        community.Category = CommandCategories.Custom;
        community.Request.Query = new Dictionary<string, string>(community.Request.Query ?? [], StringComparer.Ordinal) { ["community"] = "hidden-community" };
        var invalid = Samples.Brand;
        invalid.Id = "custom.x";
        invalid.Name = "x";
        var file = VapixCommander.CommandJson.WriteLibraryFile(new LibraryFile { FormatVersion = 1, Category = CommandCategories.Custom, Commands = [one, community, invalid] });
        var before = Stored(ctx);

        var plan = await CallAsync(ctx, plugin, admin, "tool.saved_import.preview", new JsonObject { ["file"] = file });
        Assert.Equal(2, plan["will_change"]!.GetValue<int>());
        Assert.Single(plan["problems"]!.AsArray());
        Assert.Equal(before, Stored(ctx));
        var done = await CallAsync(ctx, plugin, admin, "tool.saved_import", new JsonObject { ["file"] = file });
        Assert.Equal(2, done["imported"]!.GetValue<int>());

        var export = await CallAsync(ctx, plugin, admin, "tool.saved_export", new JsonObject());
        Assert.Equal(2, export["count"]!.GetValue<int>());
        var text = export["file"]!.GetValue<string>();
        Assert.DoesNotContain("hidden-community", text, StringComparison.Ordinal);
        Assert.Contains("***", text, StringComparison.Ordinal);
        Assert.Equal(2, VapixCommander.CommandJson.ParseLibraryFile(text).Commands.Count);
        var unknown = await CallAsync(ctx, plugin, admin, "tool.saved_export", new JsonObject { ["ids"] = new JsonArray("custom.none") });
        Assert.Equal("ids", unknown["errors"]![0]!["field"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_group_user_may_export_but_never_change_the_shared_commands()
    {
        var (ctx, plugin) = await StartAsync();
        var group = ctx.CallAsGroup([]);
        await Assert.ThrowsAnyAsync<Exception>(() => ctx.InvokeAsync(plugin, group, "tool.saved_save.preview", new JsonObject { ["command"] = CommandJson("Brand of the lobby") }.ToJsonString()));
        Assert.Empty(await plugin.SavedStore.ListAsync(CancellationToken.None));
        var export = await CallAsync(ctx, plugin, group, "tool.saved_export", new JsonObject());
        Assert.Equal(0, export["count"]!.GetValue<int>());
    }
}
