using System.Text.Json;
using System.Text.Json.Nodes;

namespace LolConfigSync.Core.Model;

/// <summary>One account's own values, keyed by <see cref="SettingKey.ToString"/>.</summary>
public sealed class AccountOverride
{
    public string RiotId { get; set; } = "";
    public Dictionary<string, JsonNode?> Values { get; set; } = [];
}

/// <summary>
/// Per-account settings: values that apply to one account on top of the main setup.
/// They are pushed even when the setting isn't otherwise synced, and in-game changes to them
/// update the account's value instead of the main setup.
/// </summary>
public sealed class AccountOverrides
{
    /// <summary>By account id (puuid).</summary>
    public Dictionary<string, AccountOverride> Accounts { get; set; } = [];

    public JsonNode? Get(string puuid, SettingKey key) =>
        Accounts.TryGetValue(puuid, out var account) && account.Values.TryGetValue(key.ToString(), out var value)
            ? value?.DeepClone()
            : null;

    public bool Has(string puuid, SettingKey key) =>
        Accounts.TryGetValue(puuid, out var account) && account.Values.ContainsKey(key.ToString());

    public void Set(string puuid, string riotId, SettingKey key, JsonNode? value)
    {
        if (!Accounts.TryGetValue(puuid, out var account))
        {
            account = new AccountOverride();
            Accounts[puuid] = account;
        }
        if (!string.IsNullOrWhiteSpace(riotId)) account.RiotId = riotId;
        account.Values[key.ToString()] = value?.DeepClone();
    }

    public void Remove(string puuid, SettingKey key)
    {
        if (!Accounts.TryGetValue(puuid, out var account)) return;
        account.Values.Remove(key.ToString());
        if (account.Values.Count == 0) Accounts.Remove(puuid);
    }

    public IEnumerable<SettingKey> KeysFor(string puuid, SettingsKind kind) =>
        Accounts.TryGetValue(puuid, out var account)
            ? account.Values.Keys.Select(SettingKey.Parse).Where(k => k.Kind == kind)
            : [];

    /// <summary>A copy of <paramref name="settings"/> with this account's own values of <paramref name="kind"/> applied.</summary>
    public JsonObject ApplyTo(JsonObject settings, SettingsKind kind, string? puuid)
    {
        var result = (JsonObject)settings.DeepClone();
        if (puuid is null) return result;
        foreach (var key in KeysFor(puuid, kind))
        {
            if (result[key.Section] is not JsonObject section)
            {
                section = [];
                result[key.Section] = section;
            }
            section[key.Name] = Get(puuid, key);
        }
        return result;
    }

    public AccountOverrides DeepClone() =>
        JsonSerializer.Deserialize<AccountOverrides>(JsonSerializer.Serialize(this))!;
}
