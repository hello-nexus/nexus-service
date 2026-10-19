using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexTemplateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flex-tpl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private FlexTemplateStore Store() => new(_dir);

    private static DevicesSettings Devices()
    {
        var d = new DevicesSettings();
        d.FlexUnits["AA"] = new LianLiFlexUnitSettings { Mode = "wave", SortIndex = 2, Merge = true, PackageSignature = "sig", PackageEffectIndex = "01020304" };
        d.FlexLcdScreens["S1"] = new LianLiWirelessScreenSettings { ContentType = "clock", Order = 3 };
        return d;
    }

    [Fact]
    public void A_template_saves_a_copy_without_the_stored_package_marks()
    {
        var store = Store();
        var (t, error) = LianLiFlexRoutes.BuildTemplate(new LianLiFlexTemplateCreateRequest { Name = " Calm ", Mac = "AA", Serial = "S1" }, Devices(), store);
        Assert.Null(error);
        Assert.Equal("Calm", t!.Name);
        Assert.Null(t.Lighting!.PackageSignature);
        Assert.Equal("wave", store.Get(t.Id)!.Lighting!.Mode);
        Assert.Single(store.List());
        Assert.True(store.Delete(t.Id));
        Assert.Empty(store.List());
    }

    [Theory]
    [InlineData("", "AA")]
    [InlineData("x", "")]
    [InlineData("x", "ZZ")]
    public void A_template_needs_a_name_and_known_targets(string name, string mac)
    {
        var (t, error) = LianLiFlexRoutes.BuildTemplate(new LianLiFlexTemplateCreateRequest { Name = name, Mac = mac }, Devices(), Store());
        Assert.Null(t);
        Assert.NotNull(error);
    }

    [Fact]
    public void Applying_keeps_the_target_position_and_merge_choice()
    {
        var store = Store();
        var source = Devices();
        source.FlexUnits["AA"].Mode = "rainbow";
        var template = LianLiFlexRoutes.BuildTemplate(new LianLiFlexTemplateCreateRequest { Name = "t", Mac = "AA", Serial = "S1" }, source, store).Template!;
        var target = Devices();
        target.FlexUnits["BB"] = new LianLiFlexUnitSettings { SortIndex = 5, Merge = false };
        Assert.Null(LianLiFlexRoutes.ApplyTemplate(template, new LianLiFlexTemplateApplyRequest { Mac = "BB", Serial = "S1" }, target));
        Assert.Equal("rainbow", target.FlexUnits["BB"].Mode);
        Assert.Equal(5, target.FlexUnits["BB"].SortIndex);
        Assert.False(target.FlexUnits["BB"].Merge);
        Assert.Equal(3, target.FlexLcdScreens["S1"].Order);
        Assert.NotNull(LianLiFlexRoutes.ApplyTemplate(template, new LianLiFlexTemplateApplyRequest(), target));
    }

    [Fact]
    public void Export_then_import_round_trips_and_hostile_archives_are_refused()
    {
        var store = Store();
        var t = LianLiFlexRoutes.BuildTemplate(new LianLiFlexTemplateCreateRequest { Name = "t", Mac = "AA" }, Devices(), store).Template!;
        var zip = store.Export(t.Id)!;
        var (imported, error) = store.Import(zip);
        Assert.Null(error);
        Assert.NotEqual(t.Id, imported!.Id);
        Assert.Equal("wave", imported.Lighting!.Mode);

        Assert.NotNull(store.Import(new byte[] { 1, 2, 3 }).Error);
        Assert.NotNull(store.Import(Zip(("template.json", "{}"), ("extra.bin", "x"))).Error);
        Assert.NotNull(store.Import(Zip(("../template.json", "{}"))).Error);
        Assert.NotNull(store.Import(Zip(("template.json", new string(' ', FlexTemplateStore.MaxJsonBytes + 1)))).Error);
        Assert.NotNull(store.Import(Zip(("template.json", "{\"version\":2,\"name\":\"x\"}"))).Error);
        Assert.NotNull(store.Import(Zip(("template.json", "not json"))).Error);
    }

    private static byte[] Zip(params (string Name, string Body)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, body) in entries)
            {
                using var w = zip.CreateEntry(name).Open();
                w.Write(Encoding.UTF8.GetBytes(body));
            }
        }
        return ms.ToArray();
    }
}
