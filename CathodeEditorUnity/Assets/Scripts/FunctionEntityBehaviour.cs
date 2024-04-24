using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class FunctionEntityBehaviour : MonoBehaviour
{
    public Entity Entity;
    public Composite Composite;
    public Commands Commands;

    private Composite ZoneComposite;
    private FunctionEntity ZoneEntity;

    [SerializeField] private bool FindZone;

    private void Update()
    {
        if (!FindZone)
            return;

        if (Entity == null || Composite == null || Commands == null)
            return;

        if (ZoneComposite == null || ZoneEntity == null)
        {
            Debug.Log("Trying to find zone info...");
            foreach (Composite comp in Commands.Entries)
            {
                ZoneComposite = comp;
                ZoneEntity = FindZoneEnt(comp);
                if (ZoneEntity != null) break;
            }
            FindZone = false;
        }

        if (ZoneComposite == null || ZoneEntity == null)
            return;

        Debug.Log("Zone: " + ZoneEntity.shortGUID.ToByteString() + "\nComposite: " + ZoneComposite.name);
    }

    private FunctionEntity FindZoneEnt(Composite comp)
    {
        ShortGuid compositesGUID = ShortGuidUtils.Generate("composites");

        List<FunctionEntity> triggerSequences = comp.functions.FindAll(o => o.function == CommandsUtils.GetFunctionTypeGUID(FunctionType.TriggerSequence));
        foreach (FunctionEntity trigEnt in triggerSequences)
        {
            TriggerSequence trig = (TriggerSequence)trigEnt;
            foreach (TriggerSequence.Entity trigger in trig.entities)
            {
                if (CommandsUtils.ResolveHierarchy(Commands, comp, trigger.connectedEntity.path, out Composite compRef, out string str) != Entity)
                    continue;

                Debug.Log("Found TriggerSequence for Entity");
                foreach (FunctionEntity z in comp.functions.FindAll(o => o.function == CommandsUtils.GetFunctionTypeGUID(FunctionType.Zone)))
                {
                    List<EntityConnector> conn = z.childLinks.FindAll(o => o.parentParamID == compositesGUID && o.childID == trig.shortGUID);
                    if (conn.Count == 0) continue;
                    return z;
                }
            }
        }

        return null;
    }
}
