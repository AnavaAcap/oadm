namespace Oadm.Plugins.VpnFleet.Client;

/// <summary>
/// An OpenVPN profile the user chose: its name, its size and its text; <see cref="Text"/> is null when the file is larger
/// than a profile can be (it is not read).
/// </summary>
public sealed record ProfileFile(string Name, long Length, string? Text)
{
    /// <summary>Why the file cannot be the profile, or null.</summary>
    public string? Problem => Text is null || Length > ConnectRules.MaxProfileLength
        ? ConnectRules.ProfileTooLarge
        : ConnectRules.ProfileProblem(Text);
}
