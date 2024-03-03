using Mutagen.Bethesda;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Mutagen.Bethesda.Skyrim.Records.Tooling;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.FormKeys.SkyrimSE;
using System;
using System.Runtime.ExceptionServices;
using DynamicData;

namespace NavmeshSpawner {


    public enum SpawnPrevention {
        Id,
        Root,
        Faction,
        Never
    }

    public class EnemySettings {
        public int minNearby = 2;
        public double preventionFactor = 1.25;
        public bool ignoreDead = true;
        public bool spawnRoot = true;
        public double[] clusterChance = [200, 20, 30, 22, 15, 9, 3];
        public double clusterDistance = 900;

        public SpawnPrevention spawnPrevention = SpawnPrevention.Faction;
    }
    public class Settings {
        public double maxDistance = 8000;
        public double verticalWeight = 0.5;
        public double minDistance = 600;
        public EnemySettings enemySettings = new();
    }

    record NpcInfo(bool IsValid, HashSet<FormKey> Factions);
    record PlacedNpcInfo {
        public IPlacedNpcGetter PlacedNpc { get; init; }

        public PlacedNpcInfo(IPlacedNpcGetter placedNpc) {
            PlacedNpc = placedNpc;
        }
    };
    public class Program {

        static Lazy<Settings> _Settings = null!;
        public static Settings Settings => _Settings.Value;

        public static async Task<int> Main(string[] args) {
            return await SynthesisPipeline.Instance
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
                .SetAutogeneratedSettings(
                    nickname: "Settings",
                    path: "settings.json",
                    out _Settings)
                .SetTypicalOpen(GameRelease.SkyrimSE, "NavmeshSpawner.esp")
                .Run(args);
        }

        private static bool NpcIsValidSpawnRec(INpcSpawnGetter npcSpawnGetter, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, bool containsLeveledChar) {
            if (npcSpawnGetter is INpcGetter npcGetter) {
                if (!npcGetter.Template.IsNull) {
                    if (npcGetter.Template.TryResolve(linkCache, out var template)) {
                        return NpcIsValidSpawnRec(template, linkCache, containsLeveledChar);
                    }
                } else {
                    var flags = npcGetter.Configuration.Flags;
                    return containsLeveledChar && !flags.HasFlag(NpcConfiguration.Flag.Unique) && !flags.HasFlag(NpcConfiguration.Flag.Essential) && flags.HasFlag(NpcConfiguration.Flag.Respawn);
                }
            }
            if (npcSpawnGetter is ILeveledNpcGetter leveledNpcGetter) {
                if (leveledNpcGetter.Entries != null && leveledNpcGetter.Entries.Count > 0) {
                    var data = leveledNpcGetter.Entries[0].Data;
                    if (data != null && data.Reference.TryResolve(linkCache, out var reference)) {
                        return NpcIsValidSpawnRec(reference, linkCache, true);
                    }
                }
            }
            return false;
        }

        private static bool NpcIsValidSpawn(INpcSpawnGetter npcSpawnGetter, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache) {
            return NpcIsValidSpawnRec(npcSpawnGetter, linkCache, false);
        }

        private static HashSet<FormKey> GetFactions(INpcSpawnGetter npcSpawnGetter, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache) {
            if (npcSpawnGetter is INpcGetter npcGetter) {
                if (npcGetter.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Factions)) {
                    if (npcGetter.Template.TryResolve(linkCache, out var template)) {
                        return GetFactions(template, linkCache);
                    }
                } else {
                    return npcGetter.Factions.Select(faction => faction.Faction.FormKey).ToHashSet();
                }
            }
            if (npcSpawnGetter is ILeveledNpcGetter leveledNpcGetter) {
                if (leveledNpcGetter.Entries != null && leveledNpcGetter.Entries.Count > 0) {
                    var data = leveledNpcGetter.Entries[0].Data;
                    if (data != null && data.Reference.TryResolve(linkCache, out var reference)) {
                        return GetFactions(reference, linkCache);
                    }
                }
            }
            return [];
        }

        private static double Distance(P3Float a, P3Float b) {
            var aa = a * 1;
            var bb = b * 1;
            aa.Z *= (float)Settings.verticalWeight;
            bb.Z *= (float)Settings.verticalWeight;
            return (aa - bb).Magnitude;
        }
        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state) {
            int counter = 0;
            var worldspaceCellLocation = new WorldspaceCellLocationCache(state.LoadOrder.PriorityOrder.Cell().WinningContextOverrides(state.LinkCache));
            var cellList = new List<FormKey>();
            var cellByFormKey = new Dictionary<FormKey, Tuple<IModContext<ISkyrimMod, ISkyrimModGetter, ICell, ICellGetter>, HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, ICell, ICellGetter>>>>();
            var npcsByCell = new Dictionary<FormKey, List<IPlacedNpcGetter>>();
            var npcInfoDict = new Dictionary<FormKey, NpcInfo>();

            foreach (var cellContext in state.LoadOrder.PriorityOrder.Cell().WinningContextOverrides(state.LinkCache)) {
                cellByFormKey.Add(cellContext.Record.FormKey, new Tuple<IModContext<ISkyrimMod, ISkyrimModGetter, ICell, ICellGetter>, HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, ICell, ICellGetter>>>(cellContext, []));
            }

            foreach (var placedNpcContext in state.LoadOrder.PriorityOrder.PlacedNpc().WinningContextOverrides(state.LinkCache)) {

                if (placedNpcContext.Record.Placement == null) {
                    continue;
                }

                if (placedNpcContext.TryGetContainingCell(worldspaceCellLocation, out var containingCell)) {
                    var formKey = containingCell.Record.FormKey;
                    /*if (formKey != Skyrim.Cell.FortNeugradExterior04.FormKey) {
                        continue;
                    }*/
                    if (!npcsByCell.TryGetValue(formKey, out List<IPlacedNpcGetter>? value)) {
                        value = [];
                        npcsByCell.Add(formKey, value);
                        cellList.Add(formKey);
                    }
                    cellByFormKey[formKey].Item2.Add(containingCell);
                    value.Add(placedNpcContext.Record);
                }
            }



            foreach (var cellFormKey in cellList) {
                var cellContext = cellByFormKey[cellFormKey].Item1;
                var pointList = new List<P3Float>();

                if(cellContext.Record.FormKey != Skyrim.Cell.EmbershardMine01.FormKey) {
                    continue;
                }

                foreach (var cell in cellByFormKey[cellFormKey].Item2) {
                    foreach (var navMesh in cell.Record.NavigationMeshes) {
                        if (navMesh.Data == null) {
                            continue;
                        }
                        var vertexArray = navMesh.Data.Vertices.ToArray();
                        foreach (var triangle in navMesh.Data.Triangles) {
                            var a = vertexArray[triangle.Vertices.X];
                            var b = vertexArray[triangle.Vertices.Y];
                            var c = vertexArray[triangle.Vertices.Z];
                            pointList.Add(new P3Float((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3));
                        }
                    }
                }

                if (pointList.Count > 0) {
                    ICell? cellCopy = null;

                    var placedNpcs = npcsByCell[cellFormKey]
                        .Where(npc => !Settings.enemySettings.ignoreDead || !npc.MajorFlags.HasFlag(PlacedNpc.MajorFlag.StartsDead))
                        .Select(npc => new PlacedNpcInfo(npc)).ToList();


                    foreach (var placedNpc in placedNpcs) {
                        var key = placedNpc.PlacedNpc.Base.FormKey;
                        if (!npcInfoDict.ContainsKey(key)) {
                            if (placedNpc.PlacedNpc.Base.TryResolve(state.LinkCache, out var npc)) {
                                npcInfoDict.Add(key, new NpcInfo(NpcIsValidSpawn(npc, state.LinkCache), GetFactions(npc, state.LinkCache)));
                            } else {
                                npcInfoDict.Add(key, new NpcInfo(false, []));
                            }
                        }
                    }
                    var spawnTypeByPoint = new Dictionary<P3Float, PlacedNpcInfo>();
                    foreach (var point in pointList) {

                        var nearbyNpcs = new List<PlacedNpcInfo>();
                        var preventionNpcs = new List<PlacedNpcInfo>();

                        var valid = true;
                        PriorityQueue<PlacedNpcInfo, double> closestNpcs = new();

                        foreach (var placedNpc in placedNpcs) {
                            var distance = Distance(placedNpc.PlacedNpc.Placement!.Position, point);

                            if (distance <= Settings.maxDistance) {
                                closestNpcs.Enqueue(placedNpc, distance);
                            }
                        }

                        if (closestNpcs.Count < Settings.enemySettings.minNearby) {
                            continue;
                        }

                        if (closestNpcs.TryDequeue(out var closestNpc, out var closestDistance)) {
                            if (closestDistance < Settings.minDistance) {
                                continue;
                            }

                            var closestBaseNpc = closestNpc.PlacedNpc.Base;
                            var closestNpcInfo = npcInfoDict[closestBaseNpc.FormKey];
                            if (!closestNpcInfo.IsValid || closestNpc.PlacedNpc.MajorFlags.HasFlag(PlacedNpc.MajorFlag.StartsDead)) {
                                continue;
                            }

                            var missing = Settings.enemySettings.minNearby - 1;
                            while (missing > 1) {
                                nearbyNpcs.Add(closestNpcs.Dequeue());
                                missing--;
                            }
                            if (closestNpcs.TryDequeue(out var farthestConsidered, out var farthestDistance)) {

                                if (Settings.enemySettings.spawnPrevention != SpawnPrevention.Never) {
                                    foreach (var placedNpc in placedNpcs) {
                                        var pos1 = placedNpc.PlacedNpc.Placement!.Position * 1;
                                        var pos2 = point * 1;
                                        pos1.Z *= (float)Settings.verticalWeight;
                                        pos2.Z *= (float)Settings.verticalWeight;
                                        var distance = (pos1 - pos2).Magnitude;
                                        if (distance <= farthestDistance * Settings.enemySettings.preventionFactor) {
                                            var preventationNpcInfo = npcInfoDict[placedNpc.PlacedNpc.Base.FormKey];
                                            if (Settings.enemySettings.spawnPrevention == SpawnPrevention.Faction) {
                                                if (!closestNpcInfo.Factions.Intersect(preventationNpcInfo.Factions).Any()) {
                                                    valid = false;
                                                    break;
                                                }
                                            } else if (Settings.enemySettings.spawnPrevention == SpawnPrevention.Id) {
                                                if (closestBaseNpc.FormKey != placedNpc.PlacedNpc.Base.FormKey) {
                                                    valid = false;
                                                    break;
                                                }
                                            } else if (Settings.enemySettings.spawnPrevention == SpawnPrevention.Root) {
                                                // not implemented
                                            }
                                        }
                                    }

                                    if (valid) {
                                        spawnTypeByPoint.Add(point, closestNpc);
                                    }
                                }
                            } else {
                                Console.WriteLine("Queue error");
                            }
                        }
                    }

                    var clusterLength = Settings.enemySettings.clusterChance.Length;
                    var totalClusterChance = .0;
                    var clusterChance = new double[clusterLength];
                    for (var i = 0; i < clusterLength; i++) {
                        totalClusterChance += Settings.enemySettings.clusterChance[i];
                        clusterChance[i] = totalClusterChance;
                    }

                    Random rng = new();
                    var remainingSpawnPoints = new LinkedList<Tuple<P3Float, PlacedNpcInfo>>();
                    foreach (var p in spawnTypeByPoint.Keys.OrderBy(_ => rng.Next())) {
                        remainingSpawnPoints.AddFirst(new Tuple<P3Float, PlacedNpcInfo>(p, spawnTypeByPoint[p]));
                    }

                    while (remainingSpawnPoints.Count > 0) {

                        var clusterSize = 0;
                        var clusterSizeRng = rng.NextDouble() * totalClusterChance;
                        for (var i = 0; i < clusterLength; i++) {
                            if (clusterSizeRng <= clusterChance[i]) {
                                clusterSize = i;
                                break;
                            }
                        }
                        Console.WriteLine("Try ClusterSize=" + clusterSize);

                        var first = remainingSpawnPoints.First!.Value;
                        var validSpawnPoints = new List<Tuple<P3Float, PlacedNpcInfo>>();
                        // remove nearby points
                        for (var current = remainingSpawnPoints.First; current != null;) {
                            var next = current.Next;
                            var distance = Distance(first.Item1, current.Value.Item1);
                            if (distance < Settings.enemySettings.clusterDistance) {                                
                                if (distance < Settings.enemySettings.clusterDistance) {
                                    validSpawnPoints.Add(current.Value);
                                }
                                remainingSpawnPoints.Remove(current);                                
                            }
                            current = next;
                        }


                        var realClusterSize = Math.Min(validSpawnPoints.Count, clusterSize);
                        Console.WriteLine("Spawn ClusterSize=" + realClusterSize);
                        foreach (var spawnPoint in validSpawnPoints.Take(realClusterSize)) {
                            var closestNpc = spawnPoint.Item2;
                            var newNpc = new PlacedNpc(state.PatchMod);
                            newNpc.DeepCopyIn(closestNpc.PlacedNpc);
                            newNpc.LevelModifier = closestNpc.PlacedNpc.LevelModifier;
                            newNpc.MajorFlags = newNpc.MajorFlags.SetFlag(PlacedNpc.MajorFlag.Persistent, false);
                            newNpc.LinkedReferences.Clear();
                            newNpc.Placement = new Placement() {
                                Position = spawnPoint.Item1
                            };
                            cellCopy ??= cellContext.GetOrAddAsOverride(state.PatchMod);
                            cellCopy.Temporary.Add(newNpc);
                            counter++;
                        }

                    }
                }
            }
            Console.WriteLine(counter);
        }
    }
}