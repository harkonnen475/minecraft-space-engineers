using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SEMCLab
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var instance = new Mutex(true, "Local\\SEMC-Lab.ControlPanel", out created))
            {
                if (!created) { MessageBox.Show("Panel laboratorium jest już otwarty.", "Minecraft × Space Engineers"); return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new ControlPanel());
            }
        }
    }

    sealed class BridgeStatus
    {
        public bool Valid, MinecraftActive;
        public long MinecraftTimestamp, SpaceEngineersTimestamp;
        public long MinecraftAge { get { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - MinecraftTimestamp; } }
        public long SpaceEngineersAge { get { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - SpaceEngineersTimestamp; } }
        public int Blocks, MirroredBlocks, SpaceEngineersState, OriginX, OriginY, OriginZ;
    }

    sealed class ControlPanel : Form
    {
        readonly string root = AppDomain.CurrentDomain.BaseDirectory;
        readonly Label minecraft = new Label(), engineers = new Label(), details = new Label(), message = new Label();
        readonly Button connect = new Button(), recenter = new Button(), reposition = new Button();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly Color green = Color.FromArgb(21, 120, 76), muted = Color.FromArgb(85, 96, 113);
        bool busy;
        BridgeStatus cachedStatus;
        long cachedStatusAt;

        public ControlPanel()
        {
            Text = "Minecraft x Space Engineers — Lab";
            Font = new Font("Segoe UI", 10F);
            BackColor = Color.FromArgb(246, 248, 251);
            ForeColor = Color.FromArgb(24, 35, 52);
            ClientSize = new Size(700, 575);
            MinimumSize = new Size(700, 610);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 8 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            Controls.Add(layout);

            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
            heading.Controls.Add(new Label { Text = "Minecraft × Space Engineers", Font = new Font("Segoe UI", 21F, FontStyle.Bold), AutoSize = true, Margin = Padding.Empty });
            heading.Controls.Add(new Label { Text = "Laboratorium połączenia światów", AutoSize = true, ForeColor = muted, Margin = Padding.Empty });
            layout.Controls.Add(heading, 0, 0);
            layout.Controls.Add(new Label { Text = "1. Uruchom Minecraft Java i wejdź do świata.\n2. Kliknij Połącz. Space Engineers uruchomi kopię testową świata.", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 1);

            var statusBox = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(16, 12, 16, 10), RowCount = 3, ColumnCount = 1, Margin = new Padding(0, 0, 0, 12) };
            foreach (var label in new[] { minecraft, engineers, details }) { label.Dock = DockStyle.Fill; label.AutoSize = true; statusBox.Controls.Add(label); }
            minecraft.Font = engineers.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            details.ForeColor = muted;
            layout.Controls.Add(statusBox, 0, 2);

            ConfigureButton(connect, "Połącz światy", true);
            connect.Click += async (sender, e) => await ConnectAsync();
            layout.Controls.Add(connect, 0, 3);
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 10, 0, 0) };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            ConfigureButton(recenter, "Pobierz teren wokół mnie", false);
            ConfigureButton(reposition, "Przenieś podgląd przede mnie", false);
            recenter.Margin = new Padding(0, 0, 6, 0);
            reposition.Margin = new Padding(6, 0, 0, 0);
            recenter.Click += async (sender, e) => await RecenterAsync();
            reposition.Click += (sender, e) => {
                try { File.WriteAllText(Path.Combine(root, "se-recenter.flag"), "requested from control panel"); SetMessage("Podgląd pojawi się przed postacią w Space Engineers.", false); }
                catch (Exception error) { SetMessage(error.Message, true); }
            };
            actions.Controls.Add(recenter, 0, 0); actions.Controls.Add(reposition, 1, 0);
            layout.Controls.Add(actions, 0, 4);
            message.Dock = DockStyle.Fill; message.Padding = new Padding(0, 10, 0, 0); message.ForeColor = muted;
            layout.Controls.Add(message, 0, 5);
            layout.Controls.Add(new Label { Text = "Podgląd obejmuje fragment 12 × 8 × 12 bloków. Jeden blok ma 0,5 m.\nPrzyciski panelu nie zmieniają bloków w Minecraft. Zamknięcie panelu pozostawia gry uruchomione.", ForeColor = muted, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9F), Padding = new Padding(0, 6, 0, 0) }, 0, 6);
            var folder = new LinkLabel { Text = "Otwórz folder laboratorium", AutoSize = true, LinkColor = Color.FromArgb(30, 94, 182), Margin = Padding.Empty };
            folder.LinkClicked += (sender, e) => Process.Start(new ProcessStartInfo(root) { UseShellExecute = true });
            layout.Controls.Add(folder, 0, 7);
            timer.Interval = 750; timer.Tick += (sender, e) => RefreshStatus(); timer.Start();
            FormClosed += (sender, e) => timer.Dispose();
            RefreshStatus();
            SetMessage("Gotowe. Otwórz świat Minecraft i kliknij Połącz.", false);
        }

        void ConfigureButton(Button button, string text, bool primary)
        {
            button.Text = text; button.Dock = DockStyle.Fill; button.Margin = Padding.Empty;
            button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(205, 213, 224);
            button.BackColor = primary ? Color.FromArgb(31, 102, 202) : Color.White;
            button.ForeColor = primary ? Color.White : ForeColor;
            button.Font = new Font("Segoe UI", primary ? 11F : 9F, FontStyle.Bold);
            button.Cursor = Cursors.Hand;
        }

        static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
        static bool Fresh(long age) { return age >= -30000 && age < 10000; }
        BridgeStatus ReadStatus()
        {
            var result = new BridgeStatus();
            try
            {
                using (var stream = new FileStream(Path.Combine(root, "bridge.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length != 65536) return result;
                    using (var map = MemoryMappedFile.CreateFromFile(stream, null, 65536, MemoryMappedFileAccess.Read, null, HandleInheritability.None, false))
                    using (var view = map.CreateViewAccessor(0, 65536, MemoryMappedFileAccess.Read))
                    {
                        var snapshot = new byte[32768];
                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            int sequence = view.ReadInt32(8);
                            if ((sequence & 1) != 0) continue;
                            Thread.MemoryBarrier(); view.ReadArray(0, snapshot, 0, snapshot.Length); Thread.MemoryBarrier();
                            if (view.ReadInt32(8) != sequence) continue;
                            if (BitConverter.ToInt32(snapshot, 0) != 0x434D4553 || BitConverter.ToInt32(snapshot, 4) != 1 || BitConverter.ToInt32(snapshot, 80) != 1152) return result;
                            result.Valid = true;
                            result.MinecraftTimestamp = BitConverter.ToInt64(snapshot, 40);
                            result.MinecraftActive = (BitConverter.ToInt32(snapshot, 36) & 1) != 0;
                            result.SpaceEngineersTimestamp = view.ReadInt64(32792);
                            result.SpaceEngineersState = view.ReadInt32(32804);
                            result.MirroredBlocks = view.ReadInt32(32800);
                            result.OriginX = BitConverter.ToInt32(snapshot, 24); result.OriginY = BitConverter.ToInt32(snapshot, 28); result.OriginZ = BitConverter.ToInt32(snapshot, 32);
                            for (int i = 0; i < 1152; i++) if (BitConverter.ToInt32(snapshot, 128 + i * 8) != 0) result.Blocks++;
                            return result;
                        }
                    }
                }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            return result;
        }

        BridgeStatus CurrentStatus()
        {
            var current = ReadStatus();
            if (current.Valid) { cachedStatus = current; cachedStatusAt = Now(); }
            // A concurrent snapshot write can briefly fail the sequence check.
            // Keep a recent coherent reading; its heartbeat ages still advance.
            return !current.Valid && cachedStatus != null && Now() - cachedStatusAt < 1500 ? cachedStatus : current;
        }

        void RefreshStatus()
        {
            var status = CurrentStatus();
            bool mcFresh = status.Valid && Fresh(status.MinecraftAge), seFresh = status.Valid && Fresh(status.SpaceEngineersAge);
            minecraft.Text = "Minecraft: " + (mcFresh ? (status.MinecraftActive ? "połączony — świat wczytany" : "połączony — wejdź do świata") : "czeka na połączenie");
            engineers.Text = "Space Engineers: " + (seFresh ? (status.SpaceEngineersState == 2 ? "połączony — świat wczytany" : "połączony — oczekiwanie na świat") : "czeka na połączenie");
            minecraft.ForeColor = mcFresh && status.MinecraftActive ? green : muted;
            engineers.ForeColor = seFresh && status.SpaceEngineersState == 2 ? green : muted;
            details.Text = mcFresh ? (status.Blocks + " bloków terenu · " + (seFresh ? status.MirroredBlocks.ToString() : "0") + " w podglądzie SE") : "Stan połączenia odświeża się automatycznie.";
            recenter.Enabled = !busy && mcFresh && status.MinecraftActive;
            reposition.Enabled = !busy && seFresh && status.SpaceEngineersState == 2;
        }

        async Task ConnectAsync()
        {
            if (busy) return; SetBusy(true);
            try
            {
                var status = CurrentStatus();
                if (!(status.Valid && Fresh(status.MinecraftAge)))
                {
                    SetMessage("Łączenie z uruchomionym Minecraft Java…", false);
                    string stopFlag = Path.Combine(root, "mc-stop.flag");
                    if (File.Exists(stopFlag)) File.Delete(stopFlag);
                    await RunScriptAsync(Path.Combine(root, "minecraft-bridge", "attach.ps1"));
                }
                else SetMessage("Minecraft jest już połączony. Przygotowywanie Space Engineers…", false);
                status = CurrentStatus();
                bool seLoaded = status.Valid && Fresh(status.SpaceEngineersAge) && status.SpaceEngineersState == 2;
                string loadFlag = Path.Combine(root, "load-test-world.flag");
                if (seLoaded)
                {
                    if (File.Exists(loadFlag)) File.Delete(loadFlag);
                }
                else File.WriteAllText(loadFlag, "requested from control panel");
                if (Process.GetProcessesByName("SpaceEngineers").Length > 0 && !(status.Valid && Fresh(status.SpaceEngineersAge)))
                    throw new IOException("Zamknij zwykłe okno Space Engineers i kliknij Połącz. Panel uruchomi grę z połączeniem do Minecraft.");
                bool seRunning = Process.GetProcessesByName("Legacy").Length > 0 || Process.GetProcessesByName("SpaceEngineers").Length > 0;
                if (!seRunning)
                {
                    SetMessage("Uruchamianie Space Engineers. Wczytanie świata może potrwać chwilę…", false);
                    await RunScriptAsync(Path.Combine(root, "start-se.ps1"));
                }
                if (seLoaded && Fresh(status.MinecraftAge) && status.MinecraftActive)
                    SetMessage("Obie gry są połączone. Zmieniaj bloki w Minecraft i zobacz je w SE.", false);
                else SetMessage("Połączenie przygotowane. Poczekaj na wczytanie świata testowego w Space Engineers.", false);
            }
            catch (Exception error) { SetMessage(ReadableError(error.Message), true); }
            finally { SetBusy(false); RefreshStatus(); }
        }

        async Task RecenterAsync()
        {
            if (busy) return; SetBusy(true);
            try
            {
                SetMessage("Pobieranie terenu wokół postaci w Minecraft…", false);
                await Task.Run(() => {
                    using (var stream = new FileStream(Path.Combine(root, "bridge.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                    using (var map = MemoryMappedFile.CreateFromFile(stream, null, 65536, MemoryMappedFileAccess.ReadWrite, null, HandleInheritability.None, false))
                    using (var view = map.CreateViewAccessor())
                    {
                        if (view.ReadInt32(0) != 0x434D4553 || view.ReadInt32(4) != 1) throw new IOException("Nieprawidłowy stan połączenia. Kliknij Połącz.");
                        int previous = view.ReadInt32(32768);
                        if (previous != 0 && view.ReadInt32(84) != previous) throw new IOException("Poprzednia operacja jeszcze trwa. Spróbuj ponownie za chwilę.");
                        if (previous == int.MaxValue) throw new IOException("Uruchom ponownie połączenie przed dalszym użyciem.");
                        int sequence = previous + 1;
                        view.Write(32772, 3); view.Write(32776, 0); view.Write(32780, 0); view.Write(32784, 0);
                        Thread.MemoryBarrier(); view.Write(32768, sequence);
                        long deadline = Now() + 8000;
                        while (view.ReadInt32(84) != sequence && Now() < deadline) Thread.Sleep(100);
                        if (view.ReadInt32(84) != sequence) throw new IOException("Minecraft nie odpowiedział. Wróć do aktywnego świata i spróbuj ponownie.");
                        if (view.ReadInt32(88) != 1) throw new IOException("Nie udało się pobrać terenu. Wróć do aktywnego świata Minecraft.");
                    }
                });
                SetMessage("Gotowe — podgląd pokazuje teren wokół postaci w Minecraft.", false);
            }
            catch (Exception error) { SetMessage(ReadableError(error.Message), true); }
            finally { SetBusy(false); RefreshStatus(); }
        }

        Task RunScriptAsync(string script)
        {
            return Task.Run(() => {
                if (!File.Exists(script)) throw new FileNotFoundException("Brakuje pliku laboratorium: " + Path.GetFileName(script));
                var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")) {
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"", WorkingDirectory = root,
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true
                };
                using (var process = Process.Start(start))
                {
                    var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                    process.WaitForExit(); Task.WaitAll(output, error);
                    if (process.ExitCode != 0) throw new IOException(error.Result.Trim());
                }
            });
        }

        string ReadableError(string error)
        {
            if (error.Contains("No running Minecraft")) return "Uruchom Minecraft Java, wejdź do świata i ponownie kliknij Połącz.";
            if (error.Contains("Multiple Minecraft")) return "Uruchom tylko jedno okno Minecraft Java, a następnie kliknij Połącz.";
            if (error.Contains("already running")) return "Space Engineers jest już uruchomiony. Poczekaj na połączenie w otwartym oknie gry.";
            if (error.Contains("mc-stop.flag")) return "Połączenie Minecraft zostało zatrzymane. Kliknij ponownie Połącz, aby je wznowić.";
            if (String.IsNullOrWhiteSpace(error)) return "Nie udało się połączyć gier. Sprawdź, czy Minecraft Java jest uruchomiony.";
            return error.Length > 260 ? error.Substring(0, 260) : error;
        }

        void SetBusy(bool value) { busy = value; connect.Enabled = !value; connect.Text = value ? "Łączenie…" : "Połącz światy"; RefreshStatus(); }
        void SetMessage(string text, bool error) { message.Text = text; message.ForeColor = error ? Color.FromArgb(170, 52, 45) : muted; }
    }
}
