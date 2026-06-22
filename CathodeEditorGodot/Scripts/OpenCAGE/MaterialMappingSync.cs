using CATHODE;
using CATHODE.Scripting;
using CathodeLib;
using System.Collections.Generic;
using System.Linq;

namespace OpenCAGE.UnityConnection
{
	public class SyncedMaterialMappingEntry
	{
		public string from = "";
		public string to = "";
	}

	public class SyncedMaterialMappingSet
	{
		public uint mapping_id;
		public string name = "";
		public List<SyncedMaterialMappingEntry> mappings = new List<SyncedMaterialMappingEntry>();
	}

	public static class MaterialMappingSync
	{
		public static SyncedMaterialMappingSet Pack(MaterialMappings.MaterialMapping mapping)
		{
			if (mapping == null)
				return null;

			ShortGuid id = EnsureMappingId(mapping);
			SyncedMaterialMappingSet sync = new SyncedMaterialMappingSet
			{
				mapping_id = id.AsUInt32,
				name = mapping.Name ?? "",
			};

			if (mapping.Mappings == null)
				return sync;

			for (int i = 0; i < mapping.Mappings.Count; i++)
			{
				MaterialMappings.MaterialMapping.Mapping entry = mapping.Mappings[i];
				if (entry == null)
					continue;

				sync.mappings.Add(new SyncedMaterialMappingEntry
				{
					from = entry.from ?? "",
					to = entry.to ?? "",
				});
			}

			return sync;
		}

		public static void Apply(Level level, SyncedMaterialMappingSet sync)
		{
			if (level?.MaterialMappings?.Entries == null || sync == null)
				return;

			ShortGuid id = new ShortGuid(sync.mapping_id);
			MaterialMappings.MaterialMapping entry = level.MaterialMappings.Entries
				.FirstOrDefault(candidate => candidate.ID == id);

			if (entry == null && !string.IsNullOrWhiteSpace(sync.name))
			{
				entry = level.MaterialMappings.Entries.FirstOrDefault(candidate =>
					string.Equals(candidate.Name, sync.name, System.StringComparison.OrdinalIgnoreCase));
			}

			if (entry == null)
			{
				entry = new MaterialMappings.MaterialMapping();
				level.MaterialMappings.Entries.Add(entry);
			}

			entry.Name = sync.name ?? "";
			entry.ID = id != ShortGuid.Invalid ? id : EnsureMappingId(entry);
			entry.Mappings.Clear();

			if (sync.mappings == null)
				return;

			for (int i = 0; i < sync.mappings.Count; i++)
			{
				SyncedMaterialMappingEntry mapped = sync.mappings[i];
				if (mapped == null)
					continue;

				entry.Mappings.Add(new MaterialMappings.MaterialMapping.Mapping
				{
					from = mapped.from ?? "",
					to = mapped.to ?? "",
				});
			}
		}

		private static ShortGuid EnsureMappingId(MaterialMappings.MaterialMapping mapping)
		{
			if (mapping.ID != ShortGuid.Invalid)
				return mapping.ID;

			if (string.IsNullOrWhiteSpace(mapping.Name))
				return ShortGuid.Invalid;

			mapping.ID = ShortGuidUtils.Generate(mapping.Name.Replace("/", "\\").ToUpper());
			return mapping.ID;
		}
	}
}
