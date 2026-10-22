using System.Text.Json;
using Xunit;

// Feature 9: a rule is data from fixed lists. Anything outside the lists is
// rejected, and nothing in the schema can express a system change.
public sealed class RuleSchemaTests
{
    private static RuleDefinition Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        return RuleSchema.ReadDefinition(document.RootElement.Clone());
    }

    public static IEnumerable<object[]> ValidRules() => new[]
    {
        """{"trigger":{"type":"device.connected","kind":"audio"},"actions":[{"type":"app.open","appId":"spotify"}]}""",
        """{"trigger":{"type":"device.disconnected","kind":"usb","name":"keyboard"},"actions":[{"type":"notify","text":"Keyboard gone"}]}""",
        """{"trigger":{"type":"device.connected","kind":"display"},"actions":[{"type":"setting.set","key":"wallpaper","value":"aurora"}]}""",
        """{"trigger":{"type":"time.daily","time":"08:30"},"actions":[{"type":"backup.run","profile":"home"}]}""",
        """{"trigger":{"type":"time.daily","time":"23:59","days":["sun","mon"]},"actions":[{"type":"backup.run"}]}""",
        """{"trigger":{"type":"app.opened","appId":"org.gnome.Calendar"},"actions":[{"type":"setting.set","key":"do-not-disturb","value":true}]}""",
        """{"trigger":{"type":"network.joined","ssid":"Home Wi-Fi"},"actions":[{"type":"setting.set","key":"volume","value":30}]}""",
        """{"trigger":{"type":"power.plugged"},"actions":[{"type":"setting.set","key":"night-light","value":false}]}""",
        """{"trigger":{"type":"power.unplugged"},"conditions":[],"actions":[{"type":"setting.set","key":"notifications.volume","value":0}]}""",
        """{"trigger":{"type":"power.unplugged"},"conditions":[{"type":"time.between","from":"22:00","to":"06:00"},{"type":"time.days","days":["sat"]},{"type":"power.is","state":"unplugged"}],"actions":[{"type":"notify","text":"x"}]}""",
    }.Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(ValidRules))]
    public void EveryTriggerActionAndConditionInTheListsIsAccepted(string json)
    {
        var rule = Read(json);
        // Round-trips through storage form unchanged.
        Assert.Equal(RuleSchema.Serialize(rule), RuleSchema.Serialize(RuleSchema.Deserialize(RuleSchema.Serialize(rule))));
        Assert.False(string.IsNullOrWhiteSpace(RuleSchema.Describe(rule)));
    }

    public static IEnumerable<object[]> InvalidRules() => new[]
    {
        // Unknown triggers, conditions and actions.
        """{"trigger":{"type":"file.changed","path":"/etc"},"actions":[{"type":"notify","text":"x"}]}""",
        """{"trigger":{"type":"power.plugged"},"conditions":[{"type":"battery.below","percent":20}],"actions":[{"type":"notify","text":"x"}]}""",
        """{"trigger":{"type":"power.plugged"},"actions":[{"type":"http.post","url":"https://evil.example"}]}""",
        // Settings outside the allow-list or with the wrong value type.
        """{"trigger":{"type":"power.plugged"},"actions":[{"type":"setting.set","key":"wifi","value":false}]}""",
        """{"trigger":{"type":"power.plugged"},"actions":[{"type":"setting.set","key":"services.openssh.enable","value":true}]}""",
        """{"trigger":{"type":"power.plugged"},"actions":[{"type":"setting.set","key":"volume","value":101}]}""",
        """{"trigger":{"type":"power.plugged"},"actions":[{"type":"setting.set","key":"volume","value":"50"}]}""",
        """{"trigger":{"type":"power.plugged"},"actions":[{"type":"setting.set","key":"volume","value":50.5}]}""",
        """{"trigger":{"type":"power.plugged"},"actions":[{"type":"setting.set","key":"night-light","value":1}]}""",
        """{"trigger":{"type":"power.plugged"},"actions":[{"type":"setting.set","key":"wallpaper","value":"../../etc/shadow"}]}""",
        """{"trigger":{"type":"power.plugged"},"actions":[{"type":"setting.set","key":"wallpaper","value":"/home/me/x.png"}]}""",
        // App ids cannot carry shell syntax or paths.
        """{"trigger":{"type":"power.plugged"},"actions":[{"type":"app.open","appId":"spotify; rm -rf ~"}]}""",
        """{"trigger":{"type":"power.plugged"},"actions":[{"type":"app.open","appId":"/usr/bin/spotify"}]}""",
        """{"trigger":{"type":"power.plugged"},"actions":[{"type":"app.open","appId":"$(reboot)"}]}""",
        """{"trigger":{"type":"power.plugged"},"actions":[{"type":"backup.run","profile":"../root"}]}""",
        // Bad trigger fields.
        """{"trigger":{"type":"time.daily","time":"24:00"},"actions":[{"type":"notify","text":"x"}]}""",
        """{"trigger":{"type":"time.daily","time":"8:30"},"actions":[{"type":"notify","text":"x"}]}""",
        """{"trigger":{"type":"time.daily","time":"08:30","days":[]},"actions":[{"type":"notify","text":"x"}]}""",
        """{"trigger":{"type":"time.daily","time":"08:30","days":["mon","mon"]},"actions":[{"type":"notify","text":"x"}]}""",
        """{"trigger":{"type":"time.daily","time":"08:30","days":["funday"]},"actions":[{"type":"notify","text":"x"}]}""",
        """{"trigger":{"type":"device.connected","kind":"bluetooth"},"actions":[{"type":"notify","text":"x"}]}""",
        """{"trigger":{"type":"device.connected"},"actions":[{"type":"notify","text":"x"}]}""",
        """{"trigger":{"type":"network.joined","ssid":"This network name is far too long to be real"},"actions":[{"type":"notify","text":"x"}]}""",
        """{"trigger":{"type":"power.plugged","kind":"audio"},"actions":[{"type":"notify","text":"x"}]}""",
        // Shape: extra fields, repeated fields, wrong types, empty or too many actions.
        """{"trigger":{"type":"power.plugged"},"actions":[{"type":"notify","text":"x"}],"approved":true}""",
        """{"trigger":{"type":"power.plugged"},"actions":[{"type":"notify","text":"x","run":"now"}]}""",
        """{"trigger":{"type":"power.plugged"},"actions":[],"actions":[{"type":"notify","text":"x"}]}""",
        """{"trigger":{"type":"power.plugged"},"actions":[]}""",
        """{"trigger":{"type":"power.plugged"},"actions":{"type":"notify","text":"x"}}""",
        """{"trigger":"power.plugged","actions":[{"type":"notify","text":"x"}]}""",
        """{"trigger":{"type":"power.plugged"},"actions":[{"type":"notify","text":""}]}""",
        """{"trigger":{"type":"power.plugged"},"actions":[{"type":"notify","text":"a"},{"type":"notify","text":"b"},{"type":"notify","text":"c"},{"type":"notify","text":"d"},{"type":"notify","text":"e"},{"type":"notify","text":"f"}]}""",
        // Conditions: equal range, duplicate kinds, unknown power state.
        """{"trigger":{"type":"power.plugged"},"conditions":[{"type":"time.between","from":"09:00","to":"09:00"}],"actions":[{"type":"notify","text":"x"}]}""",
        """{"trigger":{"type":"power.plugged"},"conditions":[{"type":"power.is","state":"plugged"},{"type":"power.is","state":"unplugged"}],"actions":[{"type":"notify","text":"x"}]}""",
        """{"trigger":{"type":"power.plugged"},"conditions":[{"type":"power.is","state":"charging"}],"actions":[{"type":"notify","text":"x"}]}""",
        // A rule that would trigger itself.
        """{"trigger":{"type":"app.opened","appId":"steam"},"actions":[{"type":"app.open","appId":"Steam"}]}""",
    }.Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(InvalidRules))]
    public void AnythingOutsideTheListsIsRejected(string json) =>
        Assert.ThrowsAny<RuleRejectedException>(() => Read(json));

    [Theory]
    [InlineData("""{"trigger":{"type":"power.plugged"},"actions":[{"type":"shell.run","command":"rm -rf ~"}]}""")]
    [InlineData("""{"trigger":{"type":"power.plugged"},"actions":[{"type":"system.exec","argv":["reboot"]}]}""")]
    [InlineData("""{"trigger":{"type":"power.plugged"},"actions":[{"type":"package.install","name":"steam"}]}""")]
    [InlineData("""{"trigger":{"type":"power.plugged"},"actions":[{"type":"guardian.apply","option":"services.openssh.enable"}]}""")]
    [InlineData("""{"trigger":{"type":"power.plugged"},"actions":[{"type":"app.open","appId":"bash"}]}""")]
    [InlineData("""{"trigger":{"type":"power.plugged"},"actions":[{"type":"app.open","appId":"sudo"}]}""")]
    [InlineData("""{"trigger":{"type":"power.plugged"},"actions":[{"type":"app.open","appId":"nixos-rebuild"}]}""")]
    public void SystemChangesAreRefusedWithTheApprovalMessage(string json)
    {
        var error = Assert.Throws<SystemChangeRejectedException>(() => Read(json));
        Assert.Contains("system changes go through approval", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsAllowListIsExactlyTheReversiblePersonalSettings() =>
        Assert.Equal(["volume", "notifications.volume", "night-light", "do-not-disturb", "wallpaper"], RuleSettings.Keys.Keys);

    [Fact]
    public void ControlAndBidiCharactersAreRemovedFromText()
    {
        var rule = Read("""{"trigger":{"type":"device.connected","kind":"audio","name":"Sony\u202e\u0007"},"actions":[{"type":"notify","text":"Hi\nthere\u2028\u001b[2J"}]}""");
        Assert.Equal("Sony", rule.Trigger.Name);
        Assert.Equal("Hithere[2J", rule.Actions[0].Text);
    }

    [Fact]
    public void SentenceIsRenderedFromTheTypedRule()
    {
        var rule = Read("""
            {"trigger":{"type":"device.connected","kind":"audio"},
             "conditions":[{"type":"time.between","from":"09:00","to":"17:00"}],
             "actions":[{"type":"app.open","appId":"spotify"},{"type":"setting.set","key":"notifications.volume","value":25}]}
            """);
        Assert.Equal("When an audio device connects, if it is between 09:00 and 17:00, open spotify and set notifications volume to 25%.",
            RuleSchema.Describe(rule));
        Assert.Equal("Every weekday at 09:00, open calendar.", RuleSchema.Describe(Read(
            """{"trigger":{"type":"time.daily","time":"09:00","days":["fri","mon","tue","wed","thu"]},"actions":[{"type":"app.open","appId":"calendar"}]}""")));
    }

    [Fact]
    public void EventsUseTheSameFieldRules()
    {
        using var ok = JsonDocument.Parse("""{"type":"device.connected","kind":"audio","name":"WH-1000XM4"}""");
        Assert.Equal("audio", RuleSchema.ReadEvent(ok.RootElement).Kind);
        foreach (var bad in new[]
        {
            """{"type":"time.daily","time":"08:00"}""",
            """{"type":"device.connected","kind":"audio","extra":1}""",
            """{"type":"app.opened","appId":"a b"}""",
            """{"type":"shell.run"}""",
            "[]",
            "\"power.plugged\"",
        })
        {
            using var document = JsonDocument.Parse(bad);
            Assert.ThrowsAny<RuleRejectedException>(() => RuleSchema.ReadEvent(document.RootElement));
        }
    }
}
