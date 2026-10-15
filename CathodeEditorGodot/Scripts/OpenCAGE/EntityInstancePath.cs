using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using System;
using System.Collections.Generic;

namespace OpenCAGE
{
    /// <summary>
    /// Turns a stored entity path into the instance path the Level Viewer addresses nodes by: the
    /// entity ids stepped through from the level root composite, which is what its scene is built from.
    /// Canonical copy for Level Viewer and OpenCAGE (linked in OpenCAGE.csproj).
    /// </summary>
    public static class EntityInstancePath
    {
        /// <summary>
        /// Resolve a TriggerSequence entry, alias target or similar stored path against the composite
        /// holding it, and express the answer from the level root.
        /// </summary>
        /// <remarks>
        /// A stored path has more than one legal reading - relative to the composite holding it, or
        /// absolute from the level root - and which one resolved decides what goes in front of it. A
        /// relative one continues from wherever the caller currently stands, so it takes the drill path
        /// as a prefix; an absolute one already starts at the level root and must not, or the target
        /// lands underneath itself.
        /// </remarks>
        public static bool TryResolve(
            Commands commands,
            Composite composite,
            IReadOnlyList<uint> drillPath,
            EntityPath stored,
            out List<uint> instancePath)
        {
            return TryResolve(commands, composite, drillPath, stored, out instancePath, out bool _);
        }

        /// <param name="relativeToHere">
        /// Which reading resolved it: relative to the composite holding it, or absolute from the level
        /// root. A caller working from somewhere other than the root can only use the relative ones -
        /// an absolute path is written from a place it cannot see.
        /// </param>
        public static bool TryResolve(
            Commands commands,
            Composite composite,
            IReadOnlyList<uint> drillPath,
            EntityPath stored,
            out List<uint> instancePath,
            out bool relativeToHere)
        {
            return TryResolve(commands, composite, drillPath, stored, out instancePath, out relativeToHere, out _, out _);
        }

        private static bool TryResolve(
            Commands commands,
            Composite composite,
            IReadOnlyList<uint> drillPath,
            EntityPath stored,
            out List<uint> instancePath,
            out bool relativeToHere,
            out Composite endComposite,
            out Entity end)
        {
            instancePath = null;
            relativeToHere = false;
            endComposite = null;
            end = null;
            if (commands == null || composite == null)
                return false;

            List<Tuple<Composite, Entity>> resolved = commands.Utils.ResolveEntityPath(stored, composite);
            if (resolved == null || resolved.Count == 0)
                return false;

            relativeToHere = resolved[0].Item1 == composite;
            int prefix = relativeToHere && drillPath != null ? drillPath.Count : 0;

            instancePath = new List<uint>(prefix + resolved.Count);
            for (int i = 0; i < prefix; i++)
                instancePath.Add(drillPath[i]);
            for (int i = 0; i < resolved.Count; i++)
                instancePath.Add(resolved[i].Item2.shortGUID.AsUInt32);

            endComposite = resolved[resolved.Count - 1].Item1;
            end = resolved[resolved.Count - 1].Item2;
            return true;
        }

        //An alias of an alias of an alias is legal; a loop of them is not, but a broken level could hold one
        private const int MaxPointerHops = 8;

        /// <summary>
        /// As <see cref="TryResolve(Commands, Composite, IReadOnlyList{uint}, EntityPath, out List{uint}, out bool)"/>, then on
        /// through any alias or proxy the path ends at to the entity it stands for. An alias or proxy has nothing of its own to
        /// draw - its target is what the level shows - and retail zones name aliases in their sequences (251 entries across the
        /// shipped levels, most of them over a ModelReference).
        /// </summary>
        /// <remarks>
        /// A pointer that resolves no further leaves the answer where it stopped, so this never finds less than TryResolve.
        /// <paramref name="relativeToHere"/> stays true only while every step was read relative to where it was written: a
        /// proxy, or an alias path that only reads from the level root, is written from the root.
        /// </remarks>
        public static bool TryResolveTarget(
            Commands commands,
            Composite composite,
            IReadOnlyList<uint> drillPath,
            EntityPath stored,
            out List<uint> instancePath,
            out bool relativeToHere)
        {
            if (!TryResolve(commands, composite, drillPath, stored, out instancePath, out relativeToHere, out Composite endComposite, out Entity end))
                return false;

            FollowPointers(commands, ref endComposite, ref end, ref instancePath, ref relativeToHere);
            return true;
        }

        /// <summary>
        /// Where an alias or proxy in <paramref name="composite"/> (reached down <paramref name="drillPath"/>) points, followed to
        /// the entity it stands for. False when <paramref name="pointer"/> is neither, or names nothing.
        /// </summary>
        public static bool TryResolvePointer(
            Commands commands,
            Composite composite,
            IReadOnlyList<uint> drillPath,
            Entity pointer,
            out List<uint> instancePath,
            out bool relativeToHere)
        {
            return TryResolvePointer(commands, composite, drillPath, pointer, out instancePath, out relativeToHere, out _);
        }

        /// <param name="target">The entity at the end of <paramref name="instancePath"/>: what the pointer stands for.</param>
        public static bool TryResolvePointer(
            Commands commands,
            Composite composite,
            IReadOnlyList<uint> drillPath,
            Entity pointer,
            out List<uint> instancePath,
            out bool relativeToHere,
            out Entity target)
        {
            instancePath = null;
            relativeToHere = false;
            target = null;
            if (commands == null || composite == null || !(pointer is AliasEntity || pointer is ProxyEntity))
                return false;

            instancePath = new List<uint>((drillPath?.Count ?? 0) + 1);
            if (drillPath != null)
                instancePath.AddRange(drillPath);
            instancePath.Add(pointer.shortGUID.AsUInt32);
            relativeToHere = true;

            List<uint> before = instancePath;
            Composite endComposite = composite;
            target = pointer;
            FollowPointers(commands, ref endComposite, ref target, ref instancePath, ref relativeToHere);
            if (ReferenceEquals(before, instancePath))
            {
                target = null;
                return false;
            }
            return true;
        }

        /// <summary>
        /// While the path ends at an alias or proxy, replace it with the path to what that points at. The alias's own path is
        /// read from the composite holding it, which the instance path reaches through everything before the alias; a proxy's
        /// is written from the level root.
        /// </summary>
        private static void FollowPointers(
            Commands commands,
            ref Composite endComposite,
            ref Entity end,
            ref List<uint> instancePath,
            ref bool relativeToHere)
        {
            for (int hop = 0; hop < MaxPointerHops; hop++)
            {
                if (end is AliasEntity alias)
                {
                    List<uint> holder = instancePath.GetRange(0, instancePath.Count - 1);
                    if (!TryResolve(commands, endComposite, holder, alias.alias, out List<uint> next, out bool nextRelative, out Composite nextComposite, out Entity nextEnd))
                        return;
                    instancePath = next;
                    relativeToHere = relativeToHere && nextRelative;
                    endComposite = nextComposite;
                    end = nextEnd;
                }
                else if (end is ProxyEntity proxy)
                {
                    List<Tuple<Composite, Entity>> resolved = commands.Utils.ResolveProxy(proxy);
                    if (resolved == null || resolved.Count == 0)
                        return;
                    List<uint> next = new List<uint>(resolved.Count);
                    for (int i = 0; i < resolved.Count; i++)
                        next.Add(resolved[i].Item2.shortGUID.AsUInt32);
                    instancePath = next;
                    relativeToHere = false;
                    endComposite = resolved[resolved.Count - 1].Item1;
                    end = resolved[resolved.Count - 1].Item2;
                }
                else
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Where a TriggerSequence's members are, for marking them alongside it.
        /// </summary>
        /// <remarks>
        /// Worked out fresh each time rather than cached: a sequence's entries are not parameters, so
        /// nothing raises an event when one is added, and the only moment this is needed is the click
        /// that selects the sequence.
        /// </remarks>
        public static List<List<uint>> ResolveTriggerSequenceMembers(
            Commands commands,
            Composite composite,
            IReadOnlyList<uint> drillPath,
            TriggerSequence sequence)
        {
            if (sequence == null || sequence.sequence.Count == 0)
                return null;

            List<List<uint>> paths = new List<List<uint>>(sequence.sequence.Count);
            foreach (TriggerSequence.SequenceEntry entry in sequence.sequence)
            {
                //An entry naming an alias marks what the alias stands for: the alias itself has nothing to mark
                if (!TryResolveTarget(commands, composite, drillPath, entry.connectedEntity, out List<uint> path, out bool _))
                    continue;

                //The same entity twice in one sequence (different timings) is one thing to mark
                if (!Contains(paths, path))
                    paths.Add(path);
            }

            return paths.Count == 0 ? null : paths;
        }

        public static bool Equal(IReadOnlyList<uint> left, IReadOnlyList<uint> right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null)
                return false;
            if (left.Count != right.Count)
                return false;

            for (int i = 0; i < left.Count; i++)
            {
                if (left[i] != right[i])
                    return false;
            }

            return true;
        }

        private static bool Contains(List<List<uint>> paths, List<uint> candidate)
        {
            for (int i = 0; i < paths.Count; i++)
            {
                if (Equal(paths[i], candidate))
                    return true;
            }

            return false;
        }
    }
}
