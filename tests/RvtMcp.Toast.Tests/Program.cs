using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RvtMcp.Plugin.Views.Toast;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--demo")
                return ToastPreview.Run();
            CheckCompactBody();
            CheckCloseHover();
            CheckCompactLayout();
            ToastVisualTests.Run();
            CheckNaNSafePlacement();
            CheckBrandingFollowsPreference();
            CheckIdentityRow();
            CheckThumbnailRow();
            CheckStationaryPointerFiltering();
            CheckSingleActivityCard();
            CheckStatusAndClickLifecycle();
            Console.WriteLine("PASS: single-card activity manager and WPF guards");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void CheckCompactBody()
    {
        var cases = new (string Summary, string Detail, string Expected)[]
        {
            ("24 rooms", "Placed 22, unplaced 2", "24 rooms · Placed 22, unplaced 2"),
            ("Done", null, "Done"),
            (null, "Context only", "Context only"),
            (null, null, ""),
            (" ", " ", ""),
            ("Done", " list rooms ", "Done"),
            (" Done ", " done ", "Done"),
            (" output_path is required. ", " Export a view. ", "output_path is required. · Export a view."),
            ("Saved view.png", "Click to open", "Saved view.png · Click to open")
        };
        foreach (var item in cases)
        {
            var vm = new McpToastViewModel
            {
                Title = "List Rooms", Summary = item.Summary, Detail = item.Detail
            };
            if (vm.Body != item.Expected)
                throw new Exception($"Expected body '{item.Expected}', got '{vm.Body}'.");
        }
        Console.WriteLine("PASS: merged body preserves results, handles blanks, removes duplicates");
    }

    private static void CheckCloseHover()
    {
        var snapshot = new ActivitySnapshot(1, false, 1, 0, 0, "List Rooms", "Done", true, false);
        var window = new McpToastWindow(snapshot, null, null, null, null, null);
        try
        {
            var close = (Border)typeof(McpToastWindow).GetField("_closeHost",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(window);
            var original = close.Background;
            close.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            if (!ReferenceEquals(close.Background, original))
                throw new Exception("Hovering × must not change its transparent background.");
            close.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
            if (!ReferenceEquals(close.Background, original))
                throw new Exception("Leaving × must not change its background.");
        }
        finally { window.CloseImmediate(); }
        Console.WriteLine("PASS: close control keeps a transparent hover background");
    }

    private static void CheckCompactLayout()
    {
        double Measure(string body)
        {
            var snapshot = new ActivitySnapshot(1, false, 2, 1, 1, "List Rooms", body, true, true);
            var window = new McpToastWindow(snapshot, null, null, null, null, null);
            try
            {
                var root = (Border)window.Content;
                var grid = (Grid)root.Child;
                if (grid.RowDefinitions.Count != 4 || grid.Children.Count != 4)
                    throw new Exception("Expected the stable activity card layout.");
                var header = (DockPanel)grid.Children[0];
                var hasActivityHeader = false;
                foreach (var child in header.Children)
                {
                    if (child is TextBlock text && text.Text == "RVT-MCP - List Rooms")
                    {
                        hasActivityHeader = true;
                        break;
                    }
                }
                if (!hasActivityHeader)
                    throw new Exception("The activity card must name the latest tool in its header.");
                var bodyGrid = (Grid)grid.Children[1];
                var counterRow = (Viewbox)bodyGrid.Children[0];
                if (counterRow.Visibility != Visibility.Visible || (string)counterRow.ToolTip != body
                    || bodyGrid.Children[1].Visibility != Visibility.Collapsed)
                    throw new Exception("Activity must show counters, with its last summary only in the tooltip.");
                var thumbnail = (Border)grid.Children[2];
                if (thumbnail.Visibility != Visibility.Collapsed)
                    throw new Exception("Thumbnail row must stay collapsed without a capture.");
                var footer = (Grid)grid.Children[3];
                if (((Grid)footer.Children[0]).HorizontalAlignment != HorizontalAlignment.Right)
                    throw new Exception("Brand must align right independently of footer fill.");
                root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return root.DesiredSize.Height;
            }
            finally { window.CloseImmediate(); }
        }

        var first = Measure("Done");
        var second = Measure("A longer body still stays on one fixed row");
        if (Math.Abs(first - second) > 0.1)
            throw new Exception($"Activity card height changed ({first} -> {second}).");
        Console.WriteLine("PASS: three-row card, latest-tool title, counters, right-aligned brand and stable height");
    }

    private static void CheckNaNSafePlacement()
    {
        var aggregator = new ActivityAggregator();
        var manager = new McpToastManager(Dispatcher.CurrentDispatcher, aggregator);
        try
        {
            if (!aggregator.RecordResult("revit_list_rooms", "Done", true, null, true))
                throw new Exception("The first activity result did not request a render.");
            manager.Render();
            Pump();
            var first = Current(manager);
            if (first == null)
                throw new Exception("The first activity card was not created.");
            AssertPosition(first, 16, 16);
        }
        finally { manager.DismissAllImmediate(); }
        Console.WriteLine("PASS: first placement from unset WPF coordinates via manager");
    }

    private static void CheckBrandingFollowsPreference()
    {
        var show = false;
        var aggregator = new ActivityAggregator();
        var manager = new McpToastManager(Dispatcher.CurrentDispatcher, aggregator, showBranding: () => show);
        try
        {
            if (!aggregator.RecordResult("List Rooms", "Done", true, null, true))
                throw new Exception("The first activity result did not request a render.");
            manager.Render();
            Pump();
            var window = Current(manager);
            if (window == null)
                throw new Exception("The activity card was not created.");
            var footer = BrandRow(window);
            if (TitleText(window) != "List Rooms" || footer.Visibility != Visibility.Collapsed)
                throw new Exception("A card opened with branding off must omit the prefix and wordmark.");
            show = true;
            manager.ApplyShowBranding();
            Pump();
            if (TitleText(window) != "RVT-MCP - List Rooms" || footer.Visibility != Visibility.Hidden)
                throw new Exception("Turning branding on must restore the prefix and reserve a blank brand row.");
        }
        finally { manager.DismissAllImmediate(); }
        Console.WriteLine("PASS: new and open cards follow the branding preference");
    }

    private static void CheckIdentityRow()
    {
        var snapshot = new ActivitySnapshot(1, false, 1, 0, 0, "List Rooms", "Done", true, false);

        var window = new McpToastWindow(snapshot, null, null, null, null, null,
            instanceIdentity: "Revit 2027");
        try
        {
            var footer = BrandRow(window);
            var identity = (TextBlock)typeof(McpToastWindow)
                .GetField("_identityText", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(window);
            if (identity.Text != "Revit 2027")
                throw new Exception("The footer did not carry the supplied instance identity.");
            if (footer.Visibility != Visibility.Visible)
                throw new Exception("An instance identity must keep the footer row visible.");
            window.SetShowBranding(false);
            if (footer.Visibility != Visibility.Visible)
                throw new Exception("Turning branding off must not hide the instance identity.");
            if (TitleText(window) != "List Rooms")
                throw new Exception("Identity must not restore the product prefix while branding is off.");
            window.SetShowBranding(true);
            if (TitleText(window) != "RVT-MCP - List Rooms")
                throw new Exception("Branding on must still prefix the title alongside the identity.");
        }
        finally { window.CloseImmediate(); }

        var plain = new McpToastWindow(snapshot, null, null, null, null, null);
        try
        {
            if (BrandRow(plain).Visibility != Visibility.Hidden)
                throw new Exception("A card without identity must still park the wordmark row.");
            plain.SetShowBranding(false);
            if (BrandRow(plain).Visibility != Visibility.Collapsed)
                throw new Exception("A card without identity must collapse the footer with branding off.");
        }
        finally { plain.CloseImmediate(); }
        Console.WriteLine("PASS: instance identity keeps the footer visible independently of branding");
    }

    private static void CheckThumbnailRow()
    {
        var png = WriteTempPng();
        var snapshot = new ActivitySnapshot(1, false, 1, 0, 1, "Capture View", "Saved", true, false,
            latestImagePath: png);

        var window = new McpToastWindow(snapshot, null, null, null, null, null);
        try
        {
            var host = (Border)typeof(McpToastWindow)
                .GetField("_thumbnailHost", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(window);
            var image = (Image)typeof(McpToastWindow)
                .GetField("_thumbnailImage", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(window);
            if (host.Visibility != Visibility.Visible || image.Source == null)
                throw new Exception("A capture result must render its thumbnail in the card.");

            // A newer result without an image collapses the row again.
            window.Update(new ActivitySnapshot(1, false, 2, 0, 1, "List Rooms", "Done", true, false));
            if (host.Visibility != Visibility.Collapsed || image.Source != null)
                throw new Exception("The thumbnail must clear when the latest result has no image.");

            // An unsafe path never renders.
            window.Update(new ActivitySnapshot(1, false, 3, 0, 1, "Capture View", "Saved", true, false,
                latestImagePath: @"C:\Windows\System32\cmd.exe.png"));
            if (host.Visibility != Visibility.Collapsed)
                throw new Exception("An out-of-allowlist path must not render a thumbnail.");
        }
        finally { window.CloseImmediate(); }
        Console.WriteLine("PASS: latest-capture thumbnail shows, clears and rejects unsafe paths");
    }

    private static string WriteTempPng()
    {
        var path = Path.Combine(Path.GetTempPath(), "rvtmcp-toast-check.png");
        // 1x1 transparent PNG
        var bytes = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44,
            0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F,
            0x15, 0xC4, 0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00,
            0x01, 0x00, 0x00, 0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49,
            0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
        };
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string TitleText(McpToastWindow window)
    {
        var title = (TextBlock)typeof(McpToastWindow).GetField("_titleText", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
        return title.Text;
    }

    private static Grid BrandRow(McpToastWindow window)
    {
        return (Grid)typeof(McpToastWindow).GetField("_brandRow", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
    }

    private static void CheckSingleActivityCard()
    {
        var aggregator = new ActivityAggregator();
        var manager = new McpToastManager(Dispatcher.CurrentDispatcher, aggregator);
        try
        {
            for (var i = 0; i < 100; i++)
            {
                if (aggregator.RecordResult("revit_list_rooms", "Done", true, null, true))
                    manager.Render();
            }
            Pump();

            var first = Current(manager);
            if (first == null)
                throw new Exception("The activity card was not created.");
            var firstId = first.CardId;
            var stableHeight = first.ActualHeight;

            if (aggregator.RecordResult("revit_list_rooms", "Updated", true, null, true))
                manager.Render();
            Pump();
            var updated = Current(manager);
            if (!ReferenceEquals(first, updated) || updated.CardId != firstId)
                throw new Exception("A result update must reuse the one visible window and CardId.");
            if (Math.Abs(updated.ActualHeight - stableHeight) > 0.1)
                throw new Exception("Updating an activity card changed its height.");

            if (!aggregator.Dismiss(firstId))
                throw new Exception("The current card could not be dismissed.");
            manager.Render(); // begin the one-way fade

            if (aggregator.RecordResult("revit_list_rooms", "New card", true, null, true))
                manager.Render(); // force-closes the fading card before creating the new one
            Pump();
            var replacement = Current(manager);
            if (replacement == null || replacement.CardId == firstId)
                throw new Exception("A result during fade must create one replacement card.");

            if (!aggregator.Dismiss(replacement.CardId))
                throw new Exception("The replacement card could not enter its fade.");
            manager.Render();
            if (!aggregator.Reset())
                throw new Exception("Turning toast off during fade did not clear the card.");
            manager.Render();
            Pump();
            if (Current(manager) != null)
                throw new Exception("Turning toast off during fade left a topmost window behind.");

            if (!aggregator.RecordResult("revit_list_rooms", "Restored", true, null, true))
                throw new Exception("The post-toggle activity result did not request a render.");
            manager.Render();
            Pump();
            replacement = Current(manager);
            if (replacement == null)
                throw new Exception("The activity card did not return after the toggle test.");

            if (!aggregator.Tick(false))
                throw new Exception("Minimized/modal frame did not park the visible card.");
            manager.Render();
            Pump();
            if (Current(manager) != null)
                throw new Exception("Parking a card left a topmost window behind.");

            if (!aggregator.FlushIfUsable(true))
                throw new Exception("A parked card did not flush when the frame became usable.");
            manager.Render();
            Pump();
            if (Current(manager) == null)
                throw new Exception("The parked activity card was not restored.");

            manager.DismissAllImmediate();
            if (Current(manager) != null)
                throw new Exception("DismissAllImmediate left a toast window behind.");

            if (!aggregator.RecordResult("revit_list_rooms", "After shutdown", true, null, true))
                throw new Exception("DismissAllImmediate left the aggregator render request stuck.");
            manager.Render();
            Pump();
            if (Current(manager) == null)
                throw new Exception("A result after DismissAllImmediate was not rendered.");
        }
        finally
        {
            manager.DismissAllImmediate();
        }
    }

    private static void CheckStationaryPointerFiltering()
    {
        var snapshot = new ActivitySnapshot(1, false, 1, 0, 0, "List Rooms", "Done", true, false);
        var window = new McpToastWindow(snapshot, null, null, null, null, null,
            () => new Point(100, 200));
        try
        {
            window.CapturePointerBaseline();
            var changed = (bool)typeof(McpToastWindow)
                .GetMethod("PointerPositionChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(window, null);
            if (changed)
                throw new Exception("A stationary pointer was treated as a real MouseEnter/Leave event.");
            window.SetPosition(30, 30);
            window.Show();
            window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            Pump();
            if (BrandRow(window).Visibility != Visibility.Hidden)
                throw new Exception("A stationary pointer must not reveal the wordmark.");
        }
        finally { window.CloseImmediate(); }
        Console.WriteLine("PASS: stationary pointer baseline filters synthetic WPF events");
    }

    private static void CheckStatusAndClickLifecycle()
    {
        var aggregator = new ActivityAggregator();
        var clicked = 0L;
        var manager = new McpToastManager(Dispatcher.CurrentDispatcher, aggregator,
            onClick: cardId => clicked = cardId);
        try
        {
            if (!aggregator.ShowStatus("Agent connected", "rvt-mcp is ready", 6))
                throw new Exception("The status card did not request a render.");
            manager.Render();
            Pump();

            var window = Current(manager);
            if (window == null || window.ViewModel.Title != "Agent connected")
                throw new Exception("The status card was not rendered in the shared slot.");

            var closeHost = (Border)typeof(McpToastWindow)
                .GetField("_closeHost", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .GetValue(window);
            var closeArgs = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent
            };
            closeHost.RaiseEvent(closeArgs);
            Pump();
            if (Current(manager) != null)
                throw new Exception("Clicking the × control did not dismiss the status card.");

            if (!aggregator.ShowStatus("Agent connected", "rvt-mcp is ready", 6))
                throw new Exception("The status card could not be recreated for the click test.");
            manager.Render();
            Pump();
            window = Current(manager);
            if (window == null)
                throw new Exception("The recreated status card was not rendered.");

            var cardId = window.CardId;
            var click = typeof(McpToastManager).GetMethod(
                "OnCardClicked",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (click == null)
                throw new Exception("The manager click callback seam is missing.");
            click.Invoke(manager, new object[] { cardId });
            Pump();

            if (clicked != cardId)
                throw new Exception("Clicking the card did not reach the host callback.");
            if (Current(manager) != null)
                throw new Exception("Clicking the card did not dismiss it.");

            Console.WriteLine("PASS: status card uses the shared slot and card click reaches the host callback");
        }
        finally
        {
            manager.DismissAllImmediate();
        }
    }

    private static McpToastWindow Current(McpToastManager manager)
    {
        return (McpToastWindow)typeof(McpToastManager)
            .GetField("_window", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .GetValue(manager);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void AssertPosition(McpToastWindow window, double top, double left)
    {
        if (!double.IsFinite(window.Top) || !double.IsFinite(window.Left)
            || Math.Abs(window.Top - top) > 1 || Math.Abs(window.Left - left) > 1)
            throw new Exception($"Expected ({top}, {left}); got ({window.Top}, {window.Left}).");
    }
}
