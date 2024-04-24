using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using static UnityEditor.Timeline.TimelinePlaybackControls;

public class FunctionEntityBehaviour : MonoBehaviour
{
    public Entity Entity;
    public Composite Composite;
    public Commands Commands;

    private Composite ZoneComposite;
    private FunctionEntity ZoneEntity;

    private void Start()
    {
        TryFindZoneForEntity(Entity, Composite, out ZoneComposite, out ZoneEntity);
    }

    private void OnDrawGizmosSelected()
    {
        if (Entity == null || Composite == null || Commands == null)
            return;

        Debug.Log("Zone: " + ZoneEntity.shortGUID.ToByteString() + "\nComposite: " + ZoneComposite.name);
    }

    public void TryFindZoneForEntity(Entity entity, Composite startComposite, out Composite composite, out FunctionEntity zone)
    {
        Func<Composite, FunctionEntity> findZone = comp => {
            if (comp == null) return null;

            FunctionEntity toReturn = null;
            ShortGuid compositesGUID = ShortGuidUtils.Generate("composites");

            List<FunctionEntity> triggerSequences = comp.functions.FindAll(o => o.function == CommandsUtils.GetFunctionTypeGUID(FunctionType.TriggerSequence));
            foreach(FunctionEntity trigEnt in triggerSequences)
            {
                TriggerSequence trig = (TriggerSequence)trigEnt;
                foreach(TriggerSequence.Entity trigger in trig.entities)
                {
                    if (CommandsUtils.ResolveHierarchy(Commands, comp, trigger.connectedEntity.path, out Composite compRef, out string str) == entity)
                    {
                        List<FunctionEntity> zones = comp.functions.FindAll(o => o.function == CommandsUtils.GetFunctionTypeGUID(FunctionType.Zone));
                        foreach(FunctionEntity z in zones)
                        {
                            foreach(EntityConnector link in z.childLinks)
                            {
                                if (link.parentParamID == compositesGUID && link.childID == trig.shortGUID)
                                {
                                    toReturn = z;
                                }
                            }
                        }
                    }
                }
            }

            return toReturn;
        };

        composite = startComposite;
        zone = findZone(composite);
        if (zone != null) return;

        foreach (Composite comp in Commands.Entries)
        {
            composite = comp;
            zone = findZone(composite);
            if (zone != null) return;
        }
    }
}
