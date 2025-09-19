using System.Diagnostics;
using CEM.Client.ZoneExporter;
using CEM.Utils;

namespace CEM.World
{
    internal static class NavmeshMgr
    {
        public const float CONVERSION_FACTOR = 1.0f / 32f;

        public static void BuildNavMesh(Zone2 z)
        {
            if (z.Name == "ArtOutside" || z.Name == "ArtInside")
            {
                Log.Normal("Skipping zone {0} because it has name {1}", z, z.Name);
                return;
            }
            if (z.ProxyZone != 0)
            {
                Log.Normal("Skipping zone {0} because it has a proxy zone id {1}", z, z.ProxyZone);
                return;
            }

            string obj = z.ObjFile;
            string nav = z.NavFile.Replace(".gz", "");

            // Create .obj
            DateTime start = DateTime.Now;
            Log.Normal("Building navmesh for zone {0}...", z);
            if (File.Exists(obj))
                File.Delete(obj);
            using (var exp = new Zone2Obj(z))
                exp.Export();

            if (Program.Arguments.ExportObjOnly)
                return;

            // .obj -> .nav
            Log.Normal("Running RecastDemo.exe for {0}", z.Name);
            Process buildnav = Process.Start("RecastDemo.exe", [obj.Replace(".obj", ".gset"), nav]);
            buildnav.PriorityClass = ProcessPriorityClass.BelowNormal;
            buildnav.WaitForExit();
            if (buildnav.ExitCode > 0)
                throw new InvalidOperationException("RecastDemo.exe failed with " + buildnav.ExitCode);
            if (!File.Exists(nav))
            {
                Log.Error("Did not generate navmesh for file {0} for unknown reasons", nav);
            }
            else if (new FileInfo(nav).Length < 2048)
            {
                // empty mesh
                Log.Warn("{0} was empty :(", nav);
                File.Delete(nav);
            }

            Log.Normal("Zone {0} finished in {1}", z, DateTime.Now - start);
        }
    }
}
