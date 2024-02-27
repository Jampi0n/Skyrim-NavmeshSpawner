using Mutagen.Bethesda;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Mutagen.Bethesda.Skyrim.Records.Tooling;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace NavmeshSpawner {

    record NpcInfo(bool isValid, HashSet<FormKey> factions);
    public class Program {
        public static async Task<int> Main(string[] args) {
            return await SynthesisPipeline.Instance
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
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
            return new HashSet<FormKey>();
        }

        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state) {
            int counter = 0;
            var worldspaceCellLocation = new WorldspaceCellLocationCache(state.LoadOrder.PriorityOrder.Cell().WinningContextOverrides(state.LinkCache));
            var npcsByCell = new Dictionary<IModContext<ISkyrimMod, ISkyrimModGetter, ICell, ICellGetter>, List<IPlacedNpcGetter>>();
            var npcInfoDict = new Dictionary<FormKey, NpcInfo>();
            foreach (var placedNpcContext in state.LoadOrder.PriorityOrder.PlacedNpc().WinningContextOverrides(state.LinkCache)) {

                if (placedNpcContext.Record.Placement == null) {
                    continue;
                }

                if (placedNpcContext.TryGetContainingCell(worldspaceCellLocation, out var containingCell)) {
                    if (!npcsByCell.ContainsKey(containingCell)) {
                        npcsByCell.Add(containingCell, new List<IPlacedNpcGetter>());
                    }
                    npcsByCell[containingCell].Add(placedNpcContext.Record);
                }
            }

            double minDistance = 400;
            double[] maxDistance = new double[] { 700, 1000, 1300, 1600, 1900, 2200, 2500, 2800, 3100, 3400, 3700, 4000 };
            double maxSpawnsPerNpc = 3;

            foreach (var kv in npcsByCell) {
                var pointList = new List<P3Float>();
                var cellContext = kv.Key;
                var cellRecord = cellContext.Record;

                foreach (var navMesh in cellRecord.NavigationMeshes) {
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

                if (pointList.Count > 0) {
                    ICell? cellCopy = null;

                    var placedNpcs = kv.Value
                        .Where(npc => !npc.MajorFlags.HasFlag(PlacedNpc.MajorFlag.StartsDead))
                        .Select(npc => new Tuple<IPlacedNpcGetter, double[]>(npc, new double[] { maxSpawnsPerNpc })).ToList();


                    foreach (var placedNpc in placedNpcs) {
                        var key = placedNpc.Item1.Base.FormKey;
                        if (!npcInfoDict.ContainsKey(key)) {
                            if (placedNpc.Item1.Base.TryResolve(state.LinkCache, out var npc)) {
                                npcInfoDict.Add(key, new NpcInfo(NpcIsValidSpawn(npc, state.LinkCache), GetFactions(npc, state.LinkCache)));
                            } else {
                                npcInfoDict.Add(key, new NpcInfo(false, new HashSet<FormKey>()));
                            }
                        }
                    }



                    foreach (var point in pointList) {
                        for (var i = 0; i <  maxDistance.Length; i++) {
                            var searchDistance = maxDistance[i];
                            var nearbyNpcs = new List<Tuple<IPlacedNpcGetter, double[]>>();
                            Tuple<IPlacedNpcGetter, double[]>? closestNpc = null;
                            double closestDistance = searchDistance + 1;
                            foreach (var placedNpc in placedNpcs) {
                                var pos1 = placedNpc.Item1.Placement!.Position * 1;
                                var pos2 = point * 1;
                                pos1.Z *= 0.25f;
                                pos2.Z *= 0.25f;
                                var distance = (pos1 - pos2).Magnitude;
                                if (distance < minDistance) {
                                    closestNpc = null;
                                    break;
                                }
                                if (distance >= minDistance && distance <= searchDistance) {
                                    nearbyNpcs.Add(placedNpc);
                                    if (distance < closestDistance) {
                                        closestDistance = distance;
                                        closestNpc = placedNpc;
                                    }
                                }
                            }

                            if (closestNpc != null) {
                                if (closestNpc.Item2[0] < 1) {
                                    continue;
                                }
                                var npcInfo = npcInfoDict[closestNpc.Item1.Base.FormKey];
                                if (!npcInfo.isValid || closestNpc.Item1.MajorFlags.HasFlag(PlacedNpc.MajorFlag.StartsDead) || closestNpc.Item1.MajorFlags.HasFlag(PlacedNpc.MajorFlag.Persistent)) {
                                    continue;
                                }
                                var valid = true;
                                foreach (var nearbyNpc in nearbyNpcs) {
                                    var npcInfoNearby = npcInfoDict[nearbyNpc.Item1.Base.FormKey];
                                    if (npcInfo.factions.Intersect(npcInfoNearby.factions).Count() == 0) {
                                        valid = false;
                                        break;
                                    }
                                }
                                if (!valid) {
                                    continue;
                                }

                                var newNpc = new PlacedNpc(state.PatchMod);
                                newNpc.DeepCopyIn(closestNpc.Item1);
                                newNpc.LinkedReferences.Clear();
                                newNpc.Placement = new Placement() {
                                    Position = point
                                };
                                closestNpc.Item2[0]--;
                                if (cellCopy == null) {
                                    cellCopy = cellContext.GetOrAddAsOverride(state.PatchMod);
                                }
                                cellCopy.Temporary.Add(newNpc);
                                placedNpcs.Add(new Tuple<IPlacedNpcGetter, double[]>(newNpc, new double[] { 0 }));
                                counter++;
                            }
                        }
                    }
                }
            }
            Console.WriteLine(counter);
        }
    }
}
