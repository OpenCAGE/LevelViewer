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
            instancePath = null;
            relativeToHere = false;
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

            return true;
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
                if (!TryResolve(commands, composite, drillPath, entry.connectedEntity, out List<uint> path))
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
