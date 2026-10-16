using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

internal static class Launcher
{
    [STAThread]
    static int Main()
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        string log = Path.Combine(root, "launcher.log");
        try
        {
            var start = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + Path.Combine(root, "Launch.ps1") + "\"");
            start.WorkingDirectory = root; start.UseShellExecute = false;
            start.CreateNoWindow = true; start.WindowStyle = ProcessWindowStyle.Hidden;
            start.RedirectStandardOutput = true; start.RedirectStandardError = true;
            using (var process = new Process())
            using (var output = new StreamWriter(log, true))
            {
                object sync = new object();
                process.StartInfo = start;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock(sync) { output.WriteLine(e.Data); output.Flush(); } };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock(sync) { output.WriteLine(e.Data); output.Flush(); } };
                output.WriteLine(DateTime.Now.ToString("O") + " Start Minecraft x Space Engineers");
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    string errorFile = Path.Combine(root, "launcher-error.txt");
                    string message = File.Exists(errorFile) ? File.ReadAllText(errorFile) : "";
                    if (String.IsNullOrWhiteSpace(message)) message = "Uruchamianie nie zostało ukończone.";
                    throw new InvalidOperationException(message + "\n\nSzczegóły: " + log);
                }
            }
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Minecraft × Space Engineers", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
