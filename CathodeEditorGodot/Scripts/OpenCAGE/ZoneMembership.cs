using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using CathodeLib;
using OpenCAGE.UnityConnection;
using System.Collections.Generic;

namespace OpenCAGE
{
    /// <summary>
    /// Works out which parts of a level each Zone entity claims. Canonical copy for Level Viewer and
    /// OpenCAGE (linked in OpenCAGE.csproj).
    /// </summary>
    /// <remarks>
    /// This is the same walk the instancer does at build time (Instancing.CalculateZones): a Zone's
    /// 'composites' pin names TriggerSequences, each sequence entry names an entity, and the zone takes
    /// that entity and everything inside it. What it does NOT reproduce is the primary/secondary
    /// ordering - that decides streaming, and nothing drawn in the viewport is streamed - so a zone
    /// here is just the set of places it reaches.
    ///
    /// It also does not stop at is_shared composites the way the build's descent does. The build
    /// instances a shared composite once and leaves what is inside it unzoned; the viewer has no such
    /// collapsing and draws a copy under every placement, so the honest answer for a copy standing in
    /// a room is the zone that room belongs to.
    ///
    /// Measured against the instancer by ReleaseSweep `zonecheck`. TECH_COMMS: reaches all 992,704
    /// entities the build zones, agreeing with the primary zone on 992,699 and the other 5 being
    /// their secondary; TECH_HUB: all 1,252,706, agreeing on 1,252,659. Neither level has a single
    /// entity put in a zone the build says it is not in. Both additionally claim entities the build
    /// leaves unzoned (309,864 and 247,905) - 100% of them inside is_shared composites, as above.
    /// </remarks>
    public static class ZoneMembership
    {
        /// <summary>
        /// Every zone in the level, with the instance paths it reaches directly. Paths run from the
        /// level root composite, which is what the viewer populates its scene from.
        /// </summary>
        public static List<SyncedZone> Calculate(Level level)
        {
            Commands commands = level?.Commands;
            Composite root = commands?.EntryPoints != null && commands.EntryPoints.Length != 0
                ? commands.EntryPoints[0]
                : null;
            return CalculateFrom(level, root);
        }

        /// <summary>
        /// The same walk, but starting from one composite rather than the level root: the paths it
        /// returns run from <paramref name="from"/>, which is what someone standing in that composite
        /// can address.
        /// </summary>
        /// <remarks>
        /// A zone naming its contents by an absolute path from the level root is left out when starting
        /// anywhere else, because where this composite sits under the root is not knowable from here -
        /// it may be instanced many times over, or not at all, and each placement would answer
        /// differently. Only what the composite can reach on its own is reported.
        /// </remarks>
        public static List<SyncedZone> CalculateFrom(Level level, Composite from)
        {
            List<SyncedZone> zones = new List<SyncedZone>();

            Commands commands = level?.Commands;
            if (commands == null || from == null)
                return zones;

            HashSet<ShortGuid> worthDescending = FindCompositesReachingAZone(commands);
            if (worthDescending.Count == 0)
                return zones;

            Composite root = commands.EntryPoints != null && commands.EntryPoints.Length != 0
                ? commands.EntryPoints[0]
                : null;

            Walk(
                commands,
                from,
                new List<Composite>() { from },
                new List<uint>(),
                new HashSet<ShortGuid>(),
                worthDescending,
                zones,
                relativePathsOnly: from != root);
            return zones;
        }

        /// <summary>
        /// Composites holding a Zone entity, plus everything that can instance its way down to one.
        /// </summary>
        /// <remarks>
        /// A level has tens of thousands of composite instances and a few dozen zones, so walking the
        /// whole tree to find them is nearly all wasted. Working out which composites can even reach a
        /// zone first turns the walk into the handful of branches that can produce anything.
        /// </remarks>
        private static HashSet<ShortGuid> FindCompositesReachingAZone(Commands commands)
        {
            Dictionary<ShortGuid, List<ShortGuid>> instancedBy = new Dictionary<ShortGuid, List<ShortGuid>>();
            Queue<ShortGuid> pending = new Queue<ShortGuid>();
            HashSet<ShortGuid> reaching = new HashSet<ShortGuid>();

            foreach (Composite composite in commands.Entries)
            {
                bool holdsZone = false;
                foreach (FunctionEntity function in composite.functions)
                {
                    if (function.function.IsFunctionType)
                    {
                        if (function.function.AsFunctionType == FunctionType.Zone)
                            holdsZone = true;
                        continue;
                    }

                    if (!instancedBy.TryGetValue(function.function, out List<ShortGuid> parents))
                    {
                        parents = new List<ShortGuid>();
                        instancedBy[function.function] = parents;
                    }
                    if (!parents.Contains(composite.shortGUID))
                        parents.Add(composite.shortGUID);
                }

                if (holdsZone && reaching.Add(composite.shortGUID))
                    pending.Enqueue(composite.shortGUID);
            }

            while (pending.Count != 0)
            {
                ShortGuid current = pending.Dequeue();
                if (!instancedBy.TryGetValue(current, out List<ShortGuid> parents))
                    continue;

                foreach (ShortGuid parent in parents)
                {
                    if (reaching.Add(parent))
                        pending.Enqueue(parent);
                }
            }

            return reaching;
        }

        /// <summary>
        /// Walk the composite instance tree, emitting a zone for every Zone entity as it is reached.
        /// </summary>
        /// <remarks>
        /// A zone is per INSTANCE, not per entity: the build gives the same Zone entity in two
        /// instances of a composite two different zone ids, and they claim different geometry. So the
        /// walk carries the path of placement entities that got here, and that path is both what a
        /// root is written relative to and part of what picks the colour.
        /// </remarks>
        private static void Walk(
            Commands commands,
            Composite composite,
            List<Composite> compositeStack,
            List<uint> path,
            HashSet<ShortGuid> onStack,
            HashSet<ShortGuid> worthDescending,
            List<SyncedZone> zones,
            bool relativePathsOnly)
        {
            //A composite that (however indirectly) instances itself would otherwise walk forever
            if (!onStack.Add(composite.shortGUID))
                return;

            foreach (FunctionEntity function in composite.functions)
            {
                if (function.function.IsFunctionType)
                {
                    if (function.function.AsFunctionType == FunctionType.Zone)
                        EmitZone(commands, composite, compositeStack, path, function, zones, relativePathsOnly);
                    continue;
                }

                if (!worthDescending.Contains(function.function))
                    continue;

                Composite nested = commands.GetComposite(function.function);
                if (nested == null)
                    continue;

                path.Add(function.shortGUID.AsUInt32);
                compositeStack.Add(nested);
                Walk(commands, nested, compositeStack, path, onStack, worthDescending, zones, relativePathsOnly);
                compositeStack.RemoveAt(compositeStack.Count - 1);
                path.RemoveAt(path.Count - 1);
            }

            onStack.Remove(composite.shortGUID);
        }

        private static void EmitZone(
            Commands commands,
            Composite composite,
            List<Composite> compositeStack,
            List<uint> path,
            FunctionEntity zone,
            List<SyncedZone> zones,
            bool relativePathsOnly)
        {
            SyncedZone synced = new SyncedZone()
            {
                zone_entity = zone.shortGUID.AsUInt32,
                zone_composite = composite.shortGUID.AsUInt32,
                name = commands.Utils.GetEntityName(composite, zone),
                zone_path = new List<uint>(path),
            };
            ZoneDefinitions.GetColour(
                ColourKey(zone, path), out synced.colour_r, out synced.colour_g, out synced.colour_b);

            foreach (EntityConnector link in zone.childLinks)
            {
                if (link.thisParamID != ShortGuids.composites)
                    continue;

                Entity linked = composite.GetEntityByID(link.linkedEntityID);
                if (linked == null)
                    continue;

                if (linked is TriggerSequence sequence)
                {
                    foreach (TriggerSequence.SequenceEntry entry in sequence.sequence)
                    {
                        if (!EntityInstancePath.TryResolve(commands, composite, path, entry.connectedEntity,
                                out List<uint> full, out bool relativeToHere))
                            continue;

                        //Walking from a composite that isn't the level root: a path written from the
                        //root names somewhere this walk cannot place, so it is not ours to claim
                        if (relativePathsOnly && !relativeToHere)
                            continue;

                        synced.roots.Add(full);
                    }
                }
                else if (linked is VariableEntity pin)
                {
                    AddPinRoots(compositeStack, path, pin, synced);
                }
                else
                {
                    //Linked straight to something in the zone's own composite
                    synced.roots.Add(PathTo(path, linked.shortGUID.AsUInt32));
                }
            }

            if (synced.roots.Count != 0)
                zones.Add(synced);
        }

        /// <summary>
        /// A zone inside a reusable composite takes its contents through one of that composite's pins,
        /// so the entities it claims are named by whatever placed the composite - one composite up.
        /// </summary>
        private static void AddPinRoots(List<Composite> compositeStack, List<uint> path, VariableEntity pin, SyncedZone synced)
        {
            //Nothing placed the level root, so a pin there is fed by nothing
            if (path.Count == 0 || compositeStack.Count < 2)
                return;

            Composite parent = compositeStack[compositeStack.Count - 2];
            Entity placement = parent.GetEntityByID(new ShortGuid(path[path.Count - 1]));
            if (placement == null)
                return;

            foreach (EntityConnector link in placement.childLinks)
            {
                if (link.thisParamID != pin.name && link.thisParamID != pin.shortGUID)
                    continue;

                Entity target = parent.GetEntityByID(link.linkedEntityID);
                if (target == null)
                    continue;

                //The target is a sibling of the placement, so the path stops one short of here
                List<uint> full = new List<uint>(path.Count);
                for (int i = 0; i < path.Count - 1; i++)
                    full.Add(path[i]);
                full.Add(target.shortGUID.AsUInt32);
                synced.roots.Add(full);
            }
        }

        private static List<uint> PathTo(List<uint> path, uint entity)
        {
            List<uint> full = new List<uint>(path.Count + 1);
            full.AddRange(path);
            full.Add(entity);
            return full;
        }

        /// <summary>
        /// What the zone's colour is picked from. The entity id alone would paint every instance of a
        /// shared composite's zone the same colour, when the build treats them as separate zones.
        /// </summary>
        private static uint ColourKey(FunctionEntity zone, List<uint> path)
        {
            uint key = zone.shortGUID.AsUInt32;
            for (int i = 0; i < path.Count; i++)
                key = (key * 31u) ^ path[i];
            return key;
        }
    }
}
