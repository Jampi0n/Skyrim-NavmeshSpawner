using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
            distanceToExistingNpcMax = 25000,
            distanceToPlayerSpawn = 2500,
            ignoreExistingDeadNpc = true,
            verticalDistanceWeight = 4,
            minimumNumExistingNpcsNearby = 2,
            preventionMethod = SpawnPrevention.Never,
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
            distanceToExistingNpcMax = 10000,
            distanceToPlayerSpawn = -1,
            ignoreExistingDeadNpc = true,
            verticalDistanceWeight = 1.5,
            minimumNumExistingNpcsNearby = 2,
            preventionMethod = SpawnPrevention.Never,
            preventionDistanceFactor = 1.5,
            clusterSpawnChance = [400, 15, 25, 21, 8, 2],
            clusterMinimumDistanceToOtherClusters = 1200,
            clusterSpawnRadius = 400,
            clusterUsePseudoRandom = false,
            clusterSpawnRoot = true
        };
    }
}
