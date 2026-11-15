using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Settings;
using Oadm.Client.Shell;
using Oadm.Contracts.Mcp;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>
/// AI agents in the client (docs/spec/mcp.md): the Settings card (Turn on, Port, Listen on, the certificate, Download CA
/// certificate; read only for other roles), the account menu's Access keys (a key shown once, Delete) and every user's keys
/// on the Permissions page. Headless, with screenshots in OADM_SCREENSHOT_DIR.
/// </summary>
public sealed class AiAgentsTests
{
    private static UserSession Session(UserRole role, params string[] permissions)
    {
        var session = new UserSession();
        var user = new UserInfo
        {
            UserName = role == UserRole.Admin ? "admin" : "tech",
            Role = role,
            RoleId = role == UserRole.Admin ? "00000000-0000-0000-0000-000000000001" : "00000000-0000-0000-0000-000000000002",
            RoleName = role == UserRole.Admin ? "Administrator" : "Operator",
            AllPermissions = role == UserRole.Admin,
        };
        user.Permissions.AddRange(permissions);
        session.SignIn(user, "fake");
        return session;
    }

    [Fact]
    public async Task TheCardTurnsTheServerOnSavesThePortAndSavesTheCaCertificate()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        dialogs.SaveFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<FileType>(), Arg.Any<byte[]>()).Returns("oadm-ca.pem");
        var card = new AiAgentsViewModel(api, dialogs, Session(UserRole.Admin));
        await card.LoadAsync();

        Assert.False(card.IsOn);
        Assert.Equal("Off", card.StatusText);
        Assert.True(card.ShowTurnOn);
        Assert.False(card.SaveCommand.CanExecute(null));
        Assert.Equal(AiAgentsViewModel.NoChanges, card.SaveTip);
        Assert.Equal(McpTexts.CertificateOnTurnOn, card.CertificateText); // the first Turn on gives the policy AI agents one
        Assert.False(card.CanDownloadCa);

        await card.TurnOnCommand.ExecuteAsync(null);
        Assert.Equal("MCP Server Certificate (OADM Root CA FAKE), until 2036-11-14", card.CertificateText);
        Assert.Equal("Names: oadm-server.local, 10.0.0.17", card.CertificateHint);
        Assert.True(card.Running);
        Assert.Equal("Running on port 5082", card.StatusText);
        Assert.True(card.ShowTurnOff);

        card.Port = "80";
        Assert.Equal(AiAgentsViewModel.PortRange, card.ErrorOf(nameof(AiAgentsViewModel.Port)));
        Assert.False(card.SaveCommand.CanExecute(null));
        card.Port = "5090";
        Assert.True(card.SaveCommand.CanExecute(null));
        await card.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Running on port 5090", card.StatusText);

        await card.DownloadCaCommand.ExecuteAsync(null);
        await dialogs.Received(1).SaveFileAsync("Download CA certificate", "oadm-ca.pem", Arg.Any<FileType>(), Arg.Is<byte[]>(b => System.Text.Encoding.ASCII.GetString(b).Contains("BEGIN CERTIFICATE", StringComparison.Ordinal)));
        Assert.Equal("Saved oadm-ca.pem.", card.Message);

        await card.TurnOffCommand.ExecuteAsync(null);
        Assert.False(card.IsOn);
    }

    [Fact]
    public async Task WithoutPkiTheCardSaysWhyAndOffersNoTurnOn()
    {
        var api = Substitute.For<IOadmApi>();
        var state = new McpState { Settings = new McpSettings { Port = 5082 }, Status = "Off", PkiAvailable = false };
        api.GetMcpAsync(Arg.Any<CancellationToken>()).Returns(state);
        var card = new AiAgentsViewModel(api, Substitute.For<IDialogService>(), Session(UserRole.Admin));
        await card.LoadAsync();
        Assert.Equal("PKI is not running. The server log says why.", card.CertificateText);
        Assert.False(card.TurnOnCommand.CanExecute(null));
        Assert.Equal("PKI is not running. The server log says why.", card.TurnOnTip);
        Assert.False(card.CanDownloadCa);
    }

    [Fact]
    public async Task AnInterfaceTheServerDoesNotKnowShowsItsErrorDirectlyBelowListenOn()
    {
        var api = Substitute.For<IOadmApi>();
        var state = new McpState { Settings = new McpSettings { Port = 5082 }, Status = McpTexts.Off, PkiAvailable = true };
        state.Interfaces.Add(new McpInterface { Id = "all", Label = "All interfaces", Name = "All interfaces" });
        state.Interfaces.Add(new McpInterface { Id = "eth0", Label = "Ethernet - 10.0.0.17", Name = "Ethernet", Addresses = { "10.0.0.17" } });
        state.Interfaces.Add(new McpInterface { Id = "eth1", Label = "Ethernet 2 - 10.1.0.17", Name = "Ethernet 2", Addresses = { "10.1.0.17" } });
        api.GetMcpAsync(Arg.Any<CancellationToken>()).Returns(state);
        api.SetMcpAsync(Arg.Any<McpSettings>(), Arg.Any<CancellationToken>())
            .Returns<McpState>(_ => throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.InvalidArgument, McpTexts.UnknownInterface)));
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var card = new AiAgentsViewModel(api, Substitute.For<IDialogService>(), Session(UserRole.Admin));
            await card.LoadAsync();
            var window = new Window { Width = 900, Height = 700, Content = new AiAgentsCardView { DataContext = card, Margin = new Avalonia.Thickness(24) } };
            window.Show();
            Pump();
            window.GetVisualDescendants().OfType<Oadm.Client.Controls.CollapsibleCard>().Single().IsExpanded = true;
            Pump();

            card.SelectedInterface = card.Listen.Find("eth0");
            await card.SaveCommand.ExecuteAsync(null);
            Pump();
            Assert.Equal(McpTexts.UnknownInterface, card.ErrorOf(nameof(AiAgentsViewModel.SelectedInterface)));
            Assert.Null(card.Message); // never under the buttons
            Assert.False(card.SaveCommand.CanExecute(null));
            var listenOn = window.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.FormField>().Single(f => f.Label == "Listen on" && f.IsEffectivelyVisible);
            Assert.Contains(McpTexts.UnknownInterface, listenOn.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
            Save(window, outDir, "client-settings-ai-agents-interface-error.png");

            card.SelectedInterface = card.Listen.Find("eth1"); // another choice clears it
            Pump();
            Assert.Null(card.ErrorOf(nameof(AiAgentsViewModel.SelectedInterface)));
            Assert.True(card.SaveCommand.CanExecute(null));
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task TheSettingsPageShowsTheCardToEveryUserAndOnlyAdministratorsChangeIt()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            foreach (var (role, name) in new[] { (UserRole.Admin, "client-page-settings-ai-agents.png"), (UserRole.Operator, "client-page-settings-ai-agents-operator.png") })
            {
                var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
                using var f = new DevicesFixture(api);
                using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerConnection>.Instance);
                var session = Session(role);
                var card = new AiAgentsViewModel(api, Substitute.For<IDialogService>(), session);
                var settings = new SettingsViewModel(f.Api, connection, Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsViewModel>.Instance, session, aiAgents: card);
                await settings.LoadAsync();
                if (role == UserRole.Admin)
                {
                    await card.TurnOnCommand.ExecuteAsync(null);
                }

                var window = new Window { Width = 1520, Height = 1100, Content = new SettingsView { DataContext = settings, Margin = new Avalonia.Thickness(24) } };
                window.Show();
                Pump();
                var view = Assert.Single(window.GetVisualDescendants().OfType<AiAgentsCardView>(), c => c.IsEffectivelyVisible);
                view.GetVisualDescendants().OfType<Oadm.Client.Controls.CollapsibleCard>().Single().IsExpanded = true;
                Pump();
                var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
                Assert.Contains(AiAgentsViewModel.Intro, texts);
                Assert.Equal(role == UserRole.Admin, view.FindControl<Button>("McpSave")!.IsEffectivelyVisible);
                Assert.Equal(role == UserRole.Admin, view.FindControl<TextBox>("McpPort")!.IsEffectivelyVisible);
                Save(window, outDir, name);
                window.Close();
            }

            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task ConnectAnAgentShowsTheConfigurationsOfThisServerWithCopyAndDownloadSkillSavesTheZip()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            using var f = new DevicesFixture(api);
            using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerConnection>.Instance);
            var dialogs = Substitute.For<IDialogService>();
            dialogs.SaveFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<FileType>(), Arg.Any<byte[]>()).Returns("oadm-skill.zip");
            var clipboard = Substitute.For<IClipboardService>();
            clipboard.SetTextAsync(Arg.Any<string>()).Returns(true);
            var session = Session(UserRole.Admin);
            var card = new AiAgentsViewModel(api, dialogs, session, clipboard);
            var settings = new SettingsViewModel(f.Api, connection, Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsViewModel>.Instance, session, aiAgents: card);
            await settings.LoadAsync();
            Assert.False(card.HasConnections); // off: no address to give

            await card.TurnOnCommand.ExecuteAsync(null);
            Assert.True(card.HasConnections);
            Assert.Equal(["Claude Code", ".mcp.json", "VS Code"], card.Connections.Select(c => c.Title));
            var code = card.Connections[0].Text;
            Assert.Equal("claude mcp add --transport http oadm https://oadm-server.local:5082/mcp --header \"Authorization: Bearer ${OADM_KEY}\"", code);
            Assert.All(card.Connections, c => Assert.Contains("https://oadm-server.local:5082/mcp", c.Text, StringComparison.Ordinal));
            Assert.Contains("\"password\": true", card.Connections[2].Text, StringComparison.Ordinal);
            Assert.Contains("\"servers\"", card.Connections[2].Text, StringComparison.Ordinal);
            Assert.All(card.Connections, c => Assert.DoesNotContain("npx", c.Text, StringComparison.OrdinalIgnoreCase));
            Assert.All(card.Connections, c => Assert.DoesNotContain("mcp-remote", c.Text, StringComparison.OrdinalIgnoreCase));
            Assert.All(card.Connections, c => Assert.DoesNotContain("oadm_", c.Text, StringComparison.Ordinal)); // never a key

            await card.CopyCommand.ExecuteAsync(card.Connections[0]);
            await clipboard.Received(1).SetTextAsync(code);
            Assert.Equal("Copied Claude Code.", card.Message);

            await card.DownloadSkillCommand.ExecuteAsync(null);
            await dialogs.Received(1).SaveFileAsync("Download skill", "oadm-skill.zip", Arg.Any<FileType>(), Arg.Is<byte[]>(b => b.Length > 4 && b[0] == (byte)'P' && b[1] == (byte)'K'));
            Assert.Equal("Saved oadm-skill.zip.", card.Message);

            var window = new Window { Width = 1520, Height = 1500, Content = new SettingsView { DataContext = settings, Margin = new Avalonia.Thickness(24) } };
            window.Show();
            Pump();
            var view = Assert.Single(window.GetVisualDescendants().OfType<AiAgentsCardView>(), c => c.IsEffectivelyVisible);
            view.GetVisualDescendants().OfType<Oadm.Client.Controls.CollapsibleCard>().Single().IsExpanded = true;
            Pump();
            Assert.True(view.FindControl<Control>("ConnectAgent")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<Button>("McpDownloadSkill")!.IsEffectivelyVisible);
            Assert.Equal(3, view.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.CodeView>().Count(c => c.IsEffectivelyVisible));
            var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("Connect an agent", texts);
            Save(window, outDir, "client-page-settings-ai-agents-connect.png");
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Theory]
    [InlineData("oadm-server.local|10.0.0.17", "oadm-server.local")]
    [InlineData("10.0.0.17|oadm-server.local", "oadm-server.local")]
    [InlineData("10.0.0.17", "10.0.0.17")]
    [InlineData("fd00::17", "[fd00::17]")]
    [InlineData("", null)]
    public void TheAgentConnectsToANameOfTheCertificate(string names, string? host) =>
        Assert.Equal(host, AgentConnections.HostOf(names.Split('|', StringSplitOptions.RemoveEmptyEntries)));

    [Theory]
    [InlineData("a$(x)", false)]
    [InlineData("quote\"d", false)]
    [InlineData("back`tick", false)]
    [InlineData("semi;colon", false)]
    [InlineData("Laptop 2.0_a-b", true)]
    public async Task AKeyNameAllowsOnlyLettersDigitsSpaceDashUnderscoreAndDotWithTheErrorBelowTheField(string name, bool fine)
    {
        var done = await HeadlessSession.Shared.Dispatch(() =>
        {
            using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            var keys = new AccessKeysViewModel(api, Substitute.For<IDialogService>());
            keys.Name = name;
            Assert.Equal(fine ? null : AccessKeyWords.NameCharacters, keys.ErrorOf(nameof(AccessKeysViewModel.Name)));
            Assert.Equal(fine, keys.CreateCommand.CanExecute(null));
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task ANewKeyIsShownOnceWithCopyAndTheListDeletesKeys()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            var dialogs = Substitute.For<IDialogService>();
            dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
            var clipboard = Substitute.For<IClipboardService>();
            clipboard.SetTextAsync(Arg.Any<string>()).Returns(true);
            var keys = new AccessKeysViewModel(api, dialogs, clipboard);
            await keys.LoadAsync();
            Assert.False(keys.HasKeys);

            var window = new AccessKeysWindow { DataContext = keys };
            window.Show();
            Pump();
            Assert.Null(keys.ErrorOf(nameof(AccessKeysViewModel.Name))); // an untouched form shows no error
            Assert.False(keys.CreateCommand.CanExecute(null));
            Assert.Equal(AccessKeyWords.EnterName, keys.CreateTip);
            Save(window, outDir, "client-access-keys-empty.png");

            keys.Name = "Laptop";
            keys.ReadOnly = true;
            await keys.CreateCommand.ExecuteAsync(null);
            Pump();
            Assert.StartsWith("oadm_", keys.NewKey, StringComparison.Ordinal);
            Assert.True(window.FindControl<Avalonia.Controls.Control>("NewKeyBox")!.IsEffectivelyVisible);
            var row = Assert.Single(keys.Keys);
            Assert.Equal(("Laptop", "Yes", AccessKeyWords.NeverUsed), (row.Name, row.ReadOnlyText, row.LastUsed));
            var grid = window.GetVisualDescendants().OfType<Oadm.Client.Controls.AccessKeyGrid>().Single().GetVisualDescendants().OfType<DataGrid>().Single();
            Assert.False(grid.Columns[0].IsVisible); // the user's own keys: no User column
            Assert.Equal(string.Empty, keys.Name);
            await keys.CopyKeyCommand.ExecuteAsync(null);
            await clipboard.Received(1).SetTextAsync(keys.NewKey!);
            Save(window, outDir, "client-access-keys-created.png");

            // The same name again: the server's refusal below the name.
            keys.Name = "laptop";
            await keys.CreateCommand.ExecuteAsync(null);
            Assert.Equal("You already have an access key with this name.", keys.ErrorOf(nameof(AccessKeysViewModel.Name)));

            await keys.DeleteCommand.ExecuteAsync(row);
            Assert.Empty(keys.Keys);
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task TheAccountMenuOffersAccessKeysOnlyToRolesThatMayUseAiAgents()
    {
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var app = (App)Avalonia.Application.Current!;
            var vm = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<MainWindowViewModel>(app.Services!);
            Assert.True(vm.CanUseAiAgents); // fake mode: the administrator
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 800 };
            window.Show();
            Pump();
            var menu = (MenuFlyout)window.FindControl<Button>("TitleUser")!.Flyout!;
            var items = menu.Items.OfType<MenuItem>().ToList();
            Assert.Equal(["Your role", "Access keys"], items.Select(i => i.Header as string));
            window.Close();
            await Task.CompletedTask;
            return true;
        }, CancellationToken.None);
        Assert.True(done);

        var operatorSession = Session(UserRole.Operator);
        Assert.False(operatorSession.Has(Oadm.Sdk.Plugins.HostPermissionKeys.McpUse));
        Assert.True(Session(UserRole.Operator, Oadm.Sdk.Plugins.HostPermissionKeys.McpUse).Has(Oadm.Sdk.Plugins.HostPermissionKeys.McpUse));
    }

    [Fact]
    public async Task ThePermissionsPageListsEveryUsersKeysAndDeletesOne()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            await api.CreateAccessKeyAsync("Build server", 365, readsOnly: false, CancellationToken.None);
            var dialogs = Substitute.For<IDialogService>();
            dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
            var session = Session(UserRole.Admin);
            var all = new AllAccessKeysViewModel(api, dialogs);
            var users = new UsersViewModel(api, dialogs, session, Microsoft.Extensions.Logging.Abstractions.NullLogger<UsersViewModel>.Instance, accessKeys: all);
            await users.LoadAsync();
            Assert.Equal(["admin", "tech2"], all.Keys.Select(k => k.UserName));

            var window = new Window { Width = 1700, Height = 1000, Content = new UsersPageView { DataContext = users, Margin = new Avalonia.Thickness(24) } };
            window.Show();
            Pump();
            Assert.True(window.GetVisualDescendants().OfType<AccessKeysCardView>().Single().IsEffectivelyVisible);
            var grid = window.GetVisualDescendants().OfType<Oadm.Client.Controls.AccessKeyGrid>().Single().GetVisualDescendants().OfType<DataGrid>().Single();
            Assert.Equal("User", grid.Columns[0].Header);
            Assert.True(grid.Columns[0].IsVisible); // every user's keys: the User column
            Save(window, outDir, "client-page-permissions-access-keys.png");

            await all.DeleteCommand.ExecuteAsync(all.Keys.Single(k => k.UserName == "tech2"));
            Assert.Equal(["admin"], all.Keys.Select(k => k.UserName));
            await dialogs.Received(1).ConfirmAsync("Delete access key", Arg.Is<string>(m => m.Contains("Laptop of tech2", StringComparison.Ordinal)), "Delete");
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Save(Window window, string? outDir, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
