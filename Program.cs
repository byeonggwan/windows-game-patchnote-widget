using System;
using System.IO;
using PatchNoteMailbox.Services;
using PatchNoteMailbox.UI;

namespace PatchNoteMailbox
{
    internal static class Program
    {
        /// <summary>BWUI app id: names the data folder (%LOCALAPPDATA%\PatchNoteMailbox), the mutex and the Run key.</summary>
        public const string AppId = "PatchNoteMailbox";

        [STAThread]
        private static void Main(string[] args)
        {
            args = args ?? new string[0];
            var storage = new StorageService(Bwui.App.DataDir(AppId));
            var fetcher = new PatchNoteFetcher();

            // CLI test mode: --test-fetch <outputFile>
            if (args.Length >= 1 && args[0] == "--test-fetch")
            {
                string outFile = args.Length >= 2 ? args[1] : Path.Combine(storage.BaseDir, "test-fetch.log");
                TestFetch(storage, fetcher, outFile);
                return;
            }

            // --snapshot <png> [light|dark] [collapsed|popover]. The pre-BWUI form had no theme
            // ("--snapshot a.png collapsed"); treat that as light.
            if (args.Length >= 3 && args[0] == "--snapshot" && args[2] != "light" && args[2] != "dark")
            {
                var a = new string[args.Length + 1];
                a[0] = args[0]; a[1] = args[1]; a[2] = "light";
                Array.Copy(args, 2, a, 3, args.Length - 2);
                args = a;
            }

            // Single instance, software rendering, theme watching and --snapshot are handled by BWUI.
            Bwui.App.Run(AppId, args, delegate { return new DockWindow(storage, fetcher); });
        }

        private static void TestFetch(StorageService storage, PatchNoteFetcher fetcher, string outFile)
        {
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
        }
    }
}
