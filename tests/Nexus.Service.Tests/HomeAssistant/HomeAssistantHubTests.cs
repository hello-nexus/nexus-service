using System.Linq;
using System.Text.Json;
using Nexus.Service.Integrations.HomeAssistant;
using Xunit;

namespace Nexus.Service.Tests.HomeAssistant;

public class HomeAssistantHubTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string[] Extract(string json) =>
        HomeAssistantHub.ExtractEntityIds(Parse(json)).OrderBy(x => x).ToArray();

    [Fact]
    public void Extract_SectionsView()
    {
        var ids = Extract("""
        {"views":[{"type":"sections","sections":[{"cards":[
          {"type":"tile","entity":"light.desk"},
          {"type":"heading","badges":[{"entity":"sensor.temp"}]}
        ]}]}]}
        """);
        Assert.Equal(new[] { "light.desk", "sensor.temp" }, ids);
    }

    [Fact]
    public void Extract_MasonryNestedStacks()
    {
        var ids = Extract("""
        {"views":[{"cards":[
          {"type":"vertical-stack","cards":[
            {"type":"grid","cards":[
              {"type":"conditional","card":{"type":"button","entity":"scene.movie"}},
              {"type":"gauge","entity":"sensor.cpu"}
            ]}
          ]}
        ]}]}
        """);
        Assert.Equal(new[] { "scene.movie", "sensor.cpu" }, ids);
    }

    [Fact]
    public void Extract_EntitiesRowsAsStringsAndObjects()
    {
        var ids = Extract("""
        {"views":[{"cards":[{"type":"entities","entities":[
          "switch.fan_plug", {"entity":"cover.blind","name":"x"}, {"type":"divider"}, 5, null
        ]}]}]}
        """);
        Assert.Equal(new[] { "cover.blind", "switch.fan_plug" }, ids);
    }

    [Fact]
    public void Extract_BadgesAsStrings()
    {
        Assert.Equal(new[] { "binary_sensor.door" }, Extract("""{"badges":["binary_sensor.door"]}"""));
    }

    [Fact]
    public void Extract_DropsUnsupportedDomainsAndGarbage()
    {
        var ids = Extract("""
        {"cards":[
          {"entity":"camera.front"},{"entity":"climate.hall"},{"entity":"sensor."},
          {"entity":"sensor.Bad Id"},{"entity":"nodot"},{"entity":42},{"entity":["light.a"]},
          {"entities":["media_player.tv","lock.front"]}
        ]}
        """);
        Assert.Equal(new[] { "lock.front" }, ids);
    }

    [Fact]
    public void Extract_SkipsConditionEntities()
    {
        var ids = Extract("""
        {"cards":[{"type":"conditional",
          "conditions":[{"condition":"state","entity":"sensor.gate","state":"on"}],
          "card":{"type":"tile","entity":"light.desk"}}]}
        """);
        Assert.Equal(new[] { "light.desk" }, ids);
    }

    [Fact]
    public void Extract_SkipsVisibilityRuleEntities()
    {
        var ids = Extract("""
        {"sections":[{"visibility":[{"condition":"state","entity":"input_boolean.guest","state":"on"}],
          "cards":[{"type":"tile","entity":"fan.desk",
            "visibility":[{"condition":"state","entity":"sensor.lux","state_not":"0"}]}]}]}
        """);
        Assert.Equal(new[] { "fan.desk" }, ids);
    }

    [Fact]
    public void Extract_EmptyConfig()
    {
        Assert.Empty(Extract("""{"strategy":{"type":"original-states"}}"""));
    }

    [Theory]
    [InlineData("light", true, null, false, "turn_on")]
    [InlineData("light", false, null, false, "turn_off")]
    [InlineData("switch", null, null, false, "turn_on")]
    [InlineData("switch", false, null, false, "turn_off")]
    [InlineData("input_boolean", true, null, false, "turn_on")]
    [InlineData("fan", false, null, false, "turn_off")]
    [InlineData("automation", true, null, false, "turn_on")]
    [InlineData("input_boolean", null, null, false, null)]
    [InlineData("scene", null, "run", false, "turn_on")]
    [InlineData("script", null, "run", false, "turn_on")]
    [InlineData("scene", true, null, false, null)]
    [InlineData("button", null, "run", false, "press")]
    [InlineData("input_button", null, "run", false, "press")]
    [InlineData("button", null, "open", false, null)]
    [InlineData("cover", null, "open", false, "open_cover")]
    [InlineData("cover", null, "close", false, "close_cover")]
    [InlineData("cover", null, "stop", false, "stop_cover")]
    [InlineData("cover", true, null, false, null)]
    [InlineData("cover", null, "lock", false, null)]
    [InlineData("lock", null, "lock", false, "lock")]
    [InlineData("lock", null, "unlock", false, "unlock")]
    [InlineData("lock", null, "unlock", true, null)]
    [InlineData("lock", null, "open", false, null)]
    [InlineData("sensor", true, "run", false, null)]
    [InlineData("binary_sensor", true, null, false, null)]
    [InlineData("camera", true, null, false, null)]
    public void ResolveService_MapsAndRefuses(string domain, bool? on, string? action, bool codeRequired, string? expected)
    {
        var (service, ok) = HomeAssistantHub.ResolveService(
            domain, new HaSetEntityBody { On = on, Action = action }, codeRequired);
        Assert.Equal(expected is not null, ok);
        if (expected is not null)
        {
            Assert.Equal(expected, service);
        }
    }

    [Fact]
    public void Normalize_SensorCarriesUnitAndDeviceClass()
    {
        var dto = HomeAssistantHub.NormalizeEntity(Parse("""
        {"entity_id":"sensor.t","state":"21.5","attributes":{"friendly_name":"Temp","unit_of_measurement":"C","device_class":"temperature"}}
        """))!;
        Assert.Equal("sensor", dto.Domain);
        Assert.Equal("C", dto.Unit);
        Assert.Equal("temperature", dto.DeviceClass);
        Assert.False(dto.On);
        Assert.Equal(-1, dto.PositionPct);
        Assert.True(dto.Reachable);
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("off", false)]
    public void Normalize_BinarySensorOn(string state, bool on)
    {
        var dto = HomeAssistantHub.NormalizeEntity(Parse(
            "{\"entity_id\":\"binary_sensor.d\",\"state\":\"" + state + "\",\"attributes\":{}}"))!;
        Assert.Equal(on, dto.On);
    }

    [Fact]
    public void Normalize_CoverPosition()
    {
        var dto = HomeAssistantHub.NormalizeEntity(Parse("""
        {"entity_id":"cover.b","state":"open","attributes":{"current_position":70}}
        """))!;
        Assert.Equal(70, dto.PositionPct);
        Assert.False(dto.On);
    }

    [Fact]
    public void Normalize_CoverWithoutPosition()
    {
        var dto = HomeAssistantHub.NormalizeEntity(Parse(
            """{"entity_id":"cover.b","state":"open","attributes":{}}"""))!;
        Assert.Equal(-1, dto.PositionPct);
    }

    [Theory]
    [InlineData("""{"code_format":"number"}""", true)]
    [InlineData("""{"code_format":null}""", false)]
    [InlineData("""{}""", false)]
    public void Normalize_LockCodeRequired(string attrs, bool expected)
    {
        var dto = HomeAssistantHub.NormalizeEntity(Parse(
            "{\"entity_id\":\"lock.f\",\"state\":\"locked\",\"attributes\":" + attrs + "}"))!;
        Assert.Equal(expected, dto.CodeRequired);
    }

    [Fact]
    public void Normalize_UnsupportedDomainIsNull()
    {
        Assert.Null(HomeAssistantHub.NormalizeEntity(Parse(
            """{"entity_id":"climate.h","state":"heat","attributes":{}}""")));
    }

    [Fact]
    public void Normalize_UnavailableIsNotReachable()
    {
        var dto = HomeAssistantHub.NormalizeEntity(Parse(
            """{"entity_id":"switch.s","state":"unavailable","attributes":{}}"""))!;
        Assert.False(dto.Reachable);
    }

    [Theory]
    [InlineData("""{"entity_id":"light.a","hidden_by":"user","entity_category":null}""", true)]
    [InlineData("""{"entity_id":"light.a","hidden_by":null,"entity_category":"diagnostic"}""", true)]
    [InlineData("""{"entity_id":"light.a","hidden_by":null,"entity_category":null}""", false)]
    [InlineData("""{"entity_id":"light.a"}""", false)]
    public void BuildMaps_HiddenFlag(string entityJson, bool hidden)
    {
        var maps = HomeAssistantWebSocket.BuildMaps(
            Parse("[]"), Parse("[]"), Parse("[" + entityJson + "]"));
        Assert.Equal(hidden, maps.IsHidden("light.a"));
    }
}
