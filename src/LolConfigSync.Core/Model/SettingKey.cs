namespace LolConfigSync.Core.Model;

/// <summary>Identifies one setting, e.g. <c>game:HUD.GlobalScale</c>.</summary>
public readonly record struct SettingKey(SettingsKind Kind, string Section, string Name)
{
    public override string ToString() => $"{Kind.ToString().ToLowerInvariant()}:{Section}.{Name}";

    public static SettingKey Parse(string text)
    {
        var colon = text.IndexOf(':');
        var dot = colon < 0 ? -1 : text.IndexOf('.', colon + 1);
        if (colon <= 0 || dot <= colon + 1 || dot == text.Length - 1
            || !Enum.TryParse<SettingsKind>(text[..colon], ignoreCase: true, out var kind))
        {
            throw new FormatException($"Not a setting key: '{text}'");
        }
        return new SettingKey(kind, text[(colon + 1)..dot], text[(dot + 1)..]);
    }
}
