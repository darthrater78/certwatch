using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace CertWatch.Core.Tests;

/// <summary>
/// Guards the App's theme dictionaries. A key defined both in a theme (Flashbang/Dark) and in Styles.xaml
/// makes {DynamicResource Key} inside Styles.xaml resolve to the wrong object: an early build crashed at
/// startup because Style "PrimaryButton" shadowed brush "PrimaryButton".
/// </summary>
public class ThemeResourceTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string ThemesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CertWatch.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "CertWatch.App", "Themes");
    }

    private static string[] Keys(string file) =>
        XDocument.Load(Path.Combine(ThemesDir(), file)).Root!
            .Elements()
            .Select(e => (string?)e.Attribute(Xaml + "Key"))
            .OfType<string>()
            .ToArray();

    [Theory]
    [InlineData("Flashbang.xaml")]
    [InlineData("Dark.xaml")]
    public void ThemeKeysDoNotCollideWithStyleKeys(string theme)
    {
        var shared = Keys(theme).Intersect(Keys("Styles.xaml")).ToArray();
        Assert.True(shared.Length == 0, $"{theme} and Styles.xaml both define: {string.Join(", ", shared)}");
    }

    [Fact]
    public void FlashbangAndDarkDefineTheSameKeys()
    {
        var light = Keys("Flashbang.xaml").ToHashSet();
        var dark = Keys("Dark.xaml").ToHashSet();
        Assert.True(light.SetEquals(dark),
            $"only in Flashbang: {string.Join(", ", light.Except(dark))}; only in Dark: {string.Join(", ", dark.Except(light))}");
    }
}
