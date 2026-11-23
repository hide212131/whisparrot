using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.Tests.Hane;

public sealed class HaneSettingsTests
{
    private static SettingsLoadResult Load(string json) => SettingsJson.Parse(json, DateTimeOffset.UtcNow);

    [Fact]
    public void Default_is_off_with_raw_first_on_and_the_default_writes_no_new_key()
    {
        Assert.Equal(new HaneSettings(false, true), AppSettings.Default.Hane);
        var text = System.Text.Encoding.UTF8.GetString(SettingsJson.Serialize(AppSettings.Default));
        Assert.DoesNotContain("hane", text);
    }

    [Fact]
    public void A_config_without_the_key_loads_with_the_defaults()
    {
        var r = Load(System.Text.Encoding.UTF8.GetString(SettingsJson.Serialize(AppSettings.Default)));
        Assert.Equal(SettingsLoadStatus.Loaded, r.Status);
        Assert.Equal(HaneSettings.Default, r.Settings.Hane);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void The_choices_round_trip(bool enabled, bool rawFirst)
    {
        var s = AppSettings.Default with { Hane = new HaneSettings(enabled, rawFirst) };
        var r = Load(System.Text.Encoding.UTF8.GetString(SettingsJson.Serialize(s)));
        Assert.Equal(SettingsLoadStatus.Loaded, r.Status);
        Assert.Equal(new HaneSettings(enabled, rawFirst), r.Settings.Hane);
    }

    [Fact]
    public void Only_the_enabled_flag_may_be_given_and_the_other_keeps_its_default()
    {
        var json = System.Text.Encoding.UTF8.GetString(SettingsJson.Serialize(AppSettings.Default)).TrimEnd().TrimEnd('}') + ",\"hane\":{\"enabled\":true}}";
        var r = Load(json);
        Assert.Equal(SettingsLoadStatus.Loaded, r.Status);
        Assert.Equal(new HaneSettings(true, true), r.Settings.Hane);
    }

    [Theory]
    [InlineData("{\"enabled\":\"yes\"}")]
    [InlineData("{\"unknown\":true}")]
    [InlineData("true")]
    public void Wrong_types_and_unknown_keys_are_rejected_with_the_path(string hane)
    {
        var json = System.Text.Encoding.UTF8.GetString(SettingsJson.Serialize(AppSettings.Default)).TrimEnd().TrimEnd('}') + ",\"hane\":" + hane + "}";
        var r = Load(json);
        Assert.Equal(SettingsLoadStatus.Invalid, r.Status);
        Assert.Contains(r.Issues, i => i.Path.StartsWith("hane"));
    }

    [Fact]
    public void The_link_follows_the_current_settings_per_session()
    {
        var current = AppSettings.Default;
        var link = new Whisparrot.Infrastructure.Hane.SettingsHaneLink(() => current, new Whisparrot.Infrastructure.Hane.HaneClient());
        Assert.IsType<Whisparrot.Core.Hane.HaneSetup.Off>(link.Prepare());
        current = current with { Hane = new HaneSettings(true, false) };
        var ready = Assert.IsType<Whisparrot.Core.Hane.HaneSetup.Ready>(link.Prepare());
        Assert.False(ready.InsertRawFirst);
    }

    [Fact]
    public void Context_sending_is_off_by_default_and_the_key_is_written_only_when_it_is_on()
    {
        Assert.False(HaneSettings.Default.SendContextToPolish);

        // 連携 ON でも文脈送信は既定 OFF。キーは書かない（旧版が未知キーとして拒否しないため、config.json のスキーマは変えない）。
        var off = System.Text.Encoding.UTF8.GetString(SettingsJson.Serialize(AppSettings.Default with { Hane = new HaneSettings(true, true) }));
        Assert.Contains("\"hane\"", off);
        Assert.DoesNotContain("send_context_to_polish", off);

        var on = System.Text.Encoding.UTF8.GetString(SettingsJson.Serialize(AppSettings.Default with { Hane = new HaneSettings(true, false, true) }));
        Assert.Contains("\"send_context_to_polish\": true", on.Replace("\":true", "\": true"));
        var r = Load(on);
        Assert.Equal(SettingsLoadStatus.Loaded, r.Status);
        Assert.Equal(new HaneSettings(true, false, true), r.Settings.Hane);
    }

    [Fact]
    public void A_file_written_by_the_previous_version_loads_with_context_sending_off()
    {
        var json = System.Text.Encoding.UTF8.GetString(SettingsJson.Serialize(AppSettings.Default)).TrimEnd().TrimEnd('}') + ",\"hane\":{\"enabled\":true,\"insert_raw_first\":false}}";
        var r = Load(json);
        Assert.Equal(SettingsLoadStatus.Loaded, r.Status);
        Assert.Equal(new HaneSettings(true, false, false), r.Settings.Hane);
    }

    [Fact]
    public void The_link_allows_context_only_while_both_the_integration_and_the_context_choice_are_on()
    {
        var current = AppSettings.Default with { Hane = new HaneSettings(true, true, true) };
        var link = new Whisparrot.Infrastructure.Hane.SettingsHaneLink(() => current, new Whisparrot.Infrastructure.Hane.HaneClient());
        var ready = Assert.IsType<Whisparrot.Core.Hane.HaneSetup.Ready>(link.Prepare());
        Assert.True(ready.ContextAllowed());
        current = current with { Hane = new HaneSettings(true, true, false) }; // 途中で OFF
        Assert.False(ready.ContextAllowed());
        current = current with { Hane = new HaneSettings(false, true, true) };
        Assert.False(ready.ContextAllowed());
    }
}
