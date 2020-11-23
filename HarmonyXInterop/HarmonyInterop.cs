﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using HarmonyLib.Public.Patching;
using HarmonyLib.Tools;
using Mono.Cecil;

namespace HarmonyXInterop
{
    public static class HarmonyInterop
    {
        private const string BACKUP_PATH = "BepInEx_Shim_Backup";
        
        private static readonly SortedDictionary<Version, string> Assemblies = new SortedDictionary<Version, string>();

        private static readonly Func<MethodBase, PatchInfo, MethodInfo> UpdateWrapper =
            AccessTools.MethodDelegate<Func<MethodBase, PatchInfo, MethodInfo>>(
                AccessTools.Method(typeof(HarmonyManipulator).Assembly.GetType("HarmonyLib.PatchFunctions"),
                    "UpdateWrapper"));

        private static readonly Action<Logger.LogChannel, Func<string>> HarmonyLog =
            AccessTools.MethodDelegate<Action<Logger.LogChannel, Func<string>>>(AccessTools.Method(typeof(Logger),
                "Log"));

        private static readonly Action<Logger.LogChannel, string> HarmonyLogText =
            AccessTools.MethodDelegate<Action<Logger.LogChannel, string>>(AccessTools.Method(typeof(Logger),
                "LogText"));
        
        private static readonly Dictionary<string, long> shimCache = new Dictionary<string, long>();
        private static BinaryWriter cacheWriter;

        public static void Log(int channel, Func<string> message)
        {
            HarmonyLog((Logger.LogChannel) channel, message);
        }
        
        public static void LogText(int channel, string message)
        {
            HarmonyLogText((Logger.LogChannel) channel, message);
        }
        
        public static void Initialize(string cachePath)
        {
            Directory.CreateDirectory(cachePath);
            var cacheFile = Path.Combine(cachePath, "harmony_interop_cache.dat");
            var curDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            foreach (var file in Directory.GetFiles(curDir, "0Harmony*.dll", SearchOption.AllDirectories))
            {
                using var ass = AssemblyDefinition.ReadAssembly(file);
                // Don't add normal Harmony, resolve it normally
                if (ass.Name.Name != "0harmony")
                    Assemblies.Add(ass.Name.Version, ass.Name.Name);
            }

            if (File.Exists(cacheFile))
            {
                try
                {
                    using var br = new BinaryReader(File.OpenRead(cacheFile));
                    while (true)
                    {
                        var file = br.ReadString();
                        var writeTime = br.ReadInt64();
                        shimCache[file] = writeTime;
                    }
                }
                catch (Exception)
                {
                    // Skip
                }
            }

            try
            {
                 var cw = new BinaryWriter(File.Create(cacheFile));
                 cacheWriter = cw;
                 foreach (var kv in shimCache)
                 {
                     cacheWriter.Write(kv.Key);
                     cacheWriter.Write(kv.Value);
                 }
                 cacheWriter.Flush();
            }
            catch (IOException)
            {
                // Sharing violation can happen; in that case simply ignore writing the cache
            }
        }

        public static byte[] TryShim(string path, string gameRootDirectory, Action<string> logMessage = null, ReaderParameters readerParameters = null)
        {
            try
            {
                if (!File.Exists(path))
                    return null;
            }
            catch (Exception e)
            {
                logMessage?.Invoke($"Failed to read path {path}: {e}");
                return null;
            }
            byte[] result = null;
            var lastWriteTime = File.GetLastWriteTimeUtc(path).Ticks;
            if (shimCache.TryGetValue(path, out var cachedWriteTime) && cachedWriteTime == lastWriteTime)
                return null;
            try
            {
                // Read via MemoryStream to prevent sharing violation
                // This is only a problem on the first run; the cache prevents this from happening often
                var origBytes = File.ReadAllBytes(path);
                using var ms = new MemoryStream(origBytes);
                using var ad = AssemblyDefinition.ReadAssembly(ms, readerParameters ?? new ReaderParameters());
                var harmonyRef = ad.MainModule.AssemblyReferences.FirstOrDefault(a => a.Name == "0Harmony");
                if (harmonyRef != null)
                {
                    var assToLoad = Assemblies.LastOrDefault(kv => kv.Key <= harmonyRef.Version);
                    if (assToLoad.Value != null)
                    {
                        logMessage?.Invoke($"Shimming {path} to use older version of Harmony ({assToLoad.Value}). Please update the plugin if possible.");
                        harmonyRef.Name = assToLoad.Value;
                        // Write via intermediate MemoryStream to prevent DLL corruption
                        using var outputMs = new MemoryStream();
                        ad.Write(outputMs);
                        try
                        {
                            var backupPath = Path.Combine(gameRootDirectory, BACKUP_PATH);
                            var midPath = Path.GetDirectoryName(Path.GetFullPath(path).Substring(gameRootDirectory.Length + 1));
                            var backupDir = Path.Combine(backupPath, midPath);
                            Directory.CreateDirectory(backupDir);
                            File.WriteAllBytes(Path.Combine(backupDir, Path.GetFileName(path)), origBytes);
                            
                            File.WriteAllBytes(path, outputMs.ToArray());
                            lastWriteTime = File.GetLastWriteTimeUtc(path).Ticks;
                        }
                        catch (IOException)
                        {
                            // Skip possible sharing violation, but in that case force to refresh the cache
                            lastWriteTime = 0;
                        }

                        result = outputMs.ToArray();
                    }
                }

                shimCache[path] = lastWriteTime;
                if (cacheWriter != null)
                {
                    cacheWriter.Write(path);
                    cacheWriter.Write(lastWriteTime);
                    cacheWriter.Flush();    
                }
            }
            catch (Exception e)
            {
                logMessage?.Invoke($"Failed to shim {path}: {e}");
            }

            return result;
        }

        public static void ApplyPatch(MethodBase target, PatchInfoWrapper add, PatchInfoWrapper remove)
        {
            static PatchMethod[] WrapTranspilers(PatchMethod[] transpilers) => transpilers.Select(p => new PatchMethod
            {
                after = p.after,
                before = p.before,
                method = TranspilerInterop.WrapInterop(p.method),
                owner = p.owner,
                priority = p.priority
            }).ToArray();
            
            var pInfo = target.ToPatchInfo();
            lock (pInfo)
            {
                pInfo.prefixes = Sync(add.prefixes, remove.prefixes, pInfo.prefixes);
                pInfo.postfixes = Sync(add.postfixes, remove.postfixes, pInfo.postfixes);
                pInfo.transpilers = Sync(WrapTranspilers(add.transpilers), WrapTranspilers(remove.transpilers), pInfo.transpilers);
                pInfo.finalizers = Sync(add.finalizers, remove.finalizers, pInfo.finalizers);
            }

            UpdateWrapper(target, pInfo);
        }

        private static Patch[] Sync(PatchMethod[] add, PatchMethod[] remove, Patch[] current)
        {
            if (add.Length == 0 && remove.Length == 0)
                return current;
            current = current.Where(p => !remove.Any(r => r.method == p.PatchMethod && r.owner == p.owner)).ToArray();
            var initialIndex = current.Length;
            return current.Concat(add.Where(method => method != null).Select((method, i) =>
                new Patch(method.ToHarmonyMethod(), i + initialIndex, method.owner))).ToArray();
        }
    }
}