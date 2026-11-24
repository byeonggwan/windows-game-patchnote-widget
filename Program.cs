using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PatchNoteMailbox.Services;
using PatchNoteMailbox.UI;

namespace PatchNoteMailbox
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // Software rendering keeps RAM usage minimal (same as AiUsageWidget)
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

            string baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PatchNoteMailbox");
            Directory.CreateDirectory(baseDir);

            var storage = new StorageService(baseDir);
            var fetcher = new PatchNoteFetcher();

            // CLI snapshot mode: --snapshot <outPng> [expanded|collapsed|popover]
            if (args != null && args.Length >= 2 && args[0] == "--snapshot")
            {
                string mode = args.Length >= 3 ? args[2] : "expanded";
                DockWindow.RenderSnapshot(storage, fetcher, args[1], mode);
                return;
            }

            // CLI test mode: --test-fetch <outputFile>
            if (args != null && args.Length >= 1 && args[0] == "--test-fetch")
            {
                string outFile = args.Length >= 2 ? args[1] : Path.Combine(baseDir, "test-fetch.log");
                using (var sw = new StreamWriter(outFile, false, System.Text.Encoding.UTF8))
                {
                    var games = storage.LoadGames();
                    foreach (var g in games)
                    {
                        sw.WriteLine("=== " + g.Name + " (" + g.Id + ") ===");
                        try
                        {
                            fetcher.EnsureIconCachedAsync(g, storage).Wait();
                            bool iconOk = File.Exists(storage.GetLocalIconPath(g.Id));
                            sw.WriteLine("Icon cached: " + iconOk);
                            var notes = fetcher.FetchAsync(g).Result;
                            sw.WriteLine("Fetched count: " + notes.Count);
                            int limit = Math.Min(5, notes.Count);
                            for (int i = 0; i < limit; i++)
                            {
                                sw.WriteLine(string.Format("  [{0}] #{1} | {2} | {3}", i + 1, notes[i].Idx, notes[i].Date, notes[i].Title));
                            }
                        }
                        catch (Exception ex)
                        {
                            sw.WriteLine("ERROR: " + ex.ToString());
                        }
                    }
                }
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, "Local\\PatchNoteMailbox_Single", out created))
            {
                if (!created) return;
                var app = new Application();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                app.Run(new DockWindow(storage, fetcher));
            }
        }
    }
}
