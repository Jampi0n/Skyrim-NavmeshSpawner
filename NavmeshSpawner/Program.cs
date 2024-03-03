using Mutagen.Bethesda;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Mutagen.Bethesda.Skyrim.Records.Tooling;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.FormKeys.SkyrimSE;

namespace NavmeshSpawner {


    public enum SpawnPrevention {
        Id,
        Root,
        Faction,
        Never
    }


    public class DomainSettings {
        public bool enabled;
        public double distanceToExistingNpcMin;
        public double distanceToExistingNpcMax;
        public double distanceToPlayerSpawn;
        public bool ignoreExistingDeadNpc;
        public double verticalDistanceWeight;
        public int minimumNumExistingNpcsNearby;
        public SpawnPrevention preventionMethod = SpawnPrevention.Never;
        public double preventionDistanceFactor;

        public double[] clusterSpawnChance = [];
        public double clusterMinimumDistanceToOtherClusters;
        public double clusterSpawnRadius;
        public bool clusterUsePseudoRandom;
        public bool clusterSpawnRoot;
    }


    public class Settings {
        public DomainSettings Interior = new() {
            enabled = true,
            distanceToExistingNpcMin = 500,
            distanceToExistingNpcMax = 10000,
            distanceToPlayerSpawn = 2500,
            ignoreExistingDeadNpc = true,
            verticalDistanceWeight = 4,
            minimumNumExistingNpcsNearby = 2,
            preventionMethod = SpawnPrevention.Faction,
            preventionDistanceFactor = 1.5,
            clusterSpawnChance = [200, 15, 25, 21, 8, 2],
            clusterMinimumDistanceToOtherClusters = 700,
            clusterSpawnRadius = 300,
            clusterUsePseudoRandom = false,
            clusterSpawnRoot = true
        };
        public DomainSettings Exterior = new() {
            enabled = true,
            distanceToExistingNpcMin = 500,
            distanceToExistingNpcMax = 5000,
            distanceToPlayerSpawn = -1,
            ignoreExistingDeadNpc = true,
            verticalDistanceWeight = 1.5,
            minimumNumExistingNpcsNearby = 2,
            preventionMethod = SpawnPrevention.Faction,
            preventionDistanceFactor = 1.5,
            clusterSpawnChance = [400, 15, 25, 21, 8, 2],
            clusterMinimumDistanceToOtherClusters = 1200,
            clusterSpawnRadius = 400,
            clusterUsePseudoRandom = false,
            clusterSpawnRoot = true
        };
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


        private static Dictionary<FormKey, FormKey> leveledNpcTree = null!;

        private static FormKey? computeRoot(Dictionary<FormKey, HashSet<FormKey>> relationTree, Dictionary<FormKey, FormKey?> cache, FormKey child) {
            if (!cache.ContainsKey(child)) {
                if (relationTree.ContainsKey(child)) {
                    var parents = relationTree[child];
                    var parentsRoots = parents.Select(p => computeRoot(relationTree, cache, p)).Distinct().ToList();
                    if (parentsRoots.Count == 1) {
                        cache[child] = parentsRoots.First();
                    } else {
                        cache[child] = null;
                    }
                } else {
                    cache[child] = child;
                }
            }
            return cache[child];
        }

        private static void BuildLeveledNpcTree(IPatcherState<ISkyrimMod, ISkyrimModGetter> state) {

            var relationTree = new Dictionary<FormKey, HashSet<FormKey>>();

            foreach (var leveledNpc in state.LoadOrder.PriorityOrder.LeveledNpc().WinningOverrides()) {

                if (leveledNpc.Entries == null) {
                    continue;
                }
                foreach (var entry in leveledNpc.Entries) {
                    if (entry.Data == null) {
                        continue;
                    }

                    if (!relationTree.TryGetValue(entry.Data.Reference.FormKey, out HashSet<FormKey>? set)) {
                        set = [];
                        relationTree.Add(entry.Data.Reference.FormKey, set);
                    }
                    set.Add(leveledNpc.FormKey);
                }
            }

            var cache = new Dictionary<FormKey, FormKey?>();

            foreach (var kv in relationTree) {
                computeRoot(relationTree, cache, kv.Key);
            }



            var nonNull = new Dictionary<FormKey, FormKey>();
            foreach (var kv in cache) {
                if (kv.Value != null) {
                    nonNull.Add(kv.Key, kv.Value.Value);
                }
            }


            var leveledNpcToNpc = new Dictionary<FormKey, INpcGetter>();
            foreach (var npcRecord in state.LoadOrder.PriorityOrder.Npc().WinningOverrides()) {
                if (!npcRecord.Template.IsNull) {
                    var template = npcRecord.Template.Resolve(state.LinkCache);
                    if (template is not ILeveledNpcGetter) {
                        continue;
                    }
                    if (leveledNpcToNpc.ContainsKey(template.FormKey)) {
                        var prev = leveledNpcToNpc[template.FormKey];
                        var prevTemplateFlags = (uint)prev.Configuration.TemplateFlags;
                        var newTemplateFlags = (uint)npcRecord.Configuration.TemplateFlags;
                        if ((newTemplateFlags & prevTemplateFlags) == prevTemplateFlags) {
                            leveledNpcToNpc[template.FormKey] = npcRecord;
                        }
                    } else {
                        leveledNpcToNpc.Add(template.FormKey, npcRecord);
                    }
                }
            }

            var result = new Dictionary<FormKey, FormKey>();
            foreach (var npcRecord in state.LoadOrder.PriorityOrder.Npc().WinningOverrides()) {
                if (!npcRecord.Template.IsNull) {
                    var template = npcRecord.Template.Resolve(state.LinkCache);
                    if (template is not ILeveledNpcGetter) {
                        continue;
                    }
                    if (nonNull.ContainsKey(template.FormKey)) {
                        var leveledNpc = nonNull[template.FormKey];
                        if (leveledNpcToNpc.ContainsKey(leveledNpc)) {
                            result.Add(npcRecord.FormKey, leveledNpcToNpc[leveledNpc].FormKey);
                        }
                    }
                }
            }

            /*var leveledNpcToNpc = new Dictionary<FormKey, INpcGetter>();
            foreach (var npcRecord in state.LoadOrder.PriorityOrder.Npc().WinningOverrides()) {
                if (!npcRecord.Template.IsNull) {
                    var template = npcRecord.Template.Resolve(state.LinkCache);
                    if (template is not ILeveledNpcGetter) {
                        continue;
                    }
                    if (leveledNpcToNpc.ContainsKey(template.FormKey)) {
                        var prev = leveledNpcToNpc[template.FormKey];
                        var prevTemplateFlags = (uint)prev.Configuration.TemplateFlags;
                        var newTemplateFlags = (uint)npcRecord.Configuration.TemplateFlags;
                        if ((newTemplateFlags & prevTemplateFlags) == prevTemplateFlags) {
                            leveledNpcToNpc[template.FormKey] =  npcRecord;
                        }
                    } else {
                        leveledNpcToNpc.Add(template.FormKey, npcRecord);
                    }
                }
            }

            var result = new Dictionary<FormKey, FormKey>();
            foreach (var kv in nonNull) {
                if (leveledNpcToNpc.ContainsKey(kv.Key)) {
                    var root = leveledNpcToNpc[kv.Key].FormKey;
                    var source = kv.Value;
                    if (leveledNpcToNpc.ContainsKey(source)) {
                        source = leveledNpcToNpc[source].FormKey;
                    }
                    result[source] = root;
                }
            }*/

            leveledNpcTree = result;
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

        private static double Distance(P3Float a, P3Float b, DomainSettings settings) {
            var aa = a * 1;
            var bb = b * 1;
            aa.Z *= (float)settings.verticalDistanceWeight;
            bb.Z *= (float)settings.verticalDistanceWeight;
            return (aa - bb).Magnitude;
        }
        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state) {
            int counter = 0;
            var worldspaceCellLocation = new WorldspaceCellLocationCache(state.LoadOrder.PriorityOrder.Cell().WinningContextOverrides(state.LinkCache));
            var cellList = new List<FormKey>();
            var cellByFormKey = new Dictionary<FormKey, Tuple<IModContext<ISkyrimMod, ISkyrimModGetter, ICell, ICellGetter>, HashSet<IModContext<ISkyrimMod, ISkyrimModGetter, ICell, ICellGetter>>>>();
            var playerMarkersByCell = new Dictionary<FormKey, HashSet<IPlacedObjectGetter>>();
            var npcsByCell = new Dictionary<FormKey, List<IPlacedNpcGetter>>();
            var npcInfoDict = new Dictionary<FormKey, NpcInfo>();



            BuildLeveledNpcTree(state);


            foreach (var placedObjectContext in state.LoadOrder.PriorityOrder.PlacedObject().WinningContextOverrides(state.LinkCache)) {
                if (placedObjectContext.Record.Base.FormKey == Skyrim.Static.COCMarkerHeading.FormKey) {
                    if (placedObjectContext.TryGetContainingCell(worldspaceCellLocation, out var containingCell)) {
                        var formKey = containingCell.Record.FormKey;
                        if (!playerMarkersByCell.ContainsKey(formKey)) {
                            playerMarkersByCell.Add(formKey, []);
                        }
                        playerMarkersByCell[formKey].Add(placedObjectContext.Record);
                    }
                }
            }

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


                var exterior = false;
                if (cellContext.TryGetParent<IWorldspaceGetter>(out var worldspace)) {
                    exterior = true;
                    // worldspaces that are actually interior:
                    var wsFormKey = worldspace.FormKey;
                    if (wsFormKey.ModKey.Name == "Skyrim.esm") {
                        if (wsFormKey != Skyrim.Worldspace.Tamriel.FormKey && wsFormKey != Skyrim.Worldspace.Sovngarde.FormKey && wsFormKey != Skyrim.Worldspace.SkuldafnWorld.FormKey) {
                            exterior = false;
                        }
                    }
                    if (wsFormKey.ModKey.Name == "Dawnguard.esm") {
                        if (wsFormKey != Dawnguard.Worldspace.DLC01FalmerValley.FormKey && wsFormKey != Dawnguard.Worldspace.DLC01SoulCairn.FormKey) {
                            exterior = false;
                        }
                    }

                    if (wsFormKey.ModKey.Name == "Dragonborn.esm") {
                        if (wsFormKey != Dragonborn.Worldspace.DLC2SolstheimWorld.FormKey) {
                            exterior = false;
                        }
                    }
                }
                var settings = exterior ? Settings.Exterior : Settings.Interior;

                var clusterLength = settings.clusterSpawnChance.Length;
                var totalClusterChance = .0;
                var clusterChance = new double[clusterLength];
                var clusterExpectedSize = .0;
                for (var i = 0; i < clusterLength; i++) {
                    clusterExpectedSize += i * settings.clusterSpawnChance[i];
                    totalClusterChance += settings.clusterSpawnChance[i];
                    clusterChance[i] = totalClusterChance;
                }
                clusterExpectedSize /= totalClusterChance;

                /*if (cellContext.Record.FormKey != Skyrim.Cell.EmbershardMine01.FormKey) {
                    continue;
                }*/

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
                        .Where(npc => !settings.ignoreExistingDeadNpc || !npc.MajorFlags.HasFlag(PlacedNpc.MajorFlag.StartsDead))
                        .Select(npc => new PlacedNpcInfo(npc)).ToList();


                    var placedMarkers = playerMarkersByCell.GetValueOrDefault(cellFormKey, []);

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

                        foreach (var marker in placedMarkers) {
                            if (marker.Placement != null) {
                                var distance = Distance(marker.Placement.Position, point, settings);
                                if (distance < settings.distanceToPlayerSpawn) {
                                    valid = false;
                                    break;
                                }
                            }
                        }
                        if (!valid) {
                            continue;
                        }

                        foreach (var placedNpc in placedNpcs) {
                            var distance = Distance(placedNpc.PlacedNpc.Placement!.Position, point, settings);

                            if (distance <= settings.distanceToExistingNpcMax) {
                                closestNpcs.Enqueue(placedNpc, distance);
                            }
                        }

                        if (closestNpcs.Count < settings.minimumNumExistingNpcsNearby) {
                            continue;
                        }

                        if (closestNpcs.TryDequeue(out var closestNpc, out var closestDistance)) {
                            if (closestDistance < settings.distanceToExistingNpcMin) {
                                continue;
                            }



                            var closestBaseNpc = closestNpc.PlacedNpc.Base;
                            var closestNpcInfo = npcInfoDict[closestBaseNpc.FormKey];
                            if (!closestNpcInfo.IsValid || closestNpc.PlacedNpc.MajorFlags.HasFlag(PlacedNpc.MajorFlag.StartsDead)) {
                                continue;
                            }
                            closestNpcs.Enqueue(closestNpc, closestDistance);


                            var missing = settings.minimumNumExistingNpcsNearby;
                            while (missing > 1) {
                                nearbyNpcs.Add(closestNpcs.Dequeue());
                                missing--;
                            }
                            if (closestNpcs.TryDequeue(out var farthestConsidered, out var farthestDistance)) {
                                foreach (var placedNpc in placedNpcs) {
                                    var distance = Distance(placedNpc.PlacedNpc.Placement!.Position, point, settings);
                                    if (distance <= farthestDistance * settings.preventionDistanceFactor) {
                                        var preventationNpcInfo = npcInfoDict[placedNpc.PlacedNpc.Base.FormKey];
                                        if (settings.preventionMethod == SpawnPrevention.Faction) {
                                            if (!closestNpcInfo.Factions.Intersect(preventationNpcInfo.Factions).Any()) {
                                                valid = false;
                                                break;
                                            }
                                        } else if (settings.preventionMethod == SpawnPrevention.Id) {
                                            if (closestBaseNpc.FormKey != placedNpc.PlacedNpc.Base.FormKey) {
                                                valid = false;
                                                break;
                                            }
                                        } else if (settings.preventionMethod == SpawnPrevention.Root) {
                                            if (!leveledNpcTree.ContainsKey(closestBaseNpc.FormKey) || !leveledNpcTree.ContainsKey(placedNpc.PlacedNpc.Base.FormKey)) {
                                                valid = false;
                                                break;
                                            }
                                            if (leveledNpcTree[closestBaseNpc.FormKey] != leveledNpcTree[placedNpc.PlacedNpc.Base.FormKey]) {
                                                valid = false;
                                                break;
                                            }
                                        }
                                    }
                                }

                                if (valid) {
                                    spawnTypeByPoint[point] = closestNpc;
                                }

                            } else {
                                Console.WriteLine("Queue error");
                            }
                        }
                    }

                    Random rng = new();
                    var remainingSpawnPoints = new LinkedList<Tuple<P3Float, PlacedNpcInfo>>();
                    foreach (var p in spawnTypeByPoint.Keys.OrderBy(_ => rng.Next())) {
                        remainingSpawnPoints.AddFirst(new Tuple<P3Float, PlacedNpcInfo>(p, spawnTypeByPoint[p]));
                    }

                    var numSpawns = 0;
                    var processedPoints = 0;
                    while (remainingSpawnPoints.Count > 0) {
                        processedPoints++;

                        var clusterSize = clusterLength - 1;
                        var clusterSizeRng = rng.NextDouble() * totalClusterChance;

                        if (processedPoints >= 1 && settings.clusterUsePseudoRandom) {
                            var expected = clusterExpectedSize * processedPoints;
                            var maxCorrection = Math.Sqrt(processedPoints);
                            var accuracy = Math.Max(1.0 / maxCorrection, Math.Min((1.0 * numSpawns) / expected, maxCorrection));
                            var tmp = Math.Pow(accuracy, processedPoints);
                            var pseudoRandom = totalClusterChance * tmp;
                            var pseudoOffset = (1 - tmp) * totalClusterChance;

                            clusterSizeRng = rng.NextDouble() * pseudoRandom + pseudoOffset;
                            //Console.WriteLine("accuracy=" + accuracy);
                        }

                        for (var i = 0; i < clusterLength; i++) {
                            if (clusterSizeRng <= clusterChance[i]) {
                                clusterSize = i;
                                break;
                            }
                        }
                        //Console.WriteLine("Try ClusterSize=" + clusterSize);

                        var first = remainingSpawnPoints.First!.Value;
                        var validSpawnPoints = new List<Tuple<P3Float, PlacedNpcInfo>>();
                        // remove nearby points
                        for (var current = remainingSpawnPoints.First; current != null;) {
                            var next = current.Next;
                            var distance = Distance(first.Item1, current.Value.Item1, settings);
                            if (distance < settings.clusterSpawnRadius) {
                                validSpawnPoints.Add(current.Value);
                            }
                            if (distance < settings.clusterMinimumDistanceToOtherClusters) {
                                remainingSpawnPoints.Remove(current);
                            }
                            current = next;
                        }


                        var realClusterSize = Math.Min(validSpawnPoints.Count, clusterSize);
                        //Console.WriteLine("Spawn ClusterSize=" + realClusterSize);
                        foreach (var spawnPoint in validSpawnPoints.Take(realClusterSize)) {
                            var closestNpc = spawnPoint.Item2;
                            var newNpc = new PlacedNpc(state.PatchMod);
                            newNpc.DeepCopyIn(closestNpc.PlacedNpc);
                            newNpc.LevelModifier = closestNpc.PlacedNpc.LevelModifier;
                            newNpc.MajorFlags = newNpc.MajorFlags.SetFlag(PlacedNpc.MajorFlag.Persistent, false);
                            newNpc.LinkedReferences.Clear();
                            newNpc.LocationRefTypes = null;
                            newNpc.PersistentLocation.Clear();
                            var direction = (float)rng.NextDouble() * 360;
                            if (placedMarkers.Count > 0) {
                                var minDistance = double.MaxValue;
                                var closestMarker = new P3Float();
                                foreach (var marker in placedMarkers) {
                                    if (marker.Placement != null) {
                                        var distance = Distance(marker.Placement.Position, spawnPoint.Item1, settings);
                                        if (distance < minDistance) {
                                            minDistance = distance;
                                            closestMarker = marker.Placement.Position;
                                        }
                                    }
                                }
                                if (minDistance < double.MaxValue) {
                                    direction = (float)Math.Atan2(closestMarker.Y - spawnPoint.Item1.Y, closestMarker.X - spawnPoint.Item1.X);
                                }
                            }
                            newNpc.Placement = new Placement() {
                                Position = spawnPoint.Item1,
                                Rotation = new P3Float(0, 0, direction)
                            };
                            if (settings.clusterSpawnRoot && leveledNpcTree.ContainsKey(closestNpc.PlacedNpc.Base.FormKey)) {
                                newNpc.Base.SetTo(leveledNpcTree[closestNpc.PlacedNpc.Base.FormKey]);
                            }
                            cellCopy ??= cellContext.GetOrAddAsOverride(state.PatchMod);
                            cellCopy.Temporary.Add(newNpc);
                            numSpawns++;
                        }
                    }
                    counter += numSpawns;
                }
            }
            Console.WriteLine(counter);
        }
    }
}