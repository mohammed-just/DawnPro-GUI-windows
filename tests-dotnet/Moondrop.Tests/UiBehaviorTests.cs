using Moondrop.Wpf;

namespace Moondrop.Tests;

[TestClass]
public sealed class UiBehaviorTests
{
    [TestMethod]
    public void NavigationStartsOnEqAndSelectionChangesTheVisiblePage()
    {
        using var model = MainViewModel.CreateDemo();

        Assert.AreEqual(ShellPage.Eq, model.SelectedPage);

        model.SelectedPage = ShellPage.Settings;

        Assert.AreEqual(ShellPage.Settings, model.SelectedPage);
    }

    [TestMethod]
    public void ResponsiveLayoutUsesWideMediumAndNarrowBreakpoints()
    {
        Assert.AreEqual(ShellLayoutMode.Wide, ResponsiveShell.Classify(1440));
        Assert.AreEqual(ShellLayoutMode.Medium, ResponsiveShell.Classify(1100));
        Assert.AreEqual(ShellLayoutMode.Narrow, ResponsiveShell.Classify(760));
    }

    [TestMethod]
    public void LaunchOptionsParseDeterministicWindowDimensions()
    {
        var options = LaunchOptions.Parse(["--demo", "--width=1440", "--height=900"]);

        Assert.AreEqual(1440, options.Width);
        Assert.AreEqual(900, options.Height);
    }

    [TestMethod]
    public async Task ConfirmationDialogWaitsForAndReportsTheUsersDecision()
    {
        var dialog = new DialogState();

        var decision = dialog.AskAsync("Import EQ", "Import 8 bands?", "Import");
        Assert.IsTrue(dialog.IsOpen);
        Assert.AreEqual("Import EQ", dialog.Title);

        dialog.Cancel();

        Assert.IsFalse(await decision);
        Assert.IsFalse(dialog.IsOpen);
    }

    [TestMethod]
    public void ErrorBannerRemainsVisibleUntilDismissed()
    {
        var banner = new StatusBannerState();

        banner.ShowError("Refresh failed", "The device did not respond.");

        Assert.IsTrue(banner.IsVisible);
        Assert.AreEqual("Refresh failed", banner.Title);
        banner.Dismiss();
        Assert.IsFalse(banner.IsVisible);
    }

    [TestMethod]
    public void GraphProjectionMapsThePlotMidpointToLogFrequencyAndGain()
    {
        var value = EqGraphProjection.FromNormalized(0.5, 0.5);

        Assert.AreEqual(632, value.Frequency);
        Assert.AreEqual(-3.0, value.Gain, 0.001);
        Assert.AreEqual("632 Hz  −3.0 dB", EqGraphProjection.FormatReadout(value));
    }

    [TestMethod]
    public void GraphResponseIncludesOneSeriesPerEnabledBandAndTheirCombinedSum()
    {
        using var model = MainViewModel.CreateDemo();
        model.Bands[7].Enabled = false;

        var response = EqGraphResponse.Calculate(model.Bands, preGain: -2, sampleCount: 64);

        Assert.HasCount(7, response.Bands);
        Assert.HasCount(64, response.CombinedDb);
        var expectedFirst = -2 + response.Bands.Sum(x => x.MagnitudeDb[0]);
        Assert.AreEqual(expectedFirst, response.CombinedDb[0], 0.0001);
    }

    [TestMethod]
    public void SelectedBandStateMovesWithGraphOrCardSelection()
    {
        using var model = MainViewModel.CreateDemo();
        Assert.IsTrue(model.Bands[0].IsSelected);

        model.SelectedBandIndex = 3;

        Assert.IsFalse(model.Bands[0].IsSelected);
        Assert.IsTrue(model.Bands[3].IsSelected);
    }
}
