// Automatic desync check. Every 10 s the host sends a short summary of its game (clock, units,
// enemies, buildings, resources). Each friend compares it with its own game. When they stay
// different for 30 s the friend saves a bug report on its own and shows a hint to press F9.
// settings.txt auto_resync=1 asks the host for a re-sync automatically instead (at most every 3 min,
// at most 3 times per session). Resources are corrected by the resource sync, not compared here.
using System;
using NetKit;
using UnityEngine;

namespace DinoNet
{
    public static class DesyncCheck
    {
        static float sendT, lastBadReport = -1000f, lastAuto = -1000f;
        static int badRuns, autoCount;
        public static string LastResult = "";
        public static int Checks, BadChecks;

        public static void Reset() { badRuns = 0; LastResult = ""; }
        public static void ResetSession() { Reset(); autoCount = 0; }

        public static void Update()
        {
            if (!Net.IsHost || !Net.Active || !WorldSync.Running || !Ecs.GameRunning || Game.Held) return;
            float now = Time.realtimeSinceStartup;
            if (now < sendT) return;
            sendT = now + 10f;
            int u, e, b;
            WorldSync.Count(out u, out e, out b);
            var p = new Packer().F64(Ecs.ElapsedGameTime).UV((ulong)u).UV((ulong)e).UV((ulong)b);
            Res.Write(p);
            Net.ToAll(Msg.Check, p.ToArray());
        }

        public static void OnMessage(int from, byte[] d)
        {
            if (Net.IsHost || from != 0 || !WorldSync.Running || !Ecs.GameRunning || Game.Held) return;
            if (Time.realtimeSinceStartup - WorldSync.StartedAt < 20f) return;   // still settling in
            var r = new Unpacker(d);
            double hostTime = r.F64();
            int hu = (int)r.UV(), he = (int)r.UV(), hb = (int)r.UV();
            int lu, le, lb;
            WorldSync.Count(out lu, out le, out lb);
            double lt = Ecs.ElapsedGameTime;
            // allowances: the summary is a ping old; fights kill units in between
            var why = new System.Text.StringBuilder();
            if (Math.Abs(hu - lu) > Math.Max(3, hu / 10)) why.Append(" units ").Append(lu).Append(" vs host ").Append(hu);
            if (Math.Abs(hb - lb) > Math.Max(1, hb / 20)) why.Append(" buildings ").Append(lb).Append(" vs host ").Append(hb);
            if (Math.Abs(he - le) > Math.Max(8, he / 5)) why.Append(" enemies ").Append(le).Append(" vs host ").Append(he);
            if (Math.Abs(hostTime - lt) > 6.0) why.Append(" clock ").Append(lt.ToString("0")).Append("s vs host ").Append(hostTime.ToString("0")).Append('s');
            Checks++;
            LastResult = why.Length == 0 ? "in sync" : "DIFFERENT:" + why;
            Flight.Add("[check] " + LastResult + " (host u/e/b " + hu + "/" + he + "/" + hb + ", here " + lu + "/" + le + "/" + lb + ")");
            if (why.Length == 0) { badRuns = 0; return; }
            BadChecks++;
            badRuns++;
            if (badRuns < 3) return;   // 30 s in a row
            float now = Time.realtimeSinceStartup;
            if (Prefs.AutoResync && now - lastAuto > 180f && autoCount < 3)
            {
                lastAuto = now; badRuns = 0; autoCount++;
                Log.Warn("desync:" + why + " - asking the host for a re-sync (auto_resync=1)");
                Ui.Toast("Your game drifted from the host's - re-syncing automatically", 5f);
                Flight.Dump("desync-auto-resync", true);
                Net.ToHost(Msg.ResyncRequest, new byte[0]);
                return;
            }
            if (now - lastBadReport > 120f)
            {
                lastBadReport = now;
                Log.Warn("desync:" + why);
                Flight.Dump("desync", true);
                Ui.Toast("Your game looks out of sync with the host (" + why.ToString().Trim() + ") - press F9 to re-sync", 8f);
            }
        }
    }
}
