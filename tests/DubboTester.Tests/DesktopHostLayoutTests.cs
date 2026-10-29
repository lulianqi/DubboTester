using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace DubboTester.Tests;

// Guard the host configuration that previously differed from browser previews.
// These checks do not replace visual tests in the actual desktop application.
public sealed class DesktopHostLayoutTests
{
    [Fact]
    public void MacHostUsesNativeMacSizingInsteadOfScaledIpadCompatibility()
    {
        using var source = new StringReader(ReadAsset("Info.plist"));
        using XmlReader reader = XmlReader.Create(source, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null
        });
        XDocument manifest = XDocument.Load(reader);
        XElement familyKey = Assert.Single(manifest.Descendants("key"),
            key => key.Value == "UIDeviceFamily");
        XElement family = familyKey.ElementsAfterSelf().First();

        Assert.Equal("array", family.Name.LocalName);
        Assert.Equal("6", Assert.Single(family.Elements("integer")).Value);
    }

    [Fact]
    public void HostUsesTheSameInitialViewportAsTheBrowser()
    {
        string html = ReadAsset("index.html");

        Assert.Contains("content=\"width=device-width, initial-scale=1.0\"", html);
        Assert.DoesNotContain("maximum-scale", html);
        Assert.DoesNotContain("user-scalable=no", html);
        Assert.DoesNotContain("status-bar-safe-area", html);
        Assert.Contains("<div id=\"app\">", html);
    }

    [Fact]
    public void DesktopStylesDoNotRescaleContentOrDivideViewportDimensions()
    {
        string css = ReadAsset("app.css");

        Assert.DoesNotMatch(@"(?m)^\s*(?:zoom|transform)\s*:", css);
        Assert.DoesNotContain("--desktop-viewport", css);
        Assert.DoesNotContain("@media", css);
        Assert.Contains("html, body, #app", css);
        Assert.Contains("height: 100%;", css);
        Assert.Contains("-webkit-text-size-adjust: 100%;", css);
    }

    private static string ReadAsset(string name)
    {
        using Stream stream = typeof(DesktopHostLayoutTests).Assembly
            .GetManifestResourceStream($"DesktopLayout.{name}")
            ?? throw new InvalidOperationException($"Missing embedded desktop asset: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
