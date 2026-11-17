namespace Oadm.Client.Discovery;

/// <summary>The colour of a part of the add page's info line: the colour of the login status it counts.</summary>
public enum StatusTone
{
    Neutral,
    Ok,
    Warning,
    Error,
}

/// <summary>One part of the add page's info line ("3 ready to add"); <see cref="IsFirst"/> has no " · " before it.</summary>
public sealed record StatusPart(string Text, StatusTone Tone, bool IsFirst)
{
    public bool IsOk => Tone == StatusTone.Ok;
    public bool IsWarning => Tone == StatusTone.Warning;
    public bool IsError => Tone == StatusTone.Error;
}
