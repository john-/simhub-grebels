using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace SimHub.GRebels
{
    public class SettingsControl : UserControl
    {
        private readonly GRebelsTelemetryPlugin _plugin;
        private readonly DispatcherTimer _timer;

        private TextBlock _tbStatus;
        private TextBlock _tbVersion;
        private TextBlock _tbBind;
        private TextBlock _tbCraft;
        private TextBlock _tbLocation;
        private TextBlock _tbPackets;
        private TextBlock _tbRate;
        private TextBlock _tbLastLength;
        private TextBlock _tbAge;
        private TextBlock _tbMismatch;

        public SettingsControl(GRebelsTelemetryPlugin plugin)
        {
            _plugin = plugin;
            BuildUI();

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _timer.Tick += OnTick;

            Loaded += (s, e) =>
            {
                UpdateEndpointText();
                _timer.Start();
            };
            Unloaded += (s, e) => _timer.Stop();
        }

        private void UpdateEndpointText()
        {
            if (_tbVersion != null)
            {
                _tbVersion.Text = GRebelsTelemetryPlugin.PluginVersion;
            }

            var s = _plugin?.Settings;
            if (s != null && _tbBind != null)
            {
                _tbBind.Text = $"{s.BindAddress}:{s.Port} ({s.Fields?.Fields?.Count ?? 0} mapped fields)";
            }
        }

        private void BuildUI()
        {
            var root = new Grid
            {
                Margin = new Thickness(20)
            };

            var stack = new StackPanel();

            // Header with Icon
            var headerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 15)
            };

            if (_plugin?.PictureIcon != null)
            {
                var img = new Image
                {
                    Source = _plugin.PictureIcon,
                    Height = 36,
                    Margin = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                headerPanel.Children.Add(img);
            }

            var header = new TextBlock
            {
                Text = "G-Rebels Telemetry Plugin",
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            };
            headerPanel.Children.Add(header);
            stack.Children.Add(headerPanel);

            // Subtitle
            var subtitle = new TextBlock
            {
                Text = "UDP telemetry receiver and property bridge for G-Rebels (Steam appid 2445980).",
                FontSize = 13,
                Foreground = Brushes.LightGray,
                Margin = new Thickness(0, 0, 0, 20)
            };
            stack.Children.Add(subtitle);

            // Status Border / Group
            var statusBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(15),
                Margin = new Thickness(0, 0, 0, 20)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int row = 0;
            void AddRow(string label, out TextBlock valBlock)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var lbl = new TextBlock
                {
                    Text = label,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.LightGray,
                    Margin = new Thickness(0, 4, 10, 4)
                };
                Grid.SetRow(lbl, row);
                Grid.SetColumn(lbl, 0);
                grid.Children.Add(lbl);

                valBlock = new TextBlock
                {
                    Text = "-",
                    Foreground = Brushes.White,
                    Margin = new Thickness(0, 4, 0, 4)
                };
                Grid.SetRow(valBlock, row);
                Grid.SetColumn(valBlock, 1);
                grid.Children.Add(valBlock);

                row++;
            }

            AddRow("Plugin Version:", out _tbVersion);
            AddRow("Receiver Status:", out _tbStatus);
            AddRow("UDP Endpoint:", out _tbBind);
            AddRow("Active Craft:", out _tbCraft);
            AddRow("Location / Sector:", out _tbLocation);
            AddRow("Packets Received:", out _tbPackets);
            AddRow("Packet Rate:", out _tbRate);
            AddRow("Last Packet Size:", out _tbLastLength);
            AddRow("Telemetry Age:", out _tbAge);
            AddRow("Size Mismatches:", out _tbMismatch);

            statusBorder.Child = grid;
            stack.Children.Add(statusBorder);

            // Info note
            var note = new TextBlock
            {
                Text = "Note: Telemetry properties are prefixed with 'GRebels.*' and can be mapped in SimHub dashes, bass shakers, and overlays.",
                FontStyle = FontStyles.Italic,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0)
            };
            stack.Children.Add(note);

            // Links panel
            var linksPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, 15, 0, 0)
            };

            var gitLinkBlock = new TextBlock { FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
            var gitHyperlink = new Hyperlink(new Run("GitHub Repository & Documentation (github.com/john-/simhub-grebels)"))
            {
                NavigateUri = new Uri("https://github.com/john-/simhub-grebels"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x58, 0xa6, 0xff))
            };
            gitHyperlink.RequestNavigate += (s, e) =>
            {
                try { if (e.Uri != null) Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
                e.Handled = true;
            };
            gitLinkBlock.Inlines.Add(gitHyperlink);
            linksPanel.Children.Add(gitLinkBlock);

            var docLinkBlock = new TextBlock { FontSize = 12 };
            var docHyperlink = new Hyperlink(new Run("Official G-Rebels Telemetry Specification (reakktor.com/htm/gr_telemetry.htm)"))
            {
                NavigateUri = new Uri("https://reakktor.com/htm/gr_telemetry.htm"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x58, 0xa6, 0xff))
            };
            docHyperlink.RequestNavigate += (s, e) =>
            {
                try { if (e.Uri != null) Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
                e.Handled = true;
            };
            docLinkBlock.Inlines.Add(docHyperlink);
            linksPanel.Children.Add(docLinkBlock);

            stack.Children.Add(linksPanel);

            root.Children.Add(stack);
            Content = root;
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_plugin == null) return;

            var r = _plugin.Receiver;
            var s = _plugin.Settings;

            if (r == null)
            {
                _tbStatus.Text = "Stopped / Not running";
                _tbStatus.Foreground = Brushes.OrangeRed;
                return;
            }

            _tbStatus.Text = _plugin.IsFeedStale ? "Active (Waiting for data)" : "Active (Receiving)";
            _tbStatus.Foreground = _plugin.IsFeedStale ? Brushes.Gold : Brushes.LightGreen;

            _tbPackets.Text = r.PacketCount.ToString("N0");
            _tbRate.Text = $"{r.PacketsPerSecond:0.0} pkt/sec";
            _tbLastLength.Text = $"{r.LastLength} bytes";
            _tbAge.Text = r.Current.HasData ? $"{r.Current.AgeSeconds:0.0} s" : "N/A (No packets)";
            _tbMismatch.Text = r.LengthMismatchCount.ToString("N0");

            int vehIdx = s?.CompiledFields?.GetStringIndex("VehicleName") ?? -1;
            int locIdx = s?.CompiledFields?.GetStringIndex("Location") ?? -1;
            _tbCraft.Text = !_plugin.IsFeedStale && vehIdx >= 0 ? (r.Current.GetString(vehIdx) ?? "-") : "-";
            _tbLocation.Text = !_plugin.IsFeedStale && locIdx >= 0 ? (r.Current.GetString(locIdx) ?? "-") : "-";
        }
    }
}
