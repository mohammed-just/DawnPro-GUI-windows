using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Globalization;
using Moondrop.Wpf;

namespace Moondrop.Tests;

[TestClass]
[DoNotParallelize]
public sealed class UiDesignRegressionTests
{
    [TestMethod]
    public void NormalWindowShowsAllEightBandsAndTheCompleteEditorAboveActions()
    {
        WpfTestHost.Run(() =>
        {
            using var model = MainViewModel.CreateDemo();
            var window = new MainWindow(model, LaunchOptions.Parse(["--demo", "--width=1180", "--height=780"]));
            try
            {
                window.Show(); window.UpdateLayout();
                var scroll = (ScrollViewer)window.FindName("EqPageScroll");
                var table = (DataGrid)window.FindName("BandTable");
                var editor = (Border)window.FindName("SelectedBandEditor");
                var graph = (Border)window.FindName("GraphCard");
                var viewport = scroll.TransformToAncestor(window).TransformBounds(new Rect(scroll.RenderSize));
                var tableBounds = table.TransformToAncestor(window).TransformBounds(new Rect(table.RenderSize));
                var editorBounds = editor.TransformToAncestor(window).TransformBounds(new Rect(editor.RenderSize));
                Assert.IsLessThanOrEqualTo(viewport.Bottom, tableBounds.Bottom, $"Table clipped: table={tableBounds}; viewport={viewport}; graph={graph.ActualHeight}; editor={editor.ActualHeight}; extent={scroll.ExtentHeight}");
                Assert.IsLessThanOrEqualTo(viewport.Bottom, editorBounds.Bottom, $"Editor clipped: editor={editorBounds}; viewport={viewport}");
                Assert.IsLessThanOrEqualTo(1, scroll.ScrollableHeight, "Normal workspace must fit without page scrolling.");
                Assert.AreEqual(8, table.Items.Count);
                Assert.IsGreaterThanOrEqualTo(180, graph.ActualHeight);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void PresetOptionsShowNamesAndGainFormattingNeverShowsNegativeZero()
    {
        using var model = MainViewModel.CreateDemo();
        Assert.AreEqual("Unsaved EQ", model.PresetOptions[0].ToString());
        foreach (var band in model.Bands) band.Enabled = false;
        Assert.AreEqual("0.0 dB", model.RecommendedPreGainText);
        var preset = model.SaveLocal("My IEM");
        Assert.AreEqual(preset.Id, model.EditingPresetId);
        Assert.IsTrue(model.PresetOptions.Any(x => x.Name == "My IEM"));
        Assert.IsFalse(model.HasNoLocalPresets);
        model.Bands[0].Gain = 2;
        Assert.IsTrue(model.IsPresetEdited);
        Assert.AreEqual(preset.Id, model.EditingPresetId, "Editing a saved preset must retain its identity.");
    }

    [TestMethod]
    public void FormattedNumericInputsRoundTripWithoutTruncationOrInvalidSmallQ()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        var frequency = new FrequencyTextConverter();
        Assert.AreEqual("1,234", frequency.Convert(1234, typeof(string), null!, culture));
        Assert.AreEqual(1234, frequency.ConvertBack("1,234", typeof(int), null!, culture));
        Assert.IsTrue(new NumericRule { Minimum = 20, Maximum = 20000, Integer = true }.Validate("1,234", culture).IsValid);
        var q = new QTextConverter();
        var formattedQ = q.Convert(1.0 / 256, typeof(string), null!, culture);
        Assert.AreEqual("0.0039", formattedQ);
        Assert.IsGreaterThan(0.0, (double)q.ConvertBack(formattedQ, typeof(double), null!, culture));
        Assert.AreEqual("0.0", new GainTextConverter().Convert(-0.001, typeof(string), null!, culture));

        WpfTestHost.Run(() =>
        {
            using var model = MainViewModel.CreateDemo();
            var window = new MainWindow(model, LaunchOptions.Parse(["--demo"]));
            try
            {
                window.Show(); window.UpdateLayout();
                var editor = (Border)window.FindName("SelectedBandEditor");
                foreach (var input in Descendants<TextBox>(editor))
                {
                    input.ApplyTemplate();
                    var host = (ScrollViewer)input.Template.FindName("PART_ContentHost", input);
                    Assert.IsGreaterThanOrEqualTo(16, host.ViewportHeight, "Numeric text must have space for a complete line.");
                    Assert.IsLessThanOrEqualTo(1, host.ScrollableHeight, "A single numeric line must not be clipped vertically.");
                }
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void NarrowNavigationKeepsEveryIconInsideItsVisibleRow()
    {
        WpfTestHost.Run(() =>
        {
            using var model = MainViewModel.CreateDemo();
            var window = new MainWindow(model, LaunchOptions.Parse(["--demo", "--width=640", "--height=620"]));
            try
            {
                window.Show(); window.UpdateLayout();
                Assert.AreEqual(44, ((Image)window.FindName("DeviceLogo")).ActualWidth);
                var nav = Descendants<ListBox>(window).First(x => x.Items.OfType<ListBoxItem>().Any());
                foreach (var row in nav.Items.OfType<ListBoxItem>())
                {
                    var icon = Descendants<Viewbox>(row).First();
                    var bounds = icon.TransformToAncestor(row).TransformBounds(new Rect(icon.RenderSize));
                    Assert.IsGreaterThanOrEqualTo(0, bounds.Left);
                    Assert.IsLessThanOrEqualTo(row.ActualWidth, bounds.Right, "Navigation icon must fit within the row.");
                }
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void GraphRendersOnlyTheCombinedCurveUntilBandOverlaysAreRequested()
    {
        WpfTestHost.Run(() =>
        {
            using var model = MainViewModel.CreateDemo();
            Assert.IsFalse(model.IndividualCurves);
            var window = new MainWindow(model, LaunchOptions.Parse(["--demo"]));
            try
            {
                window.Show(); window.UpdateLayout();
                var graph = (EqGraph)window.FindName("EqGraphControl");
                int CurveCount()
                {
                    window.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)graph.ActualWidth, (int)graph.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(graph);
                    return CurveDrawings(VisualTreeHelper.GetDrawing(graph)).Count();
                }
                Assert.AreEqual(2, CurveCount(), "The default view has a combined curve and the flat target.");
                model.ShowReferenceCurve = false;
                Assert.AreEqual(1, CurveCount(), "The selected band must not add its own response curve in the clean view.");
                model.IndividualCurves = true;
                Assert.AreEqual(9, CurveCount(), "The optional setting shows eight band responses plus their sum.");
                model.IndividualCurves = false;
                Assert.AreEqual(1, CurveCount());
            }
            finally { window.Close(); }
        });
    }

    private static IEnumerable<GeometryDrawing> CurveDrawings(Drawing drawing)
    {
        if (drawing is GeometryDrawing { Geometry: StreamGeometry } curve) yield return curve;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var curveChild in CurveDrawings(child)) yield return curveChild;
    }

    [TestMethod]
    public void ComboBoxSelectionAndArrowHaveSeparateSpaceAndSettingsUseSwitchTemplates()
    {
        WpfTestHost.Run(() =>
        {
            using var model = MainViewModel.CreateDemo();
            var window = new MainWindow(model, LaunchOptions.Parse(["--demo"]));
            try
            {
                window.Show(); model.SelectedPage = ShellPage.Settings; window.UpdateLayout();
                var groups = (System.Windows.Controls.Primitives.UniformGrid)window.FindName("SettingsGroups");
                var switches = Descendants<CheckBox>(groups).ToArray();
                Assert.HasCount(9, switches);
                foreach (var control in switches)
                {
                    control.ApplyTemplate();
                    Assert.IsNotNull(control.Template.FindName("Track", control));
                    Assert.IsNotNull(control.Template.FindName("Thumb", control));
                }
                var theme = Descendants<ComboBox>(groups).Single(); theme.ApplyTemplate();
                var content = Descendants<ContentPresenter>(theme).First();
                Assert.IsGreaterThanOrEqualTo(30, content.Margin.Right);
                Assert.IsGreaterThanOrEqualTo(34, theme.ActualHeight);
            }
            finally { window.Close(); }
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var matchChild in Descendants<T>(child)) yield return matchChild;
        }
    }
}
